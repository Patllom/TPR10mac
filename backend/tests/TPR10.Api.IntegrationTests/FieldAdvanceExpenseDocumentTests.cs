using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementEvidenceTests;
namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceExpenseDocumentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Registration_returns_owned_created_entity_and_binds_complete_payload_and_current_authority()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        var grant = await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        var input = await Input(f, files, request); var command = await Meta(f, request);
        await using var db = f.CreateContext(); var service = await Documents(f, db, files);
        var result = await service.RegisterAsync(AuthorId, request, input, command, default);
        Assert.Equal(201, result.Status); Assert.NotNull(result.Value!.EntityId); Assert.Equal(1, result.Value.EntityVersion);
        var row = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(result.Value.EntityId, row.Id); Assert.Equal(command.CommandId, row.CommandId);
        Assert.Equal(request, row.RequestId); Assert.Equal(123.45m, row.Amount);
        Assert.Equal(result, await service.RegisterAsync(AuthorId, request, input, command, default));
        foreach (var changed in new[] { input with { Issuer = "อื่น" }, input with { Number = "02" }, input with { HasNoNumber = true, Number = null }, input with { IssuedOn = input.IssuedOn.AddDays(1) }, input with { Amount = 124m } })
            Assert.Equal(409, (await service.RegisterAsync(AuthorId, request, changed, command, default)).Status);
        var anotherFile = await Input(f, files, request);
        Assert.Equal(409, (await service.RegisterAsync(AuthorId, request, input with { EvidenceIds = anotherFile.EvidenceIds }, command, default)).Status);
        await db.Set<GrantAssignment>().Where(x => x.Id == grant).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        Assert.Equal(403, (await service.RegisterAsync(AuthorId, request, input, command, default)).Status);
        Assert.Single(await db.Set<ExpenseDocument>().ToArrayAsync());
    }
    [Fact]
    public async Task Invalid_metadata_and_foreign_or_duplicate_evidence_are_rejected()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        var input = await Input(f, files, request); var foreign = await Input(f, files, await Request(f));
        await using var db = f.CreateContext(); var service = await Documents(f, db, files);
        foreach (var invalid in new[] { input with { Issuer = " " }, input with { Number = null }, input with { HasNoNumber = true }, input with { Amount = 1.001m }, input with { Amount = 0m }, input with { EvidenceIds = [] }, input with { EvidenceIds = [input.EvidenceIds[0], input.EvidenceIds[0]] }, input with { EvidenceIds = foreign.EvidenceIds } })
            Assert.Equal(422, (await service.RegisterAsync(AuthorId, request, invalid, await Meta(f, request), default)).Status);
        var noNumber = input with { HasNoNumber = true, Number = null };
        Assert.Equal(201, (await service.RegisterAsync(AuthorId, request, noNumber, await Meta(f, request), default)).Status);
        Assert.True((await db.Set<ExpenseDocument>().SingleAsync()).HasNoNumber);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_review_requires_both_scopes_and_SoD_and_metadata_change_invalidates_review(bool same)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var first = await Request(f); var second = await Request(f);
        var a = await Input(f, files, first); var b = await Input(f, files, second);
        await using var db = f.CreateContext(); var docs = await Documents(f, db, files);
        Assert.Equal(201, (await docs.RegisterAsync(AuthorId, first, a, await Meta(f, first), default)).Status);
        var created = await docs.RegisterAsync(AuthorId, second, b, await Meta(f, second), default);
        Assert.Equal(201, created.Status); Assert.Equal("PendingDuplicate", created.Value!.State);
        Assert.DoesNotContain(first.ToString(), JsonSerializer.Serialize(created));
        var duplicate = await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync();
        var review = new DuplicateDocumentService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId), f.Clock);
        var meta = await Meta(f, second, duplicate.Version);
        var result = await review.ReviewDuplicateAsync(RecipientId, second, duplicate.Id, same, meta, default);
        Assert.Equal(200, result.Status);
        Assert.Equal(same ? "SameDocument" : "DistinctDocument", result.Value!.State);
        Assert.Equal(result, await review.ReviewDuplicateAsync(RecipientId, second, duplicate.Id, same, meta, default));
        Assert.Equal(409, (await review.ReviewDuplicateAsync(RecipientId, second, duplicate.Id, !same, meta, default)).Status);
        Assert.DoesNotContain(first.ToString(), JsonSerializer.Serialize(result));
        var evidence = await Service(f, db, files);
        var replacement = await evidence.UploadAsync(AuthorId, second, new MemoryStream(Image(SkiaSharp.SKEncodedImageFormat.Jpeg)), "image/jpeg", default);
        Assert.Equal(201, replacement.Status);
        var changed = await docs.UpdateAsync(AuthorId, second, created.Value.EntityId!.Value,
            b with { Amount = 222m, Issuer = "เปลี่ยนร้าน", Number = "เปลี่ยนเลข", EvidenceIds = [replacement.Value] }, await Meta(f, second, 1), default);
        Assert.Equal(200, changed.Status); Assert.Equal("PendingDuplicate", changed.Value!.State);
        Assert.NotEqual(200, (await review.ReviewDuplicateAsync(RecipientId, second, duplicate.Id, same, meta, default)).Status);
        Assert.Equal(403, (await review.ReviewDuplicateAsync(RecipientId, first, duplicate.Id, same, meta, default)).Status);
        var own = new DuplicateDocumentService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock);
        await Grant(f, AuthorId, AuthorPersonId, "finance.check");
        Assert.Equal(403, (await own.ReviewDuplicateAsync(AuthorId, second, duplicate.Id, false, await Meta(f, second, 1), default)).Status);
        var cases = await db.Set<ExpenseDuplicateCase>().AsNoTracking().ToArrayAsync();
        Assert.Contains(cases, x => x.State == "Pending" && x.DocumentVersion == 2);
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("Payer")]
    [InlineData("Approver")]
    [InlineData("Delegate")]
    public async Task Other_project_authority_and_family_history_are_required_before_review_and_replay(string mode)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        var project2 = Guid.Parse("71000000-0000-0000-0000-000000000012");
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, AuthorId, AuthorPersonId, "request.create", project2);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var first = await Request(f); Guid other;
        await using (var seed = f.CreateContext())
        {
            var r = new AdvanceRequest
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = project2,
                OwnerPersonId = AuthorPersonId,
                CreatedBy = AuthorId,
                State = "Approved",
                DelegatePersonId = mode == "Delegate" ? RecipientPersonId : null
            };
            seed.Add(r); await seed.SaveChangesAsync(); other = r.Id;
            if (mode is "Payer" or "Approver")
            {
                seed.Add(new AdvancePaymentParticipation
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = project2,
                    FamilyId = r.FamilyId,
                    PersonId = RecipientPersonId,
                    Role = mode,
                    CreatedBy = RecipientId,
                    CreatedAtUtc = f.Clock.GetUtcNow()
                });
                await seed.SaveChangesAsync();
            }
        }
        await using var db = f.CreateContext(); var docs = await Documents(f, db, files);
        Assert.Equal(201, (await docs.RegisterAsync(AuthorId, other, await Input(f, files, other), await Meta(f, other), default)).Status);
        Assert.Equal(201, (await docs.RegisterAsync(AuthorId, first, await Input(f, files, first), await Meta(f, first), default)).Status);
        var duplicate = await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync(); var meta = await Meta(f, first, 1);
        var review = new DuplicateDocumentService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId), f.Clock);
        Assert.Equal(403, (await review.ReviewDuplicateAsync(RecipientId, first, duplicate.Id, false, meta, default)).Status);
        var otherGrant = await Grant(f, RecipientId, RecipientPersonId, "finance.check", project2);
        var result = await review.ReviewDuplicateAsync(RecipientId, first, duplicate.Id, false, meta, default);
        Assert.Equal(mode == "scope" ? 200 : 403, result.Status);
        Assert.DoesNotContain(other.ToString(), JsonSerializer.Serialize(result));
        if (mode == "scope")
        {
            var history = await db.Set<AdvancePaymentParticipation>().Where(x => x.PersonId == RecipientPersonId && x.Role == "Checker").ToArrayAsync();
            Assert.Equal(2, history.Length);
            await db.Set<GrantAssignment>().Where(x => x.Id == otherGrant).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "ถอนสิทธิ์อีกโครงการ"));
            Assert.Equal(403, (await review.ReviewDuplicateAsync(RecipientId, first, duplicate.Id, false, meta, default)).Status);
        }
        else Assert.Empty(await db.Set<AdvancePaymentParticipation>().Where(x => x.Role == "Checker").ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hash_or_metadata_match_blocks_both_sides_and_one_command_can_create_multiple_cases(bool sameHash)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var requests = new[] { await Request(f), await Request(f), await Request(f) };
        await using var db = f.CreateContext(); var docs = await Documents(f, db, files);
        var ids = new List<Guid>();
        for (var i = 0; i < requests.Length; i++)
        {
            var input = (await Input(f, files, requests[i])) with { Amount = 123.4m };
            if (sameHash) input = input with { Number = "ใบ" + i, Amount = 100m + i };
            else if (i == 1)
            {
                var evidence = await Service(f, db, files);
                var jpeg = await evidence.UploadAsync(AuthorId, requests[i], new MemoryStream(Image(SkiaSharp.SKEncodedImageFormat.Jpeg)), "image/jpeg", default);
                Assert.Equal(201, jpeg.Status); input = input with { EvidenceIds = [jpeg.Value], Amount = 123.40m };
            }
            var result = await docs.RegisterAsync(AuthorId, requests[i], input, await Meta(f, requests[i]), default);
            Assert.Equal(201, result.Status); ids.Add(result.Value!.EntityId!.Value);
        }
        Assert.Equal(3, await db.Set<ExpenseDuplicateCase>().CountAsync());
        var duplicates = new DuplicateDocumentService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock);
        for (var i = 0; i < requests.Length; i++) Assert.True(await duplicates.IsBlockedAsync(AuthorId, requests[i], ids[i], default));
    }

    [Fact]
    public async Task Concurrent_cross_family_registration_serializes_registry_and_detects_collision()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var a = await Request(f); var b = await Request(f); var ia = await Input(f, files, a); var ib = await Input(f, files, b);
        await using var dba = f.CreateContext(); await using var dbb = f.CreateContext();
        var sa = await Documents(f, dba, files); var sb = await Documents(f, dbb, files);
        var ma = await Meta(f, a); var mb = await Meta(f, b);
        var results = await Task.WhenAll(sa.RegisterAsync(AuthorId, a, ia, ma, default), sb.RegisterAsync(AuthorId, b, ib, mb, default)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, x => Assert.Equal(201, x.Status));
        await using var verify = f.CreateContext(); Assert.Single(await verify.Set<ExpenseDuplicateCase>().ToArrayAsync());
        Assert.Equal(2, await verify.Set<ExpenseDocument>().CountAsync());
        Assert.Equal(results[0], await sa.RegisterAsync(AuthorId, a, ia, ma, default));
    }

    [Fact]
    public async Task Document_registry_ownership_does_not_block_preauth_csrf_advisory_lock()
    {
        await using var f = await Ready(postgres); var request = await Request(f);
        await using var registry = f.CreateContext(); await using var registryTx = await registry.Database.BeginTransactionAsync();
        // Exercise the production registry path, not a duplicate test-only lock constant.
        await DuplicateDocumentService.LockRegistryAsync(registry, request, default);
        await using var csrf = f.CreateContext(); await using var csrfTx = await csrf.Database.BeginTransactionAsync();
        // CsrfService.IssueAsync uses this one-argument key. A separate connection must
        // acquire it while the document transaction still owns its registry lock.
        var available = await csrf.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241003) AS \"Value\"").SingleAsync();
        Assert.True(available, "Document registry ownership must not block pre-authentication CSRF issuance");
    }

    [Fact]
    public async Task Registry_wait_does_not_acquire_family_lock_first()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f); var meta = await Meta(f, request);
        await using var holder = f.CreateContext(); await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241005)");
        await using var worker = f.CreateContext(); await worker.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)worker.Database.GetDbConnection()).ProcessID;
        var executor = await Executor(f, worker, AuthorId);
        var pending = executor.ExecuteAsync(AuthorId, request, SettlementActions.Document, meta, new string('a', 64),
            _ => Task.FromResult(new TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>(200, "ok", new(request, null, "Ready", 0, 0, null))), default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var probe = f.CreateContext();
            while (await probe.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(timeout.Token) == 0)
            { Assert.False(pending.IsCompleted); await Task.Delay(20, timeout.Token); }
            await using var probeTx = await probe.Database.BeginTransactionAsync();
            var family = await probe.Set<AdvanceRequest>().Where(x => x.Id == request).Select(x => x.FamilyId).SingleAsync();
            await probe.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM field_advance_families WHERE id={family} FOR UPDATE NOWAIT");
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(200, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task Registration_rechecks_prepared_hashes_after_last_file_IO()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        var first = await Input(f, files, request); var second = await Input(f, files, request);
        var input = first with { EvidenceIds = [first.EvidenceIds[0], second.EvidenceIds[0]] };
        await using var db = f.CreateContext(); var reads = 0;
        var store = new ObservedStore(files.Store, async () =>
        {
            if (++reads != 2) return;
            await using var changed = f.CreateContext();
            await changed.Set<AdvanceEvidence>().Where(x => x.Id == first.EvidenceIds[0]).ExecuteUpdateAsync(x => x.SetProperty(y => y.Sha256, new string('a', 64)));
        }, () => Assert.Null(db.Database.CurrentTransaction));
        var docs = new ExpenseDocumentService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), await Service(f, db, files, store: store), f.Clock);
        var result = await docs.RegisterAsync(AuthorId, request, input, await Meta(f, request), default);
        Assert.Equal(422, result.Status); Assert.Empty(await db.Set<ExpenseDocument>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    [Fact]
    public async Task Duplicate_review_rechecks_other_scope_before_commit_and_rolls_back_both_histories()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        var project2 = Guid.Parse("71000000-0000-0000-0000-000000000012");
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); await Grant(f, AuthorId, AuthorPersonId, "request.create", project2);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check"); var grant = await Grant(f, RecipientId, RecipientPersonId, "finance.check", project2);
        var first = await Request(f); Guid second;
        await using (var seed = f.CreateContext())
        {
            var request = new AdvanceRequest { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = project2, OwnerPersonId = AuthorPersonId, CreatedBy = AuthorId, State = "Approved" };
            seed.Add(request); await seed.SaveChangesAsync(); second = request.Id;
        }
        await using var db = f.CreateContext(); var docs = await Documents(f, db, files);
        await docs.RegisterAsync(AuthorId, second, await Input(f, files, second), await Meta(f, second), default);
        await docs.RegisterAsync(AuthorId, first, await Input(f, files, first), await Meta(f, first), default);
        var row = await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync(); var meta = await Meta(f, first, 1);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock), async () =>
        {
            await db.Set<GrantAssignment>().Where(x => x.Id == grant).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "ถอนก่อน commit"));
        });
        var review = new DuplicateDocumentService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId, audit), f.Clock);
        Assert.Equal(403, (await review.ReviewDuplicateAsync(RecipientId, first, row.Id, false, meta, default)).Status);
        Assert.Equal("Pending", (await db.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
        Assert.Equal(meta.RequestVersion, (await Meta(f, first)).RequestVersion);
        Assert.Equal(meta.FamilyVersion, (await Meta(f, first)).FamilyVersion);
    }

    internal static async Task<DocumentInput> Input(FieldAdvanceFixture f, Files files, Guid request)
    {
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        var upload = await service.UploadAsync(AuthorId, request, new MemoryStream(Image()), "image/png", default);
        Assert.Equal(201, upload.Status);
        return new("ร้านทดสอบ", "01", false, new(2026, 10, 20), 123.45m, [upload.Value]);
    }
    internal static async Task<ExpenseDocumentService> Documents(FieldAdvanceFixture f, Tpr10DbContext db, Files files)
        => new(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), await Service(f, db, files), f.Clock);
}
