using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceEvidenceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("FinanceEvidenceService")]
    [InlineData("FinanceEvidenceMigration")]
    [InlineData("FinanceEvidenceReconciler")]
    public void Evidence_operations_are_available_to_the_application(string service)
    {
        var services = new ServiceCollection(); services.AddFieldAdvances();
        Assert.Contains(services, x => x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Evidence." + service);
    }

    [Fact]
    public async Task Immutable_store_roundtrips_and_never_overwrites()
    {
        using var files = new EvidenceFolders(); var key = Guid.NewGuid().ToString("N"); var bytes = Image();
        var ticket = await files.Store.WriteImmutableAsync(files.A, key, new MemoryStream(bytes), default);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), ticket.Sha256);
        Assert.Equal(bytes.Length, ticket.Length);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(files.RootA, key)));
        Assert.Equal(bytes, await files.Store.ReadVerifiedAsync(files.A, key, ticket.Sha256, ticket.Length, default));
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.WriteImmutableAsync(files.A, key, new MemoryStream([1, 2, 3]), default));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(files.RootA, key)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/escape")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Store_rejects_noncanonical_object_keys(string key)
    {
        using var files = new EvidenceFolders();
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.WriteImmutableAsync(files.A, key, new MemoryStream([1]), default));
        Assert.Empty(Directory.GetFiles(files.RootA));
    }

    [Fact]
    public async Task Store_rejects_unknown_root_symlinks_and_bad_digests()
    {
        using var files = new EvidenceFolders(); var key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.WriteImmutableAsync(Guid.NewGuid(), key, new MemoryStream([1]), default));
        var destination = Path.Combine(files.RootB, key); File.WriteAllBytes(destination, [42]);
        File.CreateSymbolicLink(Path.Combine(files.RootA, key), destination);
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.ReadVerifiedAsync(files.A, key, Convert.ToHexStringLower(SHA256.HashData([42])), 1, default));
        File.Delete(Path.Combine(files.RootA, key)); Directory.Delete(files.RootA); Directory.CreateSymbolicLink(files.RootA, files.RootB);
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.WriteImmutableAsync(files.A, Guid.NewGuid().ToString("N"), new MemoryStream([1]), default));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(destination));
        await Assert.ThrowsAnyAsync<IOException>(() => files.Store.ReadVerifiedAsync(files.B, key, new string('a', 64), 1, default));
    }

    [Fact]
    public async Task MovedEvidenceKeepsIdentity()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var before = await Seed(f, files, request); var old = await Current(f, before.Id);
        await using var db = f.CreateContext();
        Assert.True(await new FinanceEvidenceMigration(db, files.Store, f.Clock).CopyAndSwitchAsync(before.Id, files.B, default));
        db.ChangeTracker.Clear(); var after = await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == before.Id);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Sha256, after.Sha256);
        var locations = await db.Set<AdvanceEvidenceLocation>().Where(x => x.EvidenceId == before.Id).ToArrayAsync();
        Assert.Equal(2, locations.Length); Assert.False(locations.Single(x => x.Id == old.Id).IsCurrent);
        var locator = Parse(Assert.Single(locations, x => x.IsCurrent).StorageKey); Assert.Equal(files.B, locator.StorageId);
        Assert.Equal(Image(), await files.Store.ReadVerifiedAsync(locator.StorageId, locator.Key, after.Sha256, after.Length, default));
        Assert.True(File.Exists(Path.Combine(files.RootA, Parse(old.StorageKey).Key)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("offline")]
    [InlineData("commit")]
    [InlineData("concurrent")]
    public async Task Failed_migration_preserves_current_location_and_history(string failure)
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var evidence = await Seed(f, files, request); var before = await Current(f, evidence.Id);
        var path = Path.Combine(files.RootA, Parse(before.StorageKey).Key);
        if (failure == "missing") File.Delete(path);
        if (failure == "digest") File.WriteAllBytes(path, [1, 2, 3]);
        if (failure == "commit") await FailCommit(f, "field_advance_evidence_locations");
        var store = new ObservedStore(files.Store) { Offline = failure == "offline" };
        if (failure == "concurrent") store.AfterWrite = async () =>
        {
            await using var other = f.CreateContext();
            await other.Set<AdvanceEvidenceLocation>().Where(x => x.Id == before.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.VerifiedAtUtc, f.Clock.GetUtcNow().AddSeconds(1)));
        };
        await using var db = f.CreateContext();
        if (failure == "concurrent") _ = await db.Set<AdvanceEvidenceLocation>().SingleAsync(x => x.Id == before.Id);
        Assert.False(await new FinanceEvidenceMigration(db, store, f.Clock).CopyAndSwitchAsync(evidence.Id, files.B, default));
        Assert.Equal(before.Id, (await Current(f, evidence.Id)).Id);
        await using var check = f.CreateContext(); Assert.Single(await check.Set<AdvanceEvidenceLocation>().ToArrayAsync());
        Assert.Equal(failure != "missing", File.Exists(path));
    }

    [Fact]
    public async Task PendingLinkNeverDeletesReferencedFile()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var evidence = await Seed(f, files, request); var location = await Current(f, evidence.Id);
        var orphanKey = Guid.NewGuid().ToString("N"); File.WriteAllBytes(Path.Combine(files.RootA, orphanKey), Image());
        await using var db = f.CreateContext();
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt);
        db.Add(new AdvancePaymentProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = evidence.WorkspaceId,
            ProjectId = evidence.ProjectId,
            FamilyId = evidence.FamilyId,
            RequestId = evidence.RequestId,
            CommandId = receipt.CommandId,
            Kind = "Metadata",
            State = "Pending",
            FamilyVersion = 1,
            RequestVersion = 1,
            ContentHash = new string('a', 64),
            BeforeJson = "{}",
            AfterJson = "{}",
            EvidenceIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { evidence.Id }),
            ProposedBy = RecipientId,
            ProposedByPersonId = RecipientPersonId,
            Reason = "Pending proposal retains evidence",
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var orphans = await new FinanceEvidenceReconciler(db, files.Store).FindOrphansAsync(default);
        Assert.Equal(new EvidenceLocator(files.A, orphanKey), Assert.Single(orphans));
        Assert.True(File.Exists(Path.Combine(files.RootA, Parse(location.StorageKey).Key)));
        Assert.True(File.Exists(Path.Combine(files.RootA, orphanKey)));
    }

    [Fact]
    public async Task Stage_persists_verified_evidence_and_payer_participation_atomically_without_IO_under_locks()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        await using var db = f.CreateContext(); var store = new ObservedStore(files.Store) { BeforeIO = () => Assert.Null(db.Database.CurrentTransaction) };
        var service = await Service(f, db, files, store);
        var result = await service.StageAsync(RecipientId, request, new MemoryStream(Image()), "image/png", default);
        Assert.Equal(201, result.Status); var ticket = Assert.IsType<EvidenceTicket>(result.Value);
        await using var check = f.CreateContext(); var row = Assert.Single(await check.Set<AdvanceEvidence>().ToArrayAsync());
        Assert.Equal(ticket.Id, row.Id); Assert.Equal("Ready", row.State);
        Assert.Equal("Payer", Assert.Single(await check.Set<AdvancePaymentParticipation>().ToArrayAsync()).Role);
        Assert.NotEqual(row.Id.ToString("N"), Parse((await Current(f, row.Id)).StorageKey).Key);
        Assert.True(await service.ValidateLinksAsync(RecipientId, request, [row.Id], default));
        Assert.Equal(Image(), (await service.ReadAsync(RecipientId, row.Id, default)).Value);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("checker")]
    [InlineData("revoked")]
    public async Task Unauthorized_staging_never_reads_stream_or_records_history(string mode)
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        if (mode == "revoked") await Revoke(f);
        if (mode == "checker") await Capability(f, "finance.check");
        var actor = mode == "owner" ? AuthorId : RecipientId;
        await using var db = f.CreateContext(); var service = await Service(f, db, files, actor: actor);
        Assert.Equal(403, (await service.StageAsync(actor, request, new UnreadableStream(), "image/png", default)).Status);
        Assert.Empty(await db.Set<AdvanceEvidence>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Empty(Directory.GetFiles(files.RootA));
    }

    [Theory]
    [InlineData("mime")]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("oversize")]
    [InlineData("pixels")]
    [InlineData("truncated")]
    [InlineData("pdf")]
    public async Task Rejected_content_creates_no_evidence_or_participation(string mode)
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var bytes = Image(); var type = "image/png";
        if (mode == "mime") type = "image/jpeg";
        if (mode == "html") bytes = "<html>unsafe</html>"u8.ToArray();
        if (mode == "svg") { bytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(); type = "image/svg+xml"; }
        if (mode == "oversize") files.Options.MaxFileBytes = 16;
        if (mode == "pixels") files.Options.MaxImagePixels = 1;
        if (mode == "truncated") bytes = bytes[..^15];
        if (mode == "pdf") { bytes = "%PDF-1.7\nmalformed"u8.ToArray(); type = "application/pdf"; }
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        Assert.Equal(422, (await service.StageAsync(RecipientId, request, new MemoryStream(bytes), type, default)).Status);
        Assert.Empty(await db.Set<AdvanceEvidence>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Empty(Directory.GetFiles(files.RootA));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("cancel")]
    [InlineData("commit")]
    public async Task Failed_final_persistence_creates_no_history_and_retains_only_orphan_file(string mode)
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        using var cancel = new CancellationTokenSource();
        if (mode == "commit") await FailCommit(f, "field_advance_evidence");
        var store = new ObservedStore(files.Store)
        {
            AfterWrite = async () =>
        { if (mode == "revoke") await Revoke(f); if (mode == "cancel") cancel.Cancel(); }
        };
        await using var db = f.CreateContext(); var service = await Service(f, db, files, store);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StageAsync(RecipientId, request, new MemoryStream(Image()), "image/png", cancel.Token));
        else Assert.NotEqual(201, (await service.StageAsync(RecipientId, request, new MemoryStream(Image()), "image/png", default)).Status);
        await using var check = f.CreateContext(); Assert.Empty(await check.Set<AdvanceEvidence>().ToArrayAsync());
        Assert.Empty(await check.Set<AdvancePaymentParticipation>().ToArrayAsync()); Assert.Single(Directory.GetFiles(files.RootA));
    }

    [Fact]
    public async Task Links_reject_duplicates_foreign_requests_missing_bytes_and_limits()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var row = await Seed(f, files, request); var other = await Seed(f, files, await Approved(f));
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        Assert.True(await service.ValidateLinksAsync(RecipientId, request, [row.Id], default));
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, [row.Id, row.Id], default));
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, [other.Id], default));
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray(), default));
        files.Options.MaxTotalBytes = 1;
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, [row.Id], default));
        files.Options.MaxTotalBytes = 50 * 1024 * 1024;
        File.Delete(Path.Combine(files.RootA, Parse((await Current(f, row.Id)).StorageKey).Key));
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, [row.Id], default));
    }

    [Fact]
    public async Task Revoked_reader_is_denied_before_IO_and_after_IO()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var row = await Seed(f, files, await Approved(f));
        await using var db = f.CreateContext(); var store = new ObservedStore(files.Store) { AfterRead = () => Revoke(f), BeforeIO = () => Assert.Null(db.Database.CurrentTransaction) };
        var service = await Service(f, db, files, store);
        Assert.Equal(403, (await service.ReadAsync(RecipientId, row.Id, default)).Status);
        store.BeforeIO = () => Assert.Fail("Revoked reader must not reach storage");
        Assert.Equal(403, (await service.ReadAsync(RecipientId, row.Id, default)).Status);
    }

    [Fact]
    public async Task Link_validation_rechecks_all_rows_after_last_file_read()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var first = await Seed(f, files, request); var second = await Seed(f, files, request); var reads = 0;
        var store = new ObservedStore(files.Store)
        {
            AfterRead = async () =>
        {
            if (++reads != 2) return;
            await using var other = f.CreateContext();
            await other.Set<AdvanceEvidence>().Where(x => x.Id == first.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Missing"));
        }
        };
        await using var db = f.CreateContext(); var service = await Service(f, db, files, store);
        Assert.False(await service.ValidateLinksAsync(RecipientId, request, [first.Id, second.Id], default));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("application/pdf")]
    public async Task Valid_supported_formats_stage_and_read_exact_bytes(string contentType)
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        var bytes = contentType == "application/pdf" ? FinancePdfValidationTests.Pdf() : Image(SKEncodedImageFormat.Jpeg);
        await using var db = f.CreateContext();
        var validator = new PdfValidationProcess(await FinancePdfValidationTests.Options());
        var service = await Service(f, db, files, pdf: validator);
        var result = await service.StageAsync(RecipientId, request, new MemoryStream(bytes), contentType, default);
        Assert.Equal(201, result.Status); var ticket = Assert.IsType<EvidenceTicket>(result.Value);
        Assert.Equal(contentType, ticket.ContentType);
        Assert.Equal(bytes, (await service.ReadAsync(RecipientId, ticket.Id, default)).Value);
    }

    [Fact]
    public async Task Offline_adapter_denies_staging_without_participation()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, files, new ObservedStore(files.Store) { Offline = true });
        Assert.Equal(503, (await service.StageAsync(RecipientId, request, new MemoryStream(Image()), "image/png", default)).Status);
        Assert.Empty(await db.Set<AdvanceEvidence>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    [Fact]
    public async Task Caller_transaction_cannot_hold_financial_locks_during_file_IO()
    {
        await using var f = await Ready(); using var files = new EvidenceFolders(); var request = await Approved(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(RecipientId, request, new UnreadableStream(), "image/png", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateLinksAsync(RecipientId, request, [], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(RecipientId, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FinanceEvidenceMigration(db, files.Store, f.Clock).CopyAndSwitchAsync(Guid.NewGuid(), files.B, default));
    }

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await FieldAdvanceGrantTests.AuthorityAsync(f, RecipientId, RecipientPersonId); await Capability(f, "payment.record"); return f;
    }
    private static async Task Capability(FieldAdvanceFixture f, string capability)
    {
        await using var db = f.CreateContext();
        var proposals = await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync();
        foreach (var proposal in proposals) proposal.Capabilities = [capability]; await db.SaveChangesAsync();
    }
    private static async Task Revoke(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "test revocation"));
    }
    private static Task<Guid> Approved(FieldAdvanceFixture f) => new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
    private static async Task<FinanceEvidenceService> Service(FieldAdvanceFixture f, Tpr10DbContext db, EvidenceFolders files, IFinanceEvidenceStore? store = null, Guid? actor = null, IFinancePdfValidator? pdf = null)
    {
        var session = await f.SessionAsync(actor ?? RecipientId); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock);
        return new(db, new(db, access, people, new(db, access, people, f.Clock), f.Clock), store ?? files.Store, pdf ?? new RejectPdf(), files.Options, f.Clock);
    }
    private static async Task<AdvanceEvidence> Seed(FieldAdvanceFixture f, EvidenceFolders files, Guid requestId)
    {
        var bytes = Image(); var key = Guid.NewGuid().ToString("N"); File.WriteAllBytes(Path.Combine(files.RootA, key), bytes);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId);
        var row = new AdvanceEvidence
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = r.Id,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Length = bytes.Length,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(row); db.Add(new AdvanceEvidenceLocation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = r.Id,
            EvidenceId = row.Id,
            StorageKey = $"v1:{files.A:N}:{key}",
            IsCurrent = true,
            VerifiedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync(); return row;
    }
    private static async Task<AdvanceEvidenceLocation> Current(FieldAdvanceFixture f, Guid id)
    { await using var db = f.CreateContext(); return await db.Set<AdvanceEvidenceLocation>().SingleAsync(x => x.EvidenceId == id && x.IsCurrent); }
    private static EvidenceLocator Parse(string value) { Assert.True(EvidenceLocator.TryParse(value, out var locator)); return locator; }
    private static async Task FailCommit(FieldAdvanceFixture f, string table)
    {
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION evidence_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'evidence test commit failure'; END; $$");
        var sql = table == "field_advance_evidence" ? "CREATE CONSTRAINT TRIGGER evidence_test_fail AFTER INSERT OR UPDATE ON field_advance_evidence DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_test_fail()" : "CREATE CONSTRAINT TRIGGER evidence_test_fail AFTER INSERT OR UPDATE ON field_advance_evidence_locations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_test_fail()";
        await db.Database.ExecuteSqlRawAsync(sql);
    }
    private static byte[] Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.White); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100); return data.ToArray(); }
    private sealed class RejectPdf : IFinancePdfValidator { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
    private sealed class UnreadableStream : MemoryStream { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unauthorized content read"); }
    private sealed class ObservedStore(IFinanceEvidenceStore inner) : IFinanceEvidenceStore
    {
        public Action? BeforeIO { get; set; }
        public Func<Task>? AfterWrite { get; set; }
        public Func<Task>? AfterRead { get; init; }
        public bool Offline { get; init; }
        public async Task<EvidenceTicket> WriteImmutableAsync(Guid storageId, string key, Stream content, CancellationToken ct)
        { BeforeIO?.Invoke(); if (Offline) throw new IOException("Simulated NAS offline"); var value = await inner.WriteImmutableAsync(storageId, key, content, ct); if (AfterWrite != null) await AfterWrite(); return value; }
        public async Task<byte[]> ReadVerifiedAsync(Guid storageId, string key, string sha256, long length, CancellationToken ct)
        { BeforeIO?.Invoke(); var value = await inner.ReadVerifiedAsync(storageId, key, sha256, length, ct); if (AfterRead != null) await AfterRead(); return value; }
        public IEnumerable<EvidenceLocator> Enumerate() => inner.Enumerate();
    }
    private sealed class EvidenceFolders : IDisposable
    {
        public Guid A { get; } = Guid.NewGuid(); public Guid B { get; } = Guid.NewGuid();
        public string RootA { get; }
        public string RootB { get; }
        private readonly string root = Path.Combine(Path.GetTempPath(), "tpr10-evidence-" + Guid.NewGuid().ToString("N"));
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public EvidenceFolders()
        {
            // Resolve /var -> /private/var on macOS: the production store rejects symlink ancestors.
            root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
            RootA = Path.Combine(root, "a"); RootB = Path.Combine(root, "b"); Directory.CreateDirectory(RootA); Directory.CreateDirectory(RootB);
            Options = new() { ActiveStorageId = A, Roots = new() { [A] = RootA, [B] = RootB } }; Store = new(Options);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
