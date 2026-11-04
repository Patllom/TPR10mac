using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record SettlementWorkItem(Guid WorkspaceId, Guid ProjectId, Guid RequestId, Guid EntityId,
    Guid? RoundId, Guid? RevisionId,
    long EntityVersion, long RequestVersion, long FamilyVersion, string State, string[] AllowedActions,
    SettlementRow? Detail = null);
public sealed record SettlementWorkPage(SettlementWorkItem[] Items, string? NextCursor);
public sealed record SettlementComparisonDocument(Guid RequestId, Guid DocumentId, long Version,
    string Issuer, string? DocumentNumber, bool HasNoNumber, DateOnly IssuedOn, string Amount);
public sealed record SettlementDuplicateComparison(Guid CaseId, long CaseVersion,
    SettlementComparisonDocument Current, SettlementComparisonDocument Other);

public sealed class SettlementWorkService(Tpr10DbContext db, SettlementAccess access, SettlementUiService ui,
    IncidentPaymentBridge bridge, SettlementEvidenceService evidence, IDataProtectionProvider protection)
{
    private sealed record Cursor(Guid Actor, Guid Workspace, Guid Project, string Lane, Guid After);
    internal sealed record WorkEvidence(SettlementRow Detail, SettlementVersion Versions);

    public async Task<IResult> EvidenceAsync(Guid actor, string lane, Guid requestId, Guid entityId,
        Guid evidenceId, HttpContext context, CancellationToken ct)
    {
        if (actor == Guid.Empty || requestId == Guid.Empty || entityId == Guid.Empty || evidenceId == Guid.Empty)
            return SettlementHttp.Error(403, "finance-access-denied");
        db.ChangeTracker.Clear();
        WorkEvidence? projection;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await DuplicateDocumentService.LockRegistryAsync(db, requestId, ct);
            projection = await BoundEvidenceAsync(actor, requestId, lane, entityId, evidenceId, ct);
        }
        if (projection is null) return SettlementHttp.Error(403, "finance-access-denied");
        var row = await db.Set<AdvanceEvidence>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == evidenceId && x.RequestId == requestId, ct);
        if (row is null) return SettlementHttp.Error(403, "finance-access-denied");
        var location = await db.Set<AdvanceEvidenceLocation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.EvidenceId == evidenceId && x.IsCurrent, ct);
        var result = await evidence.ReadWorkAsync(actor, requestId, lane, entityId, evidenceId, this, ct);
        if (result.Value is null) return SettlementHttp.Error(result.Status, result.Code);
        var frozen = JsonSerializer.Serialize(projection, SettlementHttp.Json);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return SettlementHttp.ProtectRoundProof(actor, requestId, result.Value, row.ContentType, evidenceId,
            async token =>
            {
                var current = await BoundEvidenceAsync(actor, requestId, lane, entityId, evidenceId, token);
                if (current is null) return SettlementHttp.ProjectionOutcome.Denied;
                if (!await db.Set<AdvanceEvidence>().AnyAsync(x => x.Id == row.Id && x.RequestId == row.RequestId
                    && x.State == "Ready" && x.Sha256 == row.Sha256 && x.Length == row.Length
                    && x.ContentType == row.ContentType, token)) return SettlementHttp.ProjectionOutcome.Denied;
                if (location is null || !await db.Set<AdvanceEvidenceLocation>().AnyAsync(x => x.Id == location.Id
                    && x.IsCurrent && x.StorageKey == location.StorageKey, token))
                    return SettlementHttp.ProjectionOutcome.Stale;
                return JsonSerializer.Serialize(current, SettlementHttp.Json) == frozen
                    ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
            });
    }

    internal async Task<WorkEvidence?> BoundEvidenceAsync(Guid actor, Guid request, string lane,
        Guid entityId, Guid proofId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Scoped evidence requires a transaction.");
        SettlementRow? detail = lane switch
        {
            "refunds" => await EvidenceRowAsync<Refund>(actor, request, entityId,
                SettlementActions.RefundReview, ct),
            "incidents" => await IncidentEvidenceRowAsync(actor, request, entityId, ct),
            "recoveries" => await EvidenceRowAsync<PaymentIncidentRecovery>(actor, request, entityId,
                SettlementActions.RecoveryReview, ct),
            "corrections" => await EvidenceRowAsync<RefundCorrection>(actor, request, entityId,
                SettlementActions.CorrectionReview, ct),
            "case-payments" => await EvidenceRowAsync<SettlementCasePayment>(actor, request, entityId,
                SettlementActions.CasePaymentReview, ct),
            "excess-payments" => await EvidenceRowAsync<SettlementExcessPayment>(actor, request, entityId,
                SettlementActions.ExcessPaymentReview, ct),
            _ => null
        };
        if (detail is null) return null;
        var ids = detail.Refund?.EvidenceIds ?? detail.Incident?.EvidenceIds ?? detail.Recovery?.EvidenceIds
            ?? detail.Correction?.EvidenceIds ?? detail.Payment?.EvidenceIds ?? [];
        if (!ids.Contains(proofId)) return null;
        var versions = await ui.VersionAsync(request, ct);
        return versions is null ? null : new(detail, versions);
    }

    private async Task<SettlementRow?> IncidentEvidenceRowAsync(Guid actor, Guid request, Guid entityId,
        CancellationToken ct)
    {
        var incident = await db.Set<PaymentIncidentResolution>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == entityId && x.RequestId == request
                && (x.State == "Pending" || x.State == "Accepted"), ct);
        if (incident is null) return null;
        var authorized = incident.State == "Pending"
            ? (await access.CheckAsync(actor, request, SettlementActions.IncidentReview, entityId, ct)).Allowed
            : (await access.CheckAsync(actor, request, SettlementActions.IncidentRelease, entityId, ct)).Allowed
                || (await access.CheckAsync(actor, request, SettlementActions.RecoveryPropose, null, ct)).Allowed
                || (await access.CheckAsync(actor, request, SettlementActions.ShortfallPrepare, null, ct)).Allowed;
        if (!authorized) return null;
        var rows = await ui.DetailsAsync<PaymentIncidentResolution>(actor,
            [new(incident.Id, request, incident.Version, incident.State, incident.Reason)], request, ct);
        return rows is { Length: 1 } ? rows[0] : null;
    }

    private async Task<SettlementRow?> EvidenceRowAsync<T>(Guid actor, Guid request, Guid entityId,
        string action, CancellationToken ct) where T : class
    {
        if (!(await access.CheckAsync(actor, request, action, entityId, ct)).Allowed) return null;
        var row = await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => EF.Property<Guid>(x, "Id") == entityId
            && EF.Property<Guid>(x, "RequestId") == request && EF.Property<string>(x, "State") == "Pending", ct);
        if (row is null) return null;
        var version = (long)db.Entry(row).Property("Version").CurrentValue!;
        var reason = (string)db.Entry(row).Property("Reason").CurrentValue!;
        var rows = await ui.DetailsAsync<T>(actor,
            [new(entityId, request, version, "Pending", reason)], request, ct);
        return rows is { Length: 1 } ? rows[0] : null;
    }

    public async Task<IResult> DuplicateComparisonAsync(Guid actor, Guid requestId, Guid caseId, CancellationToken ct)
    {
        if (requestId == Guid.Empty || caseId == Guid.Empty)
            return SettlementHttp.Error(403, "finance-access-denied");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var comparison = await ComparisonAsync(actor, requestId, caseId, ct);
        if (comparison is null) return SettlementHttp.Error(403, "finance-access-denied");
        var frozen = JsonSerializer.Serialize(comparison, SettlementHttp.Json);
        return SettlementHttp.ProtectWork(actor, Results.Json(comparison, SettlementHttp.Json), async token =>
        {
            var current = await ComparisonAsync(actor, requestId, caseId, token);
            if (current is null) return SettlementHttp.ProjectionOutcome.Denied;
            return JsonSerializer.Serialize(current, SettlementHttp.Json) == frozen
                ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }

    private async Task<SettlementDuplicateComparison?> ComparisonAsync(Guid actor, Guid requestId, Guid caseId,
        CancellationToken ct)
    {
        // DuplicateReview checks both current scopes, the two document versions, and separation.
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.DuplicateReview, caseId, ct)).Allowed)
            return null;
        var row = await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId
            && x.RequestId == requestId && x.State == "Pending", ct);
        if (row is null) return null;
        var documents = await db.Set<ExpenseDocument>().AsNoTracking()
            .Where(x => x.Id == row.DocumentId || x.Id == row.OtherDocumentId).ToArrayAsync(ct);
        var current = documents.SingleOrDefault(x => x.Id == row.DocumentId && x.RequestId == row.RequestId
            && x.WorkspaceId == row.WorkspaceId && x.ProjectId == row.ProjectId && x.FamilyId == row.FamilyId
            && x.Version == row.DocumentVersion);
        var other = documents.SingleOrDefault(x => x.Id == row.OtherDocumentId && x.RequestId == row.OtherRequestId
            && x.WorkspaceId == row.OtherWorkspaceId && x.ProjectId == row.OtherProjectId
            && x.FamilyId == row.OtherFamilyId && x.Version == row.OtherDocumentVersion);
        if (current is null || other is null) return null;
        static SettlementComparisonDocument Document(ExpenseDocument value) => new(value.RequestId,
            value.Id, value.Version, value.Issuer, value.DocumentNumber, value.HasNoNumber, value.IssuedOn,
            value.Amount.ToString("0.00", CultureInfo.InvariantCulture));
        return new(row.Id, row.Version, Document(current), Document(other));
    }
    public async Task<IResult> QueueAsync(Guid actor, Guid workspaceId, Guid projectId, string lane,
        int take, string? cursor, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty || projectId == Guid.Empty || lane is not
            ("rounds" or "refunds" or "incidents" or "recoveries" or "corrections" or "case-payments"
            or "excess-payments" or "completion" or "duplicates" or "excess")
            || take is < 1 or > 50 || cursor?.Length > 2048)
            return SettlementHttp.Error(400, "invalid-page");
        var protector = protection.CreateProtector("TPR10.Settlement.WorkQueue.v1");
        Guid? after = null;
        if (cursor is not null)
        {
            Cursor? value;
            try { value = JsonSerializer.Deserialize<Cursor>(protector.Unprotect(cursor)); }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
            { return SettlementHttp.Error(400, "invalid-cursor"); }
            if (value is null || value.Actor != actor || value.Workspace != workspaceId || value.Project != projectId
                || value.Lane != lane || value.After == Guid.Empty) return SettlementHttp.Error(400, "invalid-cursor");
            after = value.After;
        }
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await BuildAsync(actor, workspaceId, projectId, lane, take, after, protector, ct);
        // Data Protection generates a different ciphertext for the same cursor on each build.
        // Compare the authenticated logical boundary while delivering the original token.
        string StablePage(SettlementWorkPage value) => JsonSerializer.Serialize(
            value with { NextCursor = value.NextCursor is null ? null : protector.Unprotect(value.NextCursor) },
            SettlementHttp.Json);
        var frozen = StablePage(page);
        return SettlementHttp.ProtectWork(actor, Results.Json(page, SettlementHttp.Json), async token =>
        {
            foreach (var item in page.Items)
                foreach (var action in item.AllowedActions)
                    if (!(await access.CheckAsync(actor, item.RequestId, action,
                        action is SettlementActions.RecoveryPropose or SettlementActions.ShortfallPrepare
                            ? null : item.EntityId, token)).Allowed)
                        return SettlementHttp.ProjectionOutcome.Denied;
            var current = await BuildAsync(actor, workspaceId, projectId, lane, take, after, protector, token);
            return StablePage(current) == frozen
                ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }

    private async Task<SettlementWorkPage> BuildAsync(Guid actor, Guid workspace, Guid project, string lane, int take,
        Guid? after, IDataProtector protector, CancellationToken ct)
    {
        if (lane == "refunds") return await RefundsAsync(actor, workspace, project, take, after, protector, ct);
        if (lane == "incidents") return await IncidentsAsync(actor, workspace, project, take, after, protector, ct);
        if (lane == "recoveries") return await EntitiesAsync<PaymentIncidentRecovery>(actor, workspace, project, lane,
            SettlementActions.RecoveryReview, take, after, protector, ct);
        if (lane == "corrections") return await EntitiesAsync<RefundCorrection>(actor, workspace, project, lane,
            SettlementActions.CorrectionReview, take, after, protector, ct);
        if (lane == "case-payments") return await EntitiesAsync<SettlementCasePayment>(actor, workspace, project, lane,
            SettlementActions.CasePaymentReview, take, after, protector, ct);
        if (lane == "excess-payments") return await EntitiesAsync<SettlementExcessPayment>(actor, workspace, project, lane,
            SettlementActions.ExcessPaymentReview, take, after, protector, ct);
        if (lane == "completion") return await EntitiesAsync<WorkCompletion>(actor, workspace, project, lane,
            SettlementActions.CompletionReview, take, after, protector, ct);
        if (lane == "duplicates") return await EntitiesAsync<ExpenseDuplicateCase>(actor, workspace, project, lane,
            SettlementActions.DuplicateReview, take, after, protector, ct);
        if (lane == "excess") return await EntitiesAsync<SettlementExcessCase>(actor, workspace, project, lane,
            SettlementActions.ExcessDisposition, take, after, protector, ct);
        var query = db.Set<SettlementApprovalStep>().AsNoTracking()
            .Where(x => x.WorkspaceId == workspace && x.ProjectId == project && x.State == "Pending"
                && db.Set<AdvanceRequest>().Any(r => r.Id == x.RequestId && r.WorkspaceId == workspace
                    && r.ProjectId == project && r.FamilyId == x.FamilyId));
        if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
        var candidates = await query.OrderBy(x => x.Id).Take(take + 1).ToArrayAsync(ct);
        var items = new List<SettlementWorkItem>();
        foreach (var step in candidates.Take(take))
        {
            var action = step.LevelCode == "Finance" ? SettlementActions.Check : SettlementActions.Approve;
            if (!(await access.CheckAsync(actor, step.RequestId, action, step.Id, ct)).Allowed) continue;
            var revision = await db.Set<SettlementRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == step.RevisionId
                && x.RequestId == step.RequestId && x.WorkspaceId == workspace && x.ProjectId == project, ct);
            if (revision?.State != "Submitted") continue;
            var round = await db.Set<SettlementRound>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.RoundId
                && x.RequestId == step.RequestId && x.WorkspaceId == workspace && x.ProjectId == project, ct);
            if (round?.State != "Submitted") continue;
            var stamp = await (from r in db.Set<AdvanceRequest>().AsNoTracking()
                               join f in db.Set<AdvanceFamily>().AsNoTracking() on r.FamilyId equals f.Id
                               where r.Id == step.RequestId
                               select new SettlementVersion(r.Version, f.Version)).SingleAsync(ct);
            items.Add(new(workspace, project, step.RequestId, step.Id, round.Id, revision.Id, step.Version,
                stamp.RequestVersion, stamp.FamilyVersion, step.State, [action]));
        }
        var scanned = candidates.Take(take).LastOrDefault();
        var next = candidates.Length > take && scanned is not null
            ? protector.Protect(JsonSerializer.Serialize(new Cursor(actor, workspace, project, "rounds", scanned.Id))) : null;
        return new(items.ToArray(), next);
    }

    private async Task<SettlementWorkPage> RefundsAsync(Guid actor, Guid workspace, Guid project, int take,
        Guid? after, IDataProtector protector, CancellationToken ct)
    {
        var query = db.Set<Refund>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.ProjectId == project
            && x.State == "Pending" && db.Set<AdvanceRequest>().Any(r => r.Id == x.RequestId
                && r.WorkspaceId == workspace && r.ProjectId == project && r.FamilyId == x.FamilyId));
        if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
        var candidates = await query.OrderBy(x => x.Id).Take(take + 1).ToArrayAsync(ct);
        var items = new List<SettlementWorkItem>();
        foreach (var refund in candidates.Take(take))
        {
            if (!(await access.CheckAsync(actor, refund.RequestId, SettlementActions.RefundReview, refund.Id, ct)).Allowed) continue;
            var details = await ui.DetailsAsync<Refund>(actor,
                [new(refund.Id, refund.RequestId, refund.Version, refund.State, refund.Reason)], refund.RequestId, ct);
            if (details is not { Length: 1 }) continue;
            var stamp = await (from r in db.Set<AdvanceRequest>().AsNoTracking()
                               join f in db.Set<AdvanceFamily>().AsNoTracking() on r.FamilyId equals f.Id
                               where r.Id == refund.RequestId
                               select new SettlementVersion(r.Version, f.Version)).SingleAsync(ct);
            items.Add(new(workspace, project, refund.RequestId, refund.Id, null, null, refund.Version,
                stamp.RequestVersion, stamp.FamilyVersion, refund.State, [SettlementActions.RefundReview],
                details[0] with { AllowedActions = [SettlementActions.RefundReview] }));
        }
        var scanned = candidates.Take(take).LastOrDefault();
        var next = candidates.Length > take && scanned is not null
            ? protector.Protect(JsonSerializer.Serialize(new Cursor(actor, workspace, project, "refunds", scanned.Id))) : null;
        return new(items.ToArray(), next);
    }

    private async Task<SettlementWorkPage> IncidentsAsync(Guid actor, Guid workspace, Guid project, int take,
        Guid? after, IDataProtector protector, CancellationToken ct)
    {
        var query = db.Set<PaymentIncidentResolution>().AsNoTracking().Where(x => x.WorkspaceId == workspace
            && x.ProjectId == project && (x.State == "Pending" || x.State == "Accepted")
            && db.Set<AdvanceRequest>().Any(r => r.Id == x.RequestId && r.WorkspaceId == workspace
                && r.ProjectId == project && r.FamilyId == x.FamilyId));
        if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
        var candidates = await query.OrderBy(x => x.Id).Take(take + 1).ToArrayAsync(ct);
        var items = new List<SettlementWorkItem>();
        foreach (var incident in candidates.Take(take))
        {
            var actions = new List<string>();
            if (incident.State == "Pending")
            {
                if ((await access.CheckAsync(actor, incident.RequestId, SettlementActions.IncidentReview,
                    incident.Id, ct)).Allowed) actions.Add(SettlementActions.IncidentReview);
            }
            else if (incident.AcceptedAtUtc is not null)
            {
                if ((await access.CheckAsync(actor, incident.RequestId, SettlementActions.IncidentRelease,
                    incident.Id, ct)).Allowed && await bridge.CanReleaseAsync(incident.RequestId, incident.Id, ct))
                    actions.Add(SettlementActions.IncidentRelease);
                if ((await access.CheckAsync(actor, incident.RequestId, SettlementActions.RecoveryPropose,
                    null, ct)).Allowed && (await IncidentAccounting.RecoveryFitsAsync(db, incident,
                    "Owner", 0.01m, null, ct) || await IncidentAccounting.RecoveryFitsAsync(db, incident,
                    "WrongRecipient", 0.01m, null, ct)))
                    actions.Add(SettlementActions.RecoveryPropose);
                if ((await access.CheckAsync(actor, incident.RequestId, SettlementActions.ShortfallPrepare,
                    null, ct)).Allowed && await bridge.CanPrepareShortfallAsync(incident.RequestId,
                    incident.Id, ct)) actions.Add(SettlementActions.ShortfallPrepare);
            }
            if (actions.Count == 0) continue;
            var details = await ui.DetailsAsync<PaymentIncidentResolution>(actor,
                [new(incident.Id, incident.RequestId, incident.Version, incident.State, incident.Reason)], incident.RequestId, ct);
            if (details is not { Length: 1 }) continue;
            var stamp = await (from r in db.Set<AdvanceRequest>().AsNoTracking()
                               join f in db.Set<AdvanceFamily>().AsNoTracking() on r.FamilyId equals f.Id
                               where r.Id == incident.RequestId
                               select new SettlementVersion(r.Version, f.Version)).SingleAsync(ct);
            items.Add(new(workspace, project, incident.RequestId, incident.Id, null, null, incident.Version,
                stamp.RequestVersion, stamp.FamilyVersion, incident.State, actions.ToArray(),
                details[0] with { AllowedActions = actions.ToArray() }));
        }
        var scanned = candidates.Take(take).LastOrDefault();
        var next = candidates.Length > take && scanned is not null
            ? protector.Protect(JsonSerializer.Serialize(new Cursor(actor, workspace, project, "incidents", scanned.Id))) : null;
        return new(items.ToArray(), next);
    }

    private async Task<SettlementWorkPage> EntitiesAsync<T>(Guid actor, Guid workspace, Guid project, string lane,
        string action, int take, Guid? after, IDataProtector protector, CancellationToken ct) where T : class
    {
        var query = db.Set<T>().AsNoTracking().Where(x => EF.Property<Guid>(x, "WorkspaceId") == workspace
            && EF.Property<Guid>(x, "ProjectId") == project && EF.Property<string>(x, "State") == "Pending"
            && db.Set<AdvanceRequest>().Any(r => r.Id == EF.Property<Guid>(x, "RequestId")
                && r.WorkspaceId == workspace && r.ProjectId == project
                && r.FamilyId == EF.Property<Guid>(x, "FamilyId")));
        if (after is { } last) query = query.Where(x => EF.Property<Guid>(x, "Id").CompareTo(last) > 0);
        var candidates = await query.OrderBy(x => EF.Property<Guid>(x, "Id")).Take(take + 1).ToArrayAsync(ct);
        var items = new List<SettlementWorkItem>();
        foreach (var entity in candidates.Take(take))
        {
            var entry = db.Entry(entity);
            var id = (Guid)entry.Property("Id").CurrentValue!;
            var request = (Guid)entry.Property("RequestId").CurrentValue!;
            var version = (long)entry.Property("Version").CurrentValue!;
            var reason = (string)entry.Property("Reason").CurrentValue!;
            if (!(await access.CheckAsync(actor, request, action, id, ct)).Allowed) continue;
            var details = await ui.DetailsAsync<T>(actor, [new(id, request, version, "Pending", reason)], request, ct);
            if (details is not { Length: 1 }) continue;
            var stamp = await (from r in db.Set<AdvanceRequest>().AsNoTracking()
                               join f in db.Set<AdvanceFamily>().AsNoTracking() on r.FamilyId equals f.Id
                               where r.Id == request
                               select new SettlementVersion(r.Version, f.Version)).SingleAsync(ct);
            items.Add(new(workspace, project, request, id, null, null, version, stamp.RequestVersion,
                stamp.FamilyVersion, "Pending", [action], details[0] with { AllowedActions = [action] }));
        }
        var scanned = candidates.Take(take).LastOrDefault();
        var next = candidates.Length > take && scanned is not null
            ? protector.Protect(JsonSerializer.Serialize(new Cursor(actor, workspace, project, lane,
                (Guid)db.Entry(scanned).Property("Id").CurrentValue!))) : null;
        return new(items.ToArray(), next);
    }
}
