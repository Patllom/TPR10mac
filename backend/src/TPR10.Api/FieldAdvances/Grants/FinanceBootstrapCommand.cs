using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Grants;

public static class FinanceBootstrapCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new ExplicitOffsetDateTimeConverter() }
    };

    // Explicit local external-authorization boundary; never registered in HTTP or startup seeding.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--bootstrap-finance" || !Console.IsInputRedirected)
            return Report(64);
        var connection = Environment.GetEnvironmentVariable("TPR10_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) return Report(64);
        try
        {
            var input = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await Console.In.ReadAsync(buffer)) != 0)
            {
                if (input.Length + count > 65536) return Report(64);
                input.Append(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(input.ToString(), new JsonDocumentOptions { MaxDepth = 16 });
            if (HasDuplicateProperties(document.RootElement)) return Report(64);
            var manifest = document.RootElement.Deserialize<Manifest>(Json);
            if (!Valid(manifest, DateTimeOffset.UtcNow)) return Report(64);
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(connection).Options);
            var correlation = new CorrelationContext();
            correlation.Initialize(Guid.NewGuid());
            return Report(await ApplyAsync(db, new AuditEventWriter(db, correlation, TimeProvider.System), manifest!));
        }
        catch (JsonException) { return Report(64); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never echo the manifest, connection string, database error or personal identifiers.
            return Report(1);
        }
    }

    private static async Task<int> ApplyAsync(Tpr10DbContext db, IAuditEventWriter audit, Manifest manifest)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var committed = false;
        try
        {
            // Same serialization boundary as current identity/organization/grant changes.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var now = DateTimeOffset.UtcNow;
            if (!Valid(manifest, now)) return 2;
            if (await db.Set<FinanceBootstrapReceipt>().AnyAsync(x => x.WorkspaceId == manifest.WorkspaceId)
                || await db.Set<GrantAssignment>().AnyAsync(x => x.WorkspaceId == manifest.WorkspaceId)
                || await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == manifest.CommandId))
                return 2;

            Person[] controls = [manifest.Operator, manifest.Certifier, manifest.Reviewer];
            var identities = controls.Concat(manifest.Grants.Select(x => x.Recipient)).ToArray();
            if (controls.Select(x => x.PersonId).Distinct().Count() != 3
                || controls.Select(x => x.UserId).Distinct().Count() != 3
                || manifest.Grants.Any(g => controls.Any(c => c.PersonId == g.Recipient.PersonId || c.UserId == g.Recipient.UserId))
                || identities.GroupBy(x => x.UserId).Any(g => g.Select(x => x.PersonId).Distinct().Count() != 1))
                return 2;
            var accounts = identities.DistinctBy(x => x.UserId).ToArray();
            if (!await CurrentAsync(db, manifest, accounts, now)) return 2;

            // No inference from account IDs, roles or directory names; the company supplies the stable IDs.
            foreach (var personId in accounts.Select(x => x.PersonId).Distinct())
            {
                var existing = await db.Set<FinancePerson>().SingleOrDefaultAsync(x => x.Id == personId);
                if (existing is not null)
                {
                    if (existing.WorkspaceId != manifest.WorkspaceId || existing.CreatedAtUtc > now) return 2;
                }
                else db.Add(new FinancePerson
                {
                    Id = personId,
                    WorkspaceId = manifest.WorkspaceId,
                    CreatedBy = manifest.Operator.UserId,
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                });
            }
            foreach (var account in accounts)
            {
                var links = await db.Set<FinanceAccountLink>().Where(x =>
                    x.WorkspaceId == manifest.WorkspaceId && x.UserId == account.UserId).ToArrayAsync();
                if (links.Length != 0)
                {
                    // Bootstrap cannot reactivate ended links or rewrite conflicting historical identity.
                    if (links.Any(x => x.PersonId != account.PersonId || x.CreatedAtUtc > now)
                        || links.Count(x => x.EndedAtUtc == null) != 1) return 2;
                }
                else db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = manifest.WorkspaceId,
                    UserId = account.UserId,
                    PersonId = account.PersonId,
                    CreatedBy = manifest.Operator.UserId,
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                });
            }

            var assignments = new List<Guid>();
            foreach (var spec in manifest.Grants)
            {
                var proposal = new GrantProposal
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = manifest.WorkspaceId,
                    ProjectId = spec.ProjectId,
                    RecipientId = spec.Recipient.UserId,
                    RecipientPersonId = spec.Recipient.PersonId,
                    ProposedBy = manifest.Certifier.UserId,
                    ProposedByPersonId = manifest.Certifier.PersonId,
                    State = "Approved",
                    Capabilities = spec.Capabilities,
                    DelegableCapabilities = spec.DelegableCapabilities,
                    ApprovalLimit = spec.ApprovalLimit,
                    StartsAtUtc = spec.StartsAtUtc.ToUniversalTime(),
                    EndsAtUtc = spec.EndsAtUtc?.ToUniversalTime(),
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                };
                var review = new GrantReview
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = manifest.WorkspaceId,
                    ProposalId = proposal.Id,
                    ReviewerId = manifest.Reviewer.UserId,
                    ReviewerPersonId = manifest.Reviewer.PersonId,
                    Outcome = "Approved",
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                };
                var assignment = new GrantAssignment
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = manifest.WorkspaceId,
                    ProjectId = spec.ProjectId,
                    ProposalId = proposal.Id,
                    ReviewId = review.Id,
                    CreatedBy = manifest.Operator.UserId,
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                };
                db.AddRange(proposal, review, assignment);
                assignments.Add(assignment.Id);
                db.Add(new FinanceBootstrapReceipt
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = manifest.WorkspaceId,
                    CommandId = manifest.CommandId,
                    GrantAssignmentId = assignment.Id,
                    CreatedBy = manifest.Operator.UserId,
                    CreatedAtUtc = now,
                    Reason = manifest.Reason.Trim()
                });
                await audit.WriteAsync(new SecurityAuditRequest(manifest.Operator.UserId, null,
                    manifest.WorkspaceId, spec.ProjectId, null, "field-advance.bootstrap.completed",
                    "field-advance-grant", assignment.Id, "success", new Dictionary<string, string>
                    {
                        ["reason"] = manifest.Reason.Trim(),
                        ["evidence-reference"] = manifest.EvidenceReference.Trim(),
                        ["source"] = "local-company-certified-manifest"
                    }), default);
            }
            db.Add(new FinanceCommandReceipt
            {
                CommandId = manifest.CommandId,
                WorkspaceId = manifest.WorkspaceId,
                ActorId = manifest.Operator.UserId,
                Action = "finance.bootstrap",
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, Json))),
                Status = 201,
                CreatedAtUtc = now,
                ResultJson = JsonSerializer.Serialize(new
                {
                    code = "finance-bootstrapped",
                    manifest.Operator,
                    manifest.Certifier,
                    manifest.Reviewer,
                    manifest.EvidenceReference,
                    manifest.Reason,
                    grantAssignmentIds = assignments
                }, Json)
            });
            await db.SaveChangesAsync();
            // Audit/database work may take time. Recheck current eligibility before committing any of it.
            if (!Valid(manifest, DateTimeOffset.UtcNow)
                || !await CurrentAsync(db, manifest, accounts, DateTimeOffset.UtcNow)) return 2;
            var resolver = new FinancePersonResolver(db, TimeProvider.System);
            foreach (var account in accounts)
                if (await resolver.ResolveAsync(account.UserId, manifest.WorkspaceId, default) != account.PersonId) return 2;
            await transaction.CommitAsync();
            committed = true;
            return 0;
        }
        finally
        {
            if (!committed) db.ChangeTracker.Clear();
        }
    }

    private static async Task<bool> CurrentAsync(Tpr10DbContext db, Manifest manifest, Person[] accounts, DateTimeOffset now)
    {
        foreach (var account in accounts)
        {
            if (await DirectoryQueries.Employment(db, now).CountAsync(x =>
                    x.WorkspaceId == manifest.WorkspaceId && x.UserId == account.UserId) != 1
                || !await db.Set<MfaFactor>().AnyAsync(x => x.UserId == account.UserId
                    && x.CreatedAtUtc <= now && x.ConfirmedAtUtc != null && x.ConfirmedAtUtc <= now && x.RevokedAtUtc == null))
                return false;
        }
        foreach (var projectId in manifest.Grants.Select(x => x.ProjectId).Where(x => x != null).Distinct())
            if (!await db.Set<Project>().AnyAsync(x => x.Id == projectId && x.WorkspaceId == manifest.WorkspaceId && x.IsActive))
                return false;
        return true;
    }

    private static bool Valid(Manifest? manifest, DateTimeOffset now) =>
        manifest is not null && manifest.CommandId != Guid.Empty && manifest.WorkspaceId != Guid.Empty
        && ValidPerson(manifest.Operator) && ValidPerson(manifest.Certifier) && ValidPerson(manifest.Reviewer)
        && Text(manifest.Reason) && Text(manifest.EvidenceReference)
        && manifest.Grants is { Length: > 0 and <= 100 }
        && manifest.Grants.All(g => g is not null && ValidPerson(g.Recipient) && g.ProjectId != Guid.Empty
            && g.Capabilities is { Length: > 0 and <= 3 }
            && g.Capabilities.All(c => c is "grant.propose" or "grant.review" or "grant.revoke")
            && g.Capabilities.Distinct().Count() == g.Capabilities.Length
            && g.DelegableCapabilities is { Length: <= 10 }
            && g.DelegableCapabilities.All(FinanceCapabilities.Contains)
            && g.DelegableCapabilities.Distinct().Count() == g.DelegableCapabilities.Length
            && (g.ApprovalLimit is null || g.ApprovalLimit is >= 0 and <= 9999999999999999.99m
                && decimal.Round(g.ApprovalLimit.Value, 2) == g.ApprovalLimit)
            && g.StartsAtUtc != DateTimeOffset.MinValue && g.StartsAtUtc != DateTimeOffset.MaxValue
            && (g.EndsAtUtc is null || g.EndsAtUtc > g.StartsAtUtc && g.EndsAtUtc > now && g.EndsAtUtc != DateTimeOffset.MaxValue))
        && manifest.Grants.Select(g => (g.Recipient.UserId, g.ProjectId)).Distinct().Count() == manifest.Grants.Length;

    private static bool ValidPerson(Person? person) => person is not null && person.UserId != Guid.Empty && person.PersonId != Guid.Empty;
    private static bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 1000 && !text.Any(char.IsControl);
    private static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count()
            || element.EnumerateObject().Any(x => HasDuplicateProperties(x.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        _ => false
    };

    private static int Report(int code)
    {
        var message = code switch
        {
            0 => "ตั้งต้นผู้บริหารสิทธิ์สำเร็จ เก็บหลักฐานการดำเนินการไว้แล้ว",
            2 => "ปฏิเสธการตั้งต้น: บริษัทเคยตั้งต้นหรือมีสิทธิ์แล้ว หรือบัญชี บุคคล และขอบเขตไม่ผ่านเงื่อนไข",
            64 => "ใช้ --bootstrap-finance เพียงอย่างเดียว พร้อม TPR10_CONNECTION_STRING และ manifest JSON ที่ถูกต้องผ่าน stdin แบบ redirect",
            _ => "ตั้งต้นไม่สำเร็จ ตรวจสถานะฐานข้อมูลและหลักฐานก่อนดำเนินการต่อ"
        };
        if (code == 0) Console.WriteLine(message);
        else Console.Error.WriteLine(message);
        return code;
    }

    private sealed class Manifest
    {
        public required Guid CommandId { get; init; }
        public required Guid WorkspaceId { get; init; }
        public required Person Operator { get; init; }
        public required Person Certifier { get; init; }
        public required Person Reviewer { get; init; }
        public required string Reason { get; init; }
        public required string EvidenceReference { get; init; }
        public required BootstrapGrant[] Grants { get; init; }
    }

    private sealed class Person
    {
        public required Guid UserId { get; init; }
        public required Guid PersonId { get; init; }
    }

    private sealed class BootstrapGrant
    {
        public required Person Recipient { get; init; }
        public required Guid? ProjectId { get; init; }
        public required string[] Capabilities { get; init; }
        public required string[] DelegableCapabilities { get; init; }
        public required decimal? ApprovalLimit { get; init; }
        public required DateTimeOffset StartsAtUtc { get; init; }
        public required DateTimeOffset? EndsAtUtc { get; init; }
    }

    private sealed class ExplicitOffsetDateTimeConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var value = reader.GetString()!;
            var explicitOffset = value.EndsWith('Z')
                || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':';
            if (!explicitOffset || !reader.TryGetDateTimeOffset(out var result)) throw new JsonException();
            return result;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
