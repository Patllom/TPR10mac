using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
namespace TPR10.Api.FieldAdvances.Settlements;

public static class SettlementEndpoints
{
    private static Guid Actor(HttpContext c) => c.RequestServices.GetRequiredService<RequestSession>().Entity?.UserId ?? Guid.Empty;
    public static void MapSettlements(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/requests/{requestId}/settlement");
        Contract(group.MapGet("", (Guid requestId, SettlementUiService s, HttpContext c, CancellationToken ct) => s.SummaryAsync(Actor(c), requestId, ct))).Produces<SettlementSummaryHttp>();
        Contract(group.MapGet("/family", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.FamilyAsync(Actor(c), requestId, ct, take ?? 25, cursor))).Produces<SettlementFamilyPage>();
        Contract(group.MapGet("/duplicates", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<ExpenseDuplicateCase>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/rounds", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<SettlementRound>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/documents", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<ExpenseDocument>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/refunds", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<Refund>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/adjustments", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<SettlementAdjustmentCase>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/incidents", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<PaymentIncidentResolution>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/corrections", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<RefundCorrection>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/case-payments", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<SettlementCasePayment>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/excess", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<SettlementExcessCase>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/excess-payments", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<SettlementExcessPayment>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/recoveries", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<PaymentIncidentRecovery>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/completion", (Guid requestId, int? take, string? cursor, SettlementUiService s, HttpContext c, CancellationToken ct) => s.PageAsync<WorkCompletion>(Actor(c), requestId, take ?? 25, cursor, ct))).Produces<SettlementPage>();
        Contract(group.MapGet("/rounds/{roundId}/revisions/{revisionId}", (Guid requestId, Guid roundId, Guid revisionId, SettlementUiService s, HttpContext c, CancellationToken ct) => s.RoundAsync(Actor(c), requestId, roundId, revisionId, ct))).Produces<SettlementRoundHttp>();
        Contract(group.MapGet("/rounds/{entityId}/preview", Preview)).Produces<SettlementPreviewHttp>();
        Contract(group.MapGet("/rounds/{roundId}/revisions/{revisionId}/evidence/{entityId}", DownloadRound)).Produces<byte[]>(200, "image/png", "image/jpeg", "application/pdf");
        Contract(group.MapGet("/evidence/{entityId}", Download)).Produces<byte[]>(200, "image/png", "image/jpeg", "application/pdf");
        Contract(group.MapGet("/commands/{commandId}/receipt", (Guid requestId, Guid commandId, string operation, Guid? entityId, SettlementUiService s, HttpContext c, CancellationToken ct)
            => s.ReceiptAsync(Actor(c), requestId, commandId, operation, entityId, ct))).Produces<SettlementReceiptHttp>().ProducesProblem(404);
        Upload(group, "/evidence", null);
        Upload(group, "/rounds/{entityId}/evidence", id => new ExpenseEvidenceTarget(id));
        Upload(group, "/refunds/{entityId}/evidence", id => new CorrectionEvidenceTarget(id));
        Upload(group, "/adjustments/{entityId}/evidence", id => new CaseRefundEvidenceTarget(id));
        Upload(group, "/case-payments/{entityId}/evidence", id => new CasePaymentEvidenceTarget(id));
        Upload(group, "/excess-payments/{entityId}/evidence", id => new ExcessPaymentEvidenceTarget(id));
        Upload(group, "/excess/{entityId}/evidence", id => new ExcessDispositionEvidenceTarget(id));
        Upload(group, "/incidents/{entityId}/evidence", id => new IncidentRecoveryEvidenceTarget(id));
        Map<RoundInput, SettlementRound>(group, "/rounds/create", SettlementActions.Draft,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementDraftService>().SaveAsync(a, r, null, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<RoundInput, SettlementRound>(group, "/rounds/{entityId}/update", SettlementActions.Draft,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementDraftService>().SaveAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<SettlementEmpty, SettlementRevision>(group, "/rounds/{entityId}/submit", SettlementActions.Confirm,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementSubmissionService>().SubmitAsync(a, r, e, b.Meta, ct),
            (e, result) => result.EntityId, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<ReviewInput, SettlementApprovalStep>(group, "/rounds/{entityId}/review", SettlementActions.Approve,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementReviewService>().ReviewAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => result.EntityId, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<DocumentInput, ExpenseDocument>(group, "/documents/create", SettlementActions.Document,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseDocumentService>().RegisterAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<DocumentInput, ExpenseDocument>(group, "/documents/{entityId}/update", SettlementActions.Document,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseDocumentService>().UpdateAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<ExpenseDocument>(db, r, e, ct));
        Map<DocumentInput, ExpenseDocument>(group, "/rounds/{entityId}/documents", SettlementActions.ExpensePropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseDocumentService>().RegisterAdjustmentAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<SettlementDuplicate, ExpenseDuplicateCase>(group, "/duplicates/{entityId}/review", SettlementActions.DuplicateReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<DuplicateDocumentService>().ReviewDuplicateAsync(a, r, e, b.Input.SameDocument, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<ExpenseDuplicateCase>(db, r, e, ct));
        Map<SettlementDate, WorkCompletion>(group, "/completion/confirm", SettlementActions.CompletionPropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementCompletionService>().ConfirmEndAsync(a, r, b.Input.ActualEnd, b.Meta, ct),
            (e, result) => null, null);
        Map<SettlementDate, WorkCompletion>(group, "/completion/correct", SettlementActions.CompletionPropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementCompletionService>().ProposeEndCorrectionAsync(a, r, b.Input.ActualEnd, b.Meta, ct),
            (e, result) => null, null);
        Map<SettlementEmpty, WorkCompletion>(group, "/completion/{entityId}/confirm", SettlementActions.CompletionReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementCompletionService>().ConfirmEndCorrectionAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<WorkCompletion>(db, r, e, ct));
        Map<SettlementDeclaration, SubmissionDeclaration>(group, "/declaration", SettlementActions.Declare,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<SettlementCompletionService>().DeclareCompleteAsync(a, r, b.Input.Complete, b.Meta, ct),
            (e, result) => null, null);
        Map<RefundInput, Refund>(group, "/refunds/create", SettlementActions.RefundPropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundService>().ProposeAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<RefundInput, Refund>(group, "/refunds/{entityId}/update", SettlementActions.RefundUpdate,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundService>().UpdateAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<Refund>(db, r, e, ct));
        Map<SettlementEmpty, Refund>(group, "/refunds/{entityId}/confirm", SettlementActions.RefundConfirm,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundService>().ConfirmAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<Refund>(db, r, e, ct));
        Map<SettlementEmpty, Refund>(group, "/refunds/{entityId}/cancel", SettlementActions.RefundCancel,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundService>().CancelAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<Refund>(db, r, e, ct));
        Map<ReviewInput, Refund>(group, "/refunds/{entityId}/review", SettlementActions.RefundReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundReviewService>().ReviewAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<Refund>(db, r, e, ct));
        Map<RefundCorrectionInput, RefundCorrection>(group, "/corrections/create", SettlementActions.CorrectionCreate,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundCorrectionService>().ProposeAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<CasePaymentInput, SettlementCasePayment>(group, "/case-payments/create", SettlementActions.CasePaymentCreate,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().ProposeCasePaymentAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<ExcessPaymentInput, SettlementExcessPayment>(group, "/excess-payments/create", SettlementActions.ExcessPaymentCreate,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().ProposeExternalPaymentAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<SettlementEmpty, SettlementCaseClosure>(group, "/adjustments/{entityId}/close", SettlementActions.CaseClose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().CloseCaseAsync(a, r, e, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<SettlementAdjustmentCase>(db, r, e, ct));
        Map<RoundInput, SettlementRound>(group, "/rounds/{entityId}/adjust", SettlementActions.ExpensePropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseAdjustmentService>().ProposeAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<SettlementTrack, SettlementExcessCase>(group, "/rounds/{entityId}/excess", SettlementActions.ExcessTrack,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().TrackAsync(a, r, e, b.Input.DocumentId, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<SettlementRound>(db, r, e, ct));
        Map<DispositionInput, SettlementExcessCase>(group, "/excess/{entityId}/disposition", SettlementActions.ExcessDisposition,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().RecordDispositionAsync(a, r, e, b.Input, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementExcessCase>(db, r, e, ct));
        Map<IncidentInput, PaymentIncidentResolution>(group, "/incidents/create", SettlementActions.IncidentPropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<PaymentIncidentService>().ProposeAsync(a, r, b.Input, b.Meta, ct),
            (e, result) => null, null);
        Map<SettlementEmpty, PaymentIncidentResolution>(group, "/incidents/{entityId}/confirm", SettlementActions.IncidentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<PaymentIncidentService>().ConfirmAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<PaymentIncidentResolution>(db, r, e, ct));
        Map<SettlementRecovery, PaymentIncidentRecovery>(group, "/incidents/{entityId}/recover", SettlementActions.RecoveryPropose,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<PaymentIncidentService>().RecordRecoveryAsync(a, r, e, b.Input.Amount, b.Input.Source, b.Input.EvidenceIds, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<PaymentIncidentResolution>(db, r, e, ct));
        Map<SettlementEmpty, PaymentIncidentRecovery>(group, "/recoveries/{entityId}/confirm", SettlementActions.RecoveryReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<PaymentIncidentService>().ConfirmRecoveryAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<PaymentIncidentRecovery>(db, r, e, ct));
        Map<SettlementEmpty, PaymentIncidentResolution>(group, "/incidents/{entityId}/release", SettlementActions.IncidentRelease,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<IncidentPaymentBridge>().ReleaseHoldAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<PaymentIncidentResolution>(db, r, e, ct));
        Map<SettlementAmount, AdvanceInstallment>(group, "/incidents/{entityId}/shortfall", SettlementActions.ShortfallPrepare,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<IncidentPaymentBridge>().PrepareShortfallAsync(a, r, e, b.Input.Amount, b.Meta, ct),
            (e, result) => null, (db, r, e, ct) => InScope<PaymentIncidentResolution>(db, r, e, ct));
        Map<SettlementEmpty, RefundCorrection>(group, "/corrections/{entityId}/confirm", SettlementActions.CorrectionReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundCorrectionService>().ConfirmAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<RefundCorrection>(db, r, e, ct));
        Map<SettlementEmpty, RefundCorrection>(group, "/corrections/{entityId}/return", SettlementActions.CorrectionReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundCorrectionService>().ReturnAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<RefundCorrection>(db, r, e, ct));
        Map<SettlementEmpty, RefundCorrection>(group, "/corrections/{entityId}/reject", SettlementActions.CorrectionReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundCorrectionService>().RejectAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<RefundCorrection>(db, r, e, ct));
        Map<SettlementEmpty, RefundCorrection>(group, "/corrections/{entityId}/cancel", SettlementActions.CorrectionCancel,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<RefundCorrectionService>().CancelAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<RefundCorrection>(db, r, e, ct));
        Map<SettlementEmpty, SettlementCasePayment>(group, "/case-payments/{entityId}/confirm", SettlementActions.CasePaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().ConfirmCasePaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementCasePayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementCasePayment>(group, "/case-payments/{entityId}/return", SettlementActions.CasePaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().ReturnCasePaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementCasePayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementCasePayment>(group, "/case-payments/{entityId}/reject", SettlementActions.CasePaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().RejectCasePaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementCasePayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementCasePayment>(group, "/case-payments/{entityId}/cancel", SettlementActions.CasePaymentCancel,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<AdjustmentCaseService>().CancelCasePaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementCasePayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementExcessPayment>(group, "/excess-payments/{entityId}/confirm", SettlementActions.ExcessPaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().ConfirmExternalPaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementExcessPayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementExcessPayment>(group, "/excess-payments/{entityId}/return", SettlementActions.ExcessPaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().ReturnExternalPaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementExcessPayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementExcessPayment>(group, "/excess-payments/{entityId}/reject", SettlementActions.ExcessPaymentReview,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().RejectExternalPaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementExcessPayment>(db, r, e, ct));
        Map<SettlementEmpty, SettlementExcessPayment>(group, "/excess-payments/{entityId}/cancel", SettlementActions.ExcessPaymentCancel,
            (sp, a, r, e, b, ct) => sp.GetRequiredService<ExpenseExcessService>().CancelExternalPaymentAsync(a, r, e, b.Meta, ct),
            (e, result) => e, (db, r, e, ct) => InScope<SettlementExcessPayment>(db, r, e, ct));
    }
    private static void Upload(RouteGroupBuilder group, string path, Func<Guid, AdjustmentEvidenceTarget>? target) =>
        Contract(group.MapPost(path, (Guid requestId, HttpContext c, CancellationToken ct) => Stage(requestId, c, target, ct)))
            .Accepts<byte[]>("image/png", "image/jpeg", "application/pdf").Produces<EvidenceTicket>(201).ProducesProblem(413).ProducesProblem(415).ProducesProblem(422);
    private static async Task<IResult> Stage(Guid requestId, HttpContext c, Func<Guid, AdjustmentEvidenceTarget>? target, CancellationToken ct)
    {
        var sp = c.RequestServices; var service = sp.GetRequiredService<SettlementEvidenceService>();
        var type = c.Request.ContentType ?? "";
        if (type is not ("image/png" or "image/jpeg" or "application/pdf")) return SettlementHttp.Error(415, "evidence-type");
        if (c.Request.ContentLength > sp.GetRequiredService<FinanceEvidenceOptions>().MaxFileBytes) return SettlementHttp.Error(413, "evidence-too-large");
        var actor = Actor(c);
        var e = c.Request.RouteValues.TryGetValue("entityId", out var raw) && Guid.TryParse(raw?.ToString(), out var id) ? id : Guid.Empty;
        var selected = target?.Invoke(e);
        var result = selected is null ? await service.UploadAsync(actor, requestId, c.Request.Body, type, ct)
            : await service.UploadAdjustmentAsync(actor, requestId, selected, c.Request.Body, type, ct);
        if (result.Status != 201) return SettlementHttp.Error(result.Status, result.Code);
        var db = sp.GetRequiredService<Tpr10DbContext>();
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleAsync(x => x.Id == result.Value, ct);
        var ticket = new EvidenceTicket(row.Id, row.Sha256, row.Length, row.ContentType, row.State);
        return SettlementHttp.Protect(actor, requestId, Results.Json(ticket, SettlementHttp.Json, statusCode: 201), async token =>
        {
            if (!(await service.UploadAccessAsync(actor, requestId, selected, token)).Allowed) return SettlementHttp.ProjectionOutcome.Denied;
            return await EvidenceCurrent(db, row, token) ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Denied;
        });
    }
    internal static async Task<IResult> DownloadRound(Guid requestId, Guid roundId, Guid revisionId, Guid entityId, HttpContext c, CancellationToken ct)
    {
        var sp = c.RequestServices; var db = sp.GetRequiredService<Tpr10DbContext>(); var ui = sp.GetRequiredService<SettlementUiService>(); var actor = Actor(c);
        SettlementRoundHttp? projection;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await DuplicateDocumentService.LockRegistryAsync(db, requestId, ct);
            projection = await ui.BoundProofAsync(actor, requestId, roundId, revisionId, entityId, ct);
        }
        if (projection is null) return SettlementHttp.Error(403, "finance-access-denied");
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleAsync(x => x.Id == entityId, ct);
        var location = await db.Set<AdvanceEvidenceLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.EvidenceId == entityId && x.IsCurrent, ct);
        var result = await sp.GetRequiredService<SettlementEvidenceService>().ReadRoundAsync(actor, requestId, roundId, revisionId, entityId, ui, ct);
        if (result.Value is null) return SettlementHttp.Error(result.Status, result.Code);
        var frozen = JsonSerializer.Serialize(projection, SettlementHttp.Json);
        c.Response.Headers.XContentTypeOptions = "nosniff";
        return SettlementHttp.ProtectRoundProof(actor, requestId, result.Value, row.ContentType, entityId, async token =>
        {
            var current = await ui.BoundProofAsync(actor, requestId, roundId, revisionId, entityId, token);
            if (current is null || !await EvidenceCurrent(db, row, token)) return SettlementHttp.ProjectionOutcome.Denied;
            if (location is null || !await db.Set<AdvanceEvidenceLocation>().AnyAsync(x => x.Id == location.Id && x.IsCurrent && x.StorageKey == location.StorageKey, token)) return SettlementHttp.ProjectionOutcome.Stale;
            return JsonSerializer.Serialize(current, SettlementHttp.Json) == frozen ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }
    private static async Task<IResult> Download(Guid requestId, Guid entityId, HttpContext c, CancellationToken ct)
    {
        var db = c.RequestServices.GetRequiredService<Tpr10DbContext>(); var actor = Actor(c);
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == entityId && x.RequestId == requestId, ct);
        if (row is null) return SettlementHttp.Error(403, "finance-access-denied");
        var result = await c.RequestServices.GetRequiredService<SettlementEvidenceService>().ReadAsync(actor, entityId, ct);
        if (result.Value is null) return SettlementHttp.Error(result.Status, result.Code);
        var location = await db.Set<AdvanceEvidenceLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.EvidenceId == entityId && x.IsCurrent, ct);
        c.Response.Headers.XContentTypeOptions = "nosniff";
        return SettlementHttp.Protect(actor, requestId, Results.File(result.Value, row.ContentType, "settlement-evidence-" + entityId.ToString("N"), enableRangeProcessing: false), async token =>
            await EvidenceCurrent(db, row, token) && location is not null && await db.Set<AdvanceEvidenceLocation>().AnyAsync(x => x.Id == location.Id && x.IsCurrent && x.StorageKey == location.StorageKey, token)
                ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Denied);
    }
    private static Task<bool> EvidenceCurrent(Tpr10DbContext db, AdvanceEvidence row, CancellationToken ct) =>
        db.Set<AdvanceEvidence>().AnyAsync(x => x.Id == row.Id && x.RequestId == row.RequestId && x.WorkspaceId == row.WorkspaceId
            && x.ProjectId == row.ProjectId && x.FamilyId == row.FamilyId && x.State == "Ready" && x.Sha256 == row.Sha256 && x.Length == row.Length && x.ContentType == row.ContentType
            && db.Set<AdvanceRequest>().Any(r => r.Id == x.RequestId && r.WorkspaceId == x.WorkspaceId && r.ProjectId == x.ProjectId && r.FamilyId == x.FamilyId), ct);
    private static async Task<IResult> Preview(Guid requestId, Guid entityId, HttpContext c, CancellationToken ct)
    {
        var actor = Actor(c); var sp = c.RequestServices;
        var result = await sp.GetRequiredService<SettlementSubmissionService>().PreviewAsync(actor, requestId, entityId, ct);
        if (result.Value is not { } value) return SettlementHttp.Error(result.Status, result.Code);
        var ui = sp.GetRequiredService<SettlementUiService>();
        return SettlementHttp.Protect(actor, requestId, Results.Json(SettlementUiService.PreviewWire(value), SettlementHttp.Json), token =>
            ui.ValidatePreviewAsync(actor, requestId, value, token), registry: true);
    }
    private static RouteHandlerBuilder Contract(RouteHandlerBuilder route) => route.WithMetadata(new SettlementApiMetadata())
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
    private static RouteHandlerBuilder CommandContract(RouteHandlerBuilder route, string path)
    {
        Contract(route);
        var created = path.EndsWith("/create", StringComparison.Ordinal) || path.EndsWith("/documents", StringComparison.Ordinal)
            || path.EndsWith("/adjust", StringComparison.Ordinal) || path.EndsWith("/excess", StringComparison.Ordinal) || path.EndsWith("/recover", StringComparison.Ordinal) || path.EndsWith("/shortfall", StringComparison.Ordinal);
        return route.Produces<SettlementCommandResult>(created ? 201 : 200);
    }
    private static void Map<T, TEntity>(RouteGroupBuilder group, string path, string action,
        Func<IServiceProvider, Guid, Guid, Guid, SettlementBody<T>, CancellationToken, Task<FinanceResult<SettlementCommandResult>>> run,
        Func<Guid, SettlementCommandResult, Guid?> entity,
        Func<Tpr10DbContext, Guid, Guid, CancellationToken, Task<bool>>? scope) where T : class where TEntity : class =>
        CommandContract(group.MapPost(path, (Guid requestId, HttpContext c, CancellationToken ct) => Command<T, TEntity>(requestId, c, action, run, entity, scope, ct)), path)
            .Accepts<SettlementBody<T>>("application/json").ProducesProblem(413).ProducesProblem(415).ProducesProblem(422);
    private static async Task<bool> InScope<T>(Tpr10DbContext db, Guid request, Guid entity, CancellationToken ct) where T : class =>
        await db.Set<T>().AnyAsync(x => EF.Property<Guid>(x, "Id") == entity && EF.Property<Guid>(x, "RequestId") == request
            && db.Set<AdvanceRequest>().Any(r => r.Id == request && r.FamilyId == EF.Property<Guid>(x, "FamilyId")
                && r.WorkspaceId == EF.Property<Guid>(x, "WorkspaceId") && r.ProjectId == EF.Property<Guid>(x, "ProjectId")), ct);
    private static Task<long?> ResultVersion<TEntity>(Tpr10DbContext db, Guid request, Guid entity, CancellationToken ct) where TEntity : class =>
        db.Set<TEntity>().AsNoTracking().Where(x => EF.Property<Guid>(x, "Id") == entity && EF.Property<Guid>(x, "RequestId") == request
            && db.Set<AdvanceRequest>().Any(r => r.Id == request && r.FamilyId == EF.Property<Guid>(x, "FamilyId")
                && r.WorkspaceId == EF.Property<Guid>(x, "WorkspaceId") && r.ProjectId == EF.Property<Guid>(x, "ProjectId")))
            .Select(x => (long?)EF.Property<long>(x, "Version")).SingleOrDefaultAsync(ct);
    internal static async Task<IResult> Command<T, TEntity>(Guid request, HttpContext c, string action,
        Func<IServiceProvider, Guid, Guid, Guid, SettlementBody<T>, CancellationToken, Task<FinanceResult<SettlementCommandResult>>> run,
        Func<Guid, SettlementCommandResult, Guid?> entity,
        Func<Tpr10DbContext, Guid, Guid, CancellationToken, Task<bool>>? scope, CancellationToken ct) where T : class where TEntity : class
    {
        c.Response.Headers.CacheControl = "private, no-store";
        if (!c.Request.HasJsonContentType()) return SettlementHttp.Error(415, "json-required");
        if (c.Request.ContentLength > 65536) return SettlementHttp.Error(413, "body-too-large");
        SettlementBody<T>? body;
        try
        {
            await using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
            while ((count = await c.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + count > 65536) return SettlementHttp.Error(413, "body-too-large");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
            body = JsonSerializer.Deserialize<SettlementBody<T>>(buffer.ToArray(), SettlementHttp.Json);
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException or InvalidOperationException) { return SettlementHttp.Error(400, "invalid-settlement"); }
        if (body?.Input is null || !SettlementHttp.Valid(body.Meta)) return SettlementHttp.Error(400, "invalid-settlement");
        var actor = Actor(c); var services = c.RequestServices; var db = services.GetRequiredService<Tpr10DbContext>();
        var e = c.Request.RouteValues.TryGetValue("entityId", out var raw) && Guid.TryParse(raw?.ToString(), out var id) ? id : Guid.Empty;
        if (scope is not null && !await scope(db, request, e, ct)) return SettlementHttp.Error(403, "finance-access-denied");
        var result = await run(services, actor, request, e, body, ct);
        if (result.Value is not { } value) return SettlementHttp.Error(result.Status, result.Code);
        if (value.EntityId is not { } resultId) return SettlementHttp.Error(403, "finance-access-denied");
        var target = entity(e, value);
        if (action == SettlementActions.Approve && target is { } step
            && await db.Set<SettlementApprovalStep>().AnyAsync(x => x.Id == step && x.LevelCode == "Finance", ct)) action = SettlementActions.Check;
        if (body.Input is RefundInput { AdjustmentCaseId: not null } && action == SettlementActions.RefundPropose) action = SettlementActions.CaseRefundCreate;
        var ui = services.GetRequiredService<SettlementUiService>();
        var stamp = await ui.VersionAsync(request, ct);
        var resultVersion = await ResultVersion<TEntity>(db, request, resultId, ct);
        if (stamp is null || resultVersion is null) return SettlementHttp.Error(403, "finance-access-denied");
        return SettlementHttp.ProtectReceipt(actor, request, value, result.Status, async token =>
        {
            if (scope is not null && !await scope(db, request, e, token)) return SettlementHttp.ProjectionOutcome.Denied;
            var authorization = await ui.ValidateAsync(actor, request, stamp, token, action, target);
            if (authorization != SettlementHttp.ProjectionOutcome.Current) return authorization;
            var current = await ResultVersion<TEntity>(db, request, resultId, token);
            return current is null ? SettlementHttp.ProjectionOutcome.Denied
                : current == resultVersion ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        }, registry: action == SettlementActions.DuplicateReview);
    }
}
