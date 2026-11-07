using TPR10.Api.FieldAdvances.Payments;

using TPR10.Api.FieldAdvances;

using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementEvidenceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Owner_upload_does_not_create_payment_role()
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "report.read", Guid.Parse("71000000-0000-0000-0000-000000000012"));
        var request = await Request(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        var upload = await service.UploadAsync(AuthorId, request, new MemoryStream(Image()), "image/png", default);
        Assert.Equal(201, upload.Status);
        var participation = await db.Set<AdvancePaymentParticipation>().ToArrayAsync();
        Assert.DoesNotContain(participation, x => x.PersonId == AuthorPersonId && x.Role == "Payer");
        Assert.Equal(Image(), (await service.ReadAsync(AuthorId, upload.Value, default)).Value);
        var foreign = await Service(f, db, files, RecipientId);
        var crossProjectDownload = await foreign.ReadAsync(RecipientId, upload.Value, default);
        Assert.Equal(403, crossProjectDownload.Status);
    }
    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("application/pdf")]
    public async Task Supported_bytes_roundtrip_with_real_validator(string type)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        var bytes = type == "application/pdf" ? FinancePdfValidationTests.Pdf() : Image(SKEncodedImageFormat.Jpeg);
        var result = await service.UploadAsync(AuthorId, request, new MemoryStream(bytes), type, default);
        Assert.Equal(201, result.Status); Assert.Equal(bytes, (await service.ReadAsync(AuthorId, result.Value, default)).Value);
    }
    [Theory]
    [InlineData("mime")]
    [InlineData("oversize")]
    [InlineData("pixels")]
    [InlineData("truncated")]
    [InlineData("pdf")]
    public async Task Invalid_content_has_no_persistence(string mode)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        var bytes = Image(); var type = "image/png";
        if (mode == "mime") type = "image/jpeg";
        if (mode == "oversize") files.Options.MaxFileBytes = 16;
        if (mode == "pixels") files.Options.MaxImagePixels = 1;
        if (mode == "truncated") bytes = bytes[..^15];
        if (mode == "pdf") { bytes = "%PDF-1.7\nmalformed"u8.ToArray(); type = "application/pdf"; }
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        Assert.Equal(422, (await service.UploadAsync(AuthorId, request, new MemoryStream(bytes), type, default)).Status);
        Assert.Empty(await db.Set<AdvanceEvidence>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_during_IO_denies_upload_or_download(bool download)
    {
        await using var f = await Ready(postgres); using var files = new Files();
        var grant = await Grant(f, AuthorId, AuthorPersonId, "request.create"); var request = await Request(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, files);
        var id = download ? (await service.UploadAsync(AuthorId, request, new MemoryStream(Image()), "image/png", default)).Value : Guid.Empty;
        var observed = new ObservedStore(files.Store, async () =>
        {
            await using var other = f.CreateContext();
            await other.Set<GrantAssignment>().Where(x => x.Id == grant).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        }, () => Assert.Null(db.Database.CurrentTransaction));
        service = await Service(f, db, files, store: observed);
        var status = download ? (await service.ReadAsync(AuthorId, id, default)).Status
            : (await service.UploadAsync(AuthorId, request, new MemoryStream(Image()), "image/png", default)).Status;
        Assert.Equal(403, status); Assert.Empty(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
        if (!download) Assert.Empty(await db.Set<AdvanceEvidence>().ToArrayAsync());
    }
    internal static async Task<SettlementEvidenceService> Service(FieldAdvanceFixture f, Tpr10DbContext db, Files files, Guid? actor = null, IFinanceEvidenceStore? store = null)
        => new(db, await Access(f, db, actor ?? AuthorId), store ?? files.Store,
            new PdfValidationProcess(await FinancePdfValidationTests.Options()), files.Options, f.Clock);
    internal static byte[] Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.White); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100); return data.ToArray(); }
    internal sealed class ObservedStore(IFinanceEvidenceStore inner, Func<Task> after, Action before) : IFinanceEvidenceStore
    {
        public async Task<EvidenceTicket> WriteImmutableAsync(Guid storageId, string key, Stream content, CancellationToken ct)
        { before(); var result = await inner.WriteImmutableAsync(storageId, key, content, ct); await after(); return result; }
        public async Task<byte[]> ReadVerifiedAsync(Guid storageId, string key, string sha256, long length, CancellationToken ct)
        { before(); var result = await inner.ReadVerifiedAsync(storageId, key, sha256, length, ct); await after(); return result; }
        public IEnumerable<EvidenceLocator> Enumerate() => inner.Enumerate();
    }
    internal sealed class Files : IDisposable
    {
        private readonly string root = "/private/tmp/task4-evidence-" + Guid.NewGuid().ToString("N");
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public Files() { Directory.CreateDirectory(root); var id = Guid.NewGuid(); Options = new() { ActiveStorageId = id, Roots = new() { [id] = root } }; Store = new(Options); }
        public void Dispose() => Directory.Delete(root, true);
    }
}
