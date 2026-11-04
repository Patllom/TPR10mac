using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementDraftTests(PostgresFixture postgres)
{
    internal static readonly Guid Category = Guid.Parse("73000000-0000-0000-0000-000000000001");
    internal static readonly Guid FixedCategory = Guid.Parse("73000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Draft_uses_approved_snapshot_rounds_each_line_and_does_not_reserve_or_accept()
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var input = new RoundInput([
            new(null, FixedCategory, "ค่าเดินทาง", new(2026, 10, 30), 0.0001m, null, null, []),
            new(null, FixedCategory, "ค่าเดินทาง", new(2026, 10, 30), 0.0001m, 50m, null, []),
            new(null, Category, "เกินวงเงินบันทึกร่างได้", new(2026, 10, 30), null, null, 2000m, [])]);
        var saved = await Save(f, request, null, input);
        Assert.Equal(201, saved.Status);
        await using var db = f.CreateContext();
        var revision = await db.Set<SettlementRevision>().SingleAsync();
        Assert.Equal(2000.02m, revision.Total);
        Assert.Null(revision.SubmittedAtUtc); Assert.Null(revision.AcceptedAtUtc);
        Assert.All(await db.Set<SettlementLine>().Where(x => x.CategoryId == FixedCategory).ToArrayAsync(), x => { Assert.Equal(50m, x.Rate); Assert.Equal(0.01m, x.Amount); });
        Assert.Empty(await db.Set<ExpenseAllocation>().ToArrayAsync());
        Assert.Equal(1000m, (await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request)).Approved);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("rate")]
    [InlineData("lump")]
    [InlineData("standard")]
    [InlineData("category")]
    [InlineData("initial-source")]
    public async Task Invalid_input_rolls_back_command_and_round(string mode)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var line = Input(10).Lines[0];
        line = mode switch
        {
            "quantity" => line with { Quantity = 1.00001m, Rate = 1m, LumpSum = null },
            "rate" => line with { Quantity = 1m, Rate = 1.001m, LumpSum = null },
            "lump" => line with { LumpSum = 1.001m },
            "standard" => line with { CategoryId = FixedCategory, Quantity = 1, Rate = 49m, LumpSum = null },
            "category" => line with { CategoryId = Guid.NewGuid() },
            _ => line with { SourceLineId = Guid.NewGuid() }
        };
        var result = await Save(f, request, null, new([line]));
        Assert.Equal(422, result.Status);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<SettlementRound>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceCommandReceipt>().Where(x => x.Action.StartsWith("settlement.")).ToArrayAsync());
    }

    [Fact]
    public async Task Edits_preserve_previous_revision_and_only_accept_immediate_same_round_lineage()
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var first = await Save(f, request, null, Input(100)); Assert.Equal(201, first.Status);
        await using var db = f.CreateContext(); var original = await db.Set<SettlementRevision>().SingleAsync();
        var source = await db.Set<SettlementLine>().SingleAsync();
        var edit = Input(200); edit = new([edit.Lines[0] with { SourceLineId = source.Id }]);
        Assert.Equal(200, (await Save(f, request, first.Value!.EntityId, edit)).Status);
        Assert.Equal(100m, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == original.Id)).Total);
        Assert.Equal(422, (await Save(f, request, first.Value.EntityId, edit)).Status);
        var other = await Save(f, request, null, Input(50));
        Assert.Equal(422, (await Save(f, request, other.Value!.EntityId, edit)).Status);
        Assert.Equal(2, await db.Set<SettlementRevision>().CountAsync(x => x.RoundId == first.Value.EntityId));
    }

    [Fact]
    public async Task Replay_checks_current_authority_and_stale_edit_cannot_write()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var meta = await Meta(f, request);
        var input = Input(100); var first = await Save(f, request, null, input, meta);
        Assert.Equal(201, first.Status); Assert.Equal(first, await Save(f, request, null, input, meta));
        Assert.Equal(409, (await Save(f, request, null, Input(101), meta)).Status);
        Assert.Equal(409, (await Save(f, request, first.Value!.EntityId, input, meta with { CommandId = Guid.NewGuid(), EntityVersion = 99 })).Status);
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AuthorId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        Assert.Equal(403, (await Save(f, request, null, input, meta)).Status);
        Assert.Single(await db.Set<SettlementRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Latest_working_request_policy_cannot_override_approved_account_snapshot()
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        await using (var db = f.CreateContext())
        {
            var original = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == request);
            original.IsCurrent = false; await db.SaveChangesAsync();
            var next = new AdvanceRequestRevision
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = request,
                RevisionNumber = 2,
                IsCurrent = true,
                ContentJson = "{}",
                ContentHash = new('c', 64),
                Total = 9000,
                CreatedBy = AuthorId,
                CreatedByPersonId = AuthorPersonId,
                CompanyPolicyRevisionId = CompanyRevisionId,
                PolicySnapshotJson = original.PolicySnapshotJson!.Replace("50", "900"),
                PolicyHash = new('d', 64)
            };
            db.Add(next); await db.SaveChangesAsync();
        }
        var input = new RoundInput([new(null, FixedCategory, "อัตราฉบับอนุมัติ", new(2026, 10, 30), 2m, null, null, [])]);
        Assert.Equal(201, (await Save(f, request, null, input)).Status);
        await using var verify = f.CreateContext(); var line = await verify.Set<SettlementLine>().SingleAsync();
        Assert.Equal(50m, line.Rate); Assert.Equal(100m, line.Amount);
    }

    [Fact]
    public async Task Delegate_can_save_but_cannot_reserve_and_pending_round_is_not_editable()
    {
        await using var f = await Ready(postgres); var request = await Approved(f, RecipientPersonId); var doc = await Document(f, request);
        await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        await using var db = f.CreateContext();
        var executor = await Executor(f, db, RecipientId);
        var service = new SettlementDraftService(db, await Access(f, db, RecipientId), executor, new(db, executor), f.Clock);
        var saved = await service.SaveAsync(RecipientId, request, null, Input(100, doc), await Meta(f, request), default);
        Assert.Equal(201, saved.Status); var round = saved.Value!.EntityId!.Value;
        var revision = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync();
        var denied = await executor.ExecuteAsync(RecipientId, request, SettlementActions.Confirm, revision.Id, await Meta(f, request, 1),
            SettlementCommandExecutor.Hash(new { operation = "test-delegate-confirm", round }),
            _ => throw new InvalidOperationException("Delegate must not reach mutation"), default);
        Assert.Equal(403, denied.Status);
        Assert.Equal(200, (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Status);
        Assert.Equal(409, (await Save(f, request, round, Input(50, doc))).Status);
        Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    [Theory]
    [InlineData("foreign-document")]
    [InlineData("allocation-precision")]
    [InlineData("allocation-exceeds-line")]
    [InlineData("duplicate-document")]
    [InlineData("cross-request-lineage")]
    public async Task Invalid_document_allocations_and_foreign_lineage_leave_no_new_revision(string mode)
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var other = await Approved(f);
        var doc = await Document(f, mode == "foreign-document" ? other : request);
        var initial = await Save(f, request, null, Input(100)); var round = initial.Value!.EntityId;
        var line = Input(100, doc).Lines[0];
        if (mode == "allocation-precision") line = line with { Allocations = [new(doc, 99.999m)] };
        if (mode == "allocation-exceeds-line") line = line with { Allocations = [new(doc, 101m)] };
        if (mode == "duplicate-document") line = line with { Allocations = [new(doc, 50m), new(doc, 50m)] };
        if (mode == "cross-request-lineage")
        {
            await Save(f, other, null, Input(100)); await using var context = f.CreateContext();
            line = line with { SourceLineId = (await context.Set<SettlementLine>().SingleAsync(x => x.RequestId == other)).Id };
        }
        Assert.Equal(422, (await Save(f, request, round, new([line]))).Status);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<SettlementRevision>().Where(x => x.RequestId == request).ToArrayAsync());
    }

    [Fact]
    public void Legacy_null_document_input_keeps_exact_json_hash_shape_and_old_snapshot_reads()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var input = new DocumentInput("shop", "01", false, new(2026, 10, 30), 100m, [id]);
        const string oldJson = """{"Issuer":"shop","Number":"01","HasNoNumber":false,"IssuedOn":"2026-10-30","Amount":100,"EvidenceIds":["11111111-1111-1111-1111-111111111111"]}""";
        Assert.Equal(oldJson, JsonSerializer.Serialize(input));
        Assert.Equal(oldJson, JsonSerializer.Serialize(input with { EvidenceType = null }));
        var legacyPayload = "{\"operation\":\"register-document\",\"documentId\":null,\"input\":" + oldJson + "}";
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyPayload)));
        Assert.Equal(expectedHash, SettlementCommandExecutor.Hash(new { operation = "register-document", documentId = (Guid?)null, input }));
        var oldSnapshot = JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>("""{"EvidenceIds":["11111111-1111-1111-1111-111111111111"],"Hashes":["legacy-hash"]}""")!;
        Assert.Null(oldSnapshot.EvidenceType); Assert.Equal([id], oldSnapshot.EvidenceIds); Assert.Equal(["legacy-hash"], oldSnapshot.Hashes);
        Assert.DoesNotContain("EvidenceType", JsonSerializer.Serialize(oldSnapshot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("oversized")]
    public async Task Explicit_document_kind_must_be_nonempty_and_bounded(string kind)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var input = await FieldAdvanceExpenseDocumentTests.Input(f, files, request);
        await using var db = f.CreateContext(); var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var invalid = input with { EvidenceType = kind == "oversized" ? new string('x', 201) : kind };
        Assert.Equal(422, (await docs.RegisterAsync(AuthorId, request, invalid, await Meta(f, request), default)).Status);
        Assert.Empty(await db.Set<ExpenseDocument>().ToArrayAsync());
    }

    [Fact]
    public async Task Document_kind_roundtrips_trimmed_and_relabel_invalidates_duplicate_review_without_changing_fingerprint()
    {
        await using var f = await Ready(postgres); var first = await Approved(f); var second = await Approved(f);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var a = (await FieldAdvanceExpenseDocumentTests.Input(f, files, first)) with { EvidenceType = "receipt" };
        var b = (await FieldAdvanceExpenseDocumentTests.Input(f, files, second)) with { EvidenceType = " certificate " };
        await using var db = f.CreateContext(); var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        Assert.Equal(201, (await docs.RegisterAsync(AuthorId, first, a, await Meta(f, first), default)).Status);
        var command = await Meta(f, second); var created = await docs.RegisterAsync(AuthorId, second, b, command, default);
        Assert.Equal(201, created.Status); Assert.Equal("PendingDuplicate", created.Value!.State);
        var row = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.RequestId == second);
        Assert.Equal("certificate", JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(row.SnapshotJson)!.EvidenceType);
        Assert.Equal(created, await docs.RegisterAsync(AuthorId, second, b, command, default));
        Assert.Equal(409, (await docs.RegisterAsync(AuthorId, second, b with { EvidenceType = "receipt" }, command, default)).Status);
        var pair = await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync();
        var review = new DuplicateDocumentService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId), f.Clock);
        Assert.Equal(200, (await review.ReviewDuplicateAsync(RecipientId, second, pair.Id, false, await Meta(f, second, 1), default)).Status);
        var status = new DuplicateDocumentService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock);
        Assert.False(await status.IsBlockedAsync(AuthorId, second, row.Id, default));
        var changed = await docs.UpdateAsync(AuthorId, second, row.Id, b with { EvidenceType = "ใบรับรองค่าใช้จ่าย" }, await Meta(f, second, 1), default);
        Assert.Equal(200, changed.Status); Assert.Equal(2, changed.Value!.EntityVersion); Assert.Equal("PendingDuplicate", changed.Value.State);
        var after = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Fingerprint, after.Fingerprint);
        Assert.Equal("ใบรับรองค่าใช้จ่าย", JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(after.SnapshotJson)!.EvidenceType);
        Assert.True(await db.Set<ExpenseDuplicateCase>().AnyAsync(x => x.DocumentId == row.Id && x.DocumentVersion == 2 && x.State == "Pending"));
        Assert.True(await status.IsBlockedAsync(AuthorId, second, row.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_kind_document_can_be_labelled_while_all_references_are_unsubmitted_drafts(bool removedFromLatest)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var input = await FieldAdvanceExpenseDocumentTests.Input(f, files, request);
        await using var db = f.CreateContext(); var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var created = await docs.RegisterAsync(AuthorId, request, input, await Meta(f, request), default);
        Assert.Equal(201, created.Status); var doc = created.Value!.EntityId!.Value;
        var draft = await Save(f, request, null, Input(100, doc)); var round = draft.Value!.EntityId!.Value;
        Assert.Equal(409, (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Status);
        if (removedFromLatest) Assert.Equal(200, (await Save(f, request, round, new([]))).Status);
        var updated = await docs.UpdateAsync(AuthorId, request, doc, input with { EvidenceType = "receipt" }, await Meta(f, request, 1), default);
        Assert.Equal(200, updated.Status); Assert.Equal(2, updated.Value!.EntityVersion);
        if (removedFromLatest) Assert.Equal(200, (await Save(f, request, round, Input(100, doc))).Status);
        Assert.Equal(200, (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Status);
        Assert.Equal(100m, await FieldAdvanceExpenseAllocationTests.Reserved(db, doc));
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("submitted")]
    [InlineData("confirmed")]
    [InlineData("returned")]
    [InlineData("released")]
    [InlineData("allocation-accepted")]
    [InlineData("revision-accepted")]
    public async Task Any_protected_reference_blocks_document_update_even_with_other_draft_references(string mode)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var input = (await FieldAdvanceExpenseDocumentTests.Input(f, files, request)) with { EvidenceType = "receipt" };
        await using var db = f.CreateContext(); var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var created = await docs.RegisterAsync(AuthorId, request, input, await Meta(f, request), default); var doc = created.Value!.EntityId!.Value;
        var first = await Save(f, request, null, Input(100, doc)); await Save(f, request, null, Input(10, doc));
        var revision = await db.Set<SettlementRevision>().SingleAsync(x => x.RoundId == first.Value!.EntityId);
        var allocation = await db.Set<ExpenseAllocation>().SingleAsync(x => x.RevisionId == revision.Id);
        switch (mode)
        {
            case "reserved": allocation.State = "Reserved"; break;
            case "released": allocation.State = "Released"; break;
            case "submitted": revision.SubmittedAtUtc = f.Clock.GetUtcNow(); break;
            case "confirmed": revision.ConfirmedAtUtc = f.Clock.GetUtcNow(); revision.ConfirmedBy = AuthorId; revision.ConfirmedByPersonId = AuthorPersonId; break;
            case "returned": revision.State = "Returned"; break;
            case "allocation-accepted":
                allocation.State = "Accepted"; allocation.AcceptedAtUtc = f.Clock.GetUtcNow(); allocation.ReviewedAtUtc = f.Clock.GetUtcNow();
                allocation.ReviewedBy = ReviewerId; allocation.ReviewedByPersonId = ReviewerPersonId; break;
            case "revision-accepted":
                revision.State = "Accepted"; revision.AcceptedAtUtc = f.Clock.GetUtcNow(); revision.ReviewedAtUtc = f.Clock.GetUtcNow();
                revision.ReviewedBy = ReviewerId; revision.ReviewedByPersonId = ReviewerPersonId; break;
        }
        await db.SaveChangesAsync();
        var result = await docs.UpdateAsync(AuthorId, request, doc, input with { EvidenceType = "certificate" }, await Meta(f, request, 1), default);
        Assert.Equal(409, result.Status); Assert.Equal("document-in-use", result.Code);
        Assert.Equal(1, (await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == doc)).Version);
    }

    [Fact]
    public async Task Draft_document_updates_are_rechecked_for_kind_and_budget_before_reserving()
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var input = (await FieldAdvanceExpenseDocumentTests.Input(f, files, request)) with { EvidenceType = "receipt" };
        await using var db = f.CreateContext(); var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var created = await docs.RegisterAsync(AuthorId, request, input, await Meta(f, request), default); var doc = created.Value!.EntityId!.Value;
        var draft = await Save(f, request, null, Input(100, doc)); var round = draft.Value!.EntityId!.Value;
        Assert.Equal(200, (await docs.UpdateAsync(AuthorId, request, doc, input with { Amount = 99m }, await Meta(f, request, 1), default)).Status);
        Assert.Equal("document-budget-exceeded", (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Code);
        Assert.Equal(200, (await docs.UpdateAsync(AuthorId, request, doc, input with { EvidenceType = "wrong-kind" }, await Meta(f, request, 2), default)).Status);
        Assert.Equal(409, (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Status);
        Assert.Equal(200, (await docs.UpdateAsync(AuthorId, request, doc, input, await Meta(f, request, 3), default)).Status);
        Assert.Equal(200, (await FieldAdvanceExpenseAllocationTests.Reserve(f, request, round, [new(doc, 100)])).Status);
    }

    internal static RoundInput Input(decimal amount, Guid? document = null) => new([
        new(null, Category, "ค่าใช้จ่ายจำลอง", new(2026, 10, 30), null, null, amount,
            document is { } id ? [new(id, amount)] : [])]);

    internal static async Task<Guid> Approved(FieldAdvanceFixture f, Guid? delegatePerson = null, string[]? evidenceTypes = null, string[]? fixedEvidenceTypes = null)
    {
        if (!await HasGrant(f)) await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var request = await Request(f, delegatePerson: delegatePerson);
        await using var db = f.CreateContext();
        var policy = new PolicySnapshot(CompanyRevisionId, null, new(7, [], [
            new(Category, "ค่าใช้จ่าย", true, null, null, evidenceTypes ?? ["receipt"]),
            new(FixedCategory, "อัตราคงที่", true, "หน่วย", 50m, fixedEvidenceTypes ?? ["receipt"])]), new('b', 64));
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request,
            RevisionNumber = 1,
            IsCurrent = true,
            ContentJson = "{}",
            ContentHash = new('a', 64),
            Total = 1000,
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            PolicySnapshotJson = JsonSerializer.Serialize(policy),
            PolicyHash = new('b', 64)
        };
        db.Add(revision); await db.SaveChangesAsync();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        account.ApprovedRevisionId = revision.Id; account.Approved = 1000; account.Spendable = 1000;
        await db.SaveChangesAsync(); return request;
    }
    private static async Task<bool> HasGrant(FieldAdvanceFixture f)
    { await using var db = f.CreateContext(); return await db.Set<GrantProposal>().AnyAsync(x => x.RecipientId == AuthorId); }

    internal static async Task<FinanceResult<SettlementCommandResult>> Save(FieldAdvanceFixture f, Guid request, Guid? round, RoundInput input, SettlementCommand? command = null)
    {
        await using var db = f.CreateContext(); var executor = await Executor(f, db, AuthorId);
        var version = round is { } id ? (long?)(await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == id)).Version : null;
        return await new SettlementDraftService(db, await Access(f, db, AuthorId), executor, new(db, executor), f.Clock)
            .SaveAsync(AuthorId, request, round, input, command ?? await Meta(f, request, version), default);
    }

    internal static async Task<Guid> Document(FieldAdvanceFixture f, Guid request, decimal amount = 1000, string? evidenceType = "receipt")
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var evidence = new AdvanceEvidence
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            Sha256 = new('a', 64),
            Length = 10,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(evidence); await db.SaveChangesAsync();
        var doc = await Row<ExpenseDocument>(f, db, r); doc.EvidenceId = evidence.Id; doc.Issuer = "ร้านจำลอง";
        doc.HasNoNumber = true; doc.IssuedOn = new(2026, 10, 30); doc.Amount = amount; doc.State = "Ready";
        doc.SnapshotJson = JsonSerializer.Serialize(new ExpenseDocumentService.DocumentEvidence([evidence.Id], [evidence.Sha256]) { EvidenceType = evidenceType });
        db.Add(doc); await db.SaveChangesAsync(); return doc.Id;
    }
}
