using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using TPR10.Api.Attendance.Evidence;

namespace TPR10.Api.IntegrationTests;

[Collection("image-processing")]
public sealed class EvidenceStampTests
{
    [Theory]
    [InlineData("2026-09-24T16:59:59Z", EvidenceAction.CheckIn, "24/09/2026 23:59:59 (UTC+7) — เข้า")]
    [InlineData("2026-09-24T17:00:00Z", EvidenceAction.CheckOut, "25/09/2026 00:00:00 (UTC+7) — ออก")]
    [InlineData("2026-09-25T10:00:00+09:00", EvidenceAction.CheckIn, "25/09/2026 08:00:00 (UTC+7) — เข้า")]
    public void Stamp_uses_fixed_instant_and_Thai_calendar_day(string instant, EvidenceAction action, string expected)
    {
        Assert.Equal(expected, StampFormatter.Format(new StampRequest(DateTimeOffset.Parse(instant), action)));
    }

    [Fact]
    public void Stamp_ignores_host_culture_and_rejects_unknown_action()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("25/09/2026 00:00:00 (UTC+7) — เข้า", StampFormatter.Format(Stamp));
            Assert.Throws<ArgumentOutOfRangeException>(() => StampFormatter.Format(Stamp with { Action = (EvidenceAction)99 }));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stamp_returns_clean_JPEG_with_real_footer_pixels_and_checksum(bool png)
    {
        var service = Service();
        var input = StampFixtures.Image(800, 400, png);
        using var source = new MemoryStream(input);
        StampedImage result = await service.StampAsync(source, Stamp, CancellationToken.None);
        Assert.Equal("25/09/2026 00:00:00 (UTC+7) — เข้า", result.StampText);
        Assert.Equal(800, result.Width);
        Assert.Equal(512, result.Height);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(result.Jpeg)), result.Sha256);
        using var decoded = SKBitmap.Decode(result.Jpeg);
        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.Width);
        Assert.Equal(512, decoded.Height);
        Assert.True(decoded.GetPixel(5, 405).Red < 20);
        var white = 0;
        for (var y = 410; y < 502; y++)
            for (var x = 16; x < 784; x++)
                if (decoded.GetPixel(x, y).Red > 200) white++;
        Assert.True(white > 500, "The footer must contain rendered text, not just metadata or a blank bar.");
        Assert.True(source.CanRead);
        using var second = new MemoryStream(input);
        StampedImage checkOut = await service.StampAsync(second, Stamp with { Action = EvidenceAction.CheckOut }, CancellationToken.None);
        Assert.NotEqual(result.Sha256, checkOut.Sha256);
    }

    [Theory]
    [InlineData(1, 0, 1, 2, 3)]
    [InlineData(2, 1, 0, 3, 2)]
    [InlineData(3, 3, 2, 1, 0)]
    [InlineData(4, 2, 3, 0, 1)]
    [InlineData(5, 0, 2, 1, 3)]
    [InlineData(6, 2, 0, 3, 1)]
    [InlineData(7, 3, 1, 2, 0)]
    [InlineData(8, 1, 3, 0, 2)]
    public async Task Exif_orientation_is_applied_to_pixels_then_removed(int orientation, int tl, int tr, int bl, int br)
    {
        var service = Service();
        using var source = new MemoryStream(StampFixtures.Exif(StampFixtures.Image(800, 400, false), orientation));
        StampedImage output = await service.StampAsync(source, Stamp, CancellationToken.None);
        using var bitmap = SKBitmap.Decode(output.Jpeg);
        var rotated = orientation >= 5;
        Assert.Equal(rotated ? 912 : 512, output.Height);
        var left = rotated ? 210 : 10;
        var right = rotated ? 590 : 790;
        var bottom = rotated ? 790 : 390;
        var actual = new[] { bitmap.GetPixel(left, 10), bitmap.GetPixel(right, 10), bitmap.GetPixel(left, bottom), bitmap.GetPixel(right, bottom) };
        var expected = new[] { tl, tr, bl, br }.Select(i => StampFixtures.Colors[i]).ToArray();
        for (var i = 0; i < actual.Length; i++)
            Assert.True(Math.Abs(actual[i].Red - expected[i].Red) < 15 && Math.Abs(actual[i].Green - expected[i].Green) < 15 && Math.Abs(actual[i].Blue - expected[i].Blue) < 15);
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(output.Jpeg));
        Assert.DoesNotContain("private-gps", Encoding.Latin1.GetString(output.Jpeg));
    }

    [Fact]
    public async Task Transparent_PNG_is_flattened_on_white_without_upscaling_small_photo()
    {
        var service = Service();
        using var source = new MemoryStream(StampFixtures.Image(100, 80, true, transparent: true));
        StampedImage output = await service.StampAsync(source, Stamp, CancellationToken.None);
        Assert.Equal(800, output.Width);
        Assert.Equal(192, output.Height);
        using var bitmap = SKBitmap.Decode(output.Jpeg);
        Assert.True(bitmap.GetPixel(400, 40).Red > 245 && bitmap.GetPixel(400, 40).Green > 245);
    }

    [Theory]
    [InlineData(5000, 4000, 2048, 1750)]
    [InlineData(4000, 5000, 1638, 2160)]
    public async Task Twenty_million_pixels_are_accepted_and_resized_before_footer(int w, int h, int expectedW, int expectedH)
    {
        var service = Service();
        using var source = new MemoryStream(StampFixtures.Image(w, h, true));
        StampedImage output = await service.StampAsync(source, Stamp, CancellationToken.None);
        Assert.Equal(expectedW, output.Width);
        Assert.Equal(expectedH, output.Height);
    }

    [Theory]
    [InlineData(10485759, true)]
    [InlineData(10485760, true)]
    [InlineData(10485761, false)]
    public async Task Byte_limit_is_enforced_on_nonseekable_stream(int bytes, bool accepted)
    {
        var service = Service();
        using var source = new StampFixtures.NonSeekable(StampFixtures.PaddedJpeg(bytes));
        if (accepted)
        {
            StampedImage output = await service.StampAsync(source, Stamp, CancellationToken.None);
            Assert.InRange(output.Jpeg.Length, 1, 10485760);
        }
        else await RejectAsync("image-too-large", () => service.StampAsync(source, Stamp, CancellationToken.None));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("svg")]
    [InlineData("gif")]
    [InlineData("truncated-jpeg")]
    [InlineData("jpeg-trailing-payload")]
    [InlineData("apng")]
    [InlineData("bad-png-crc")]
    [InlineData("truncated-png")]
    [InlineData("zero-width")]
    [InlineData("pixel-overflow")]
    [InlineData("over-pixels")]
    public async Task Invalid_or_animated_image_is_rejected_before_publication(string kind)
    {
        var service = Service();
        using var source = new MemoryStream(StampFixtures.Invalid(kind));
        await RejectAsync("image-invalid", () => service.StampAsync(source, Stamp, CancellationToken.None));
    }

    [Fact]
    public async Task Thumbnail_uses_stamped_pixels_and_rejects_changed_source_bytes()
    {
        var service = Service();
        using var source = new MemoryStream(StampFixtures.Image(1600, 900, true));
        StampedImage full = await service.StampAsync(source, Stamp, CancellationToken.None);
        StampedImage thumbnail = service.Thumbnail(full);
        Assert.Equal(640, Math.Max(thumbnail.Width, thumbnail.Height));
        Assert.Equal(full.StampText, thumbnail.StampText);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(thumbnail.Jpeg)), thumbnail.Sha256);
        using var decoded = SKBitmap.Decode(thumbnail.Jpeg);
        Assert.NotNull(decoded);
        var changed = full with { Jpeg = (byte[])full.Jpeg.Clone() };
        changed.Jpeg[^10] ^= 1;
        await RejectAsync("image-invalid", () => Task.Run(() => service.Thumbnail(changed)));
    }

    private static readonly StampRequest Stamp = new(DateTimeOffset.Parse("2026-09-24T17:00:00Z"), EvidenceAction.CheckIn);

    [Fact]
    public async Task Changing_instant_changes_footer_pixels_only_and_thumbnail_copies_entire_stamp()
    {
        var service = Service();
        var source = StampFixtures.Image(1600, 900, true);
        using var input1 = new MemoryStream(source);
        using var input2 = new MemoryStream(source);
        var first = await service.StampAsync(input1, Stamp, CancellationToken.None);
        var second = await service.StampAsync(input2, Stamp with { OccurredAtUtc = Stamp.OccurredAtUtc.AddDays(1).AddHours(12) }, CancellationToken.None);
        Assert.Equal("26/09/2026 12:00:00 (UTC+7) — เข้า", second.StampText);
        using var a = SKBitmap.Decode(first.Jpeg);
        using var b = SKBitmap.Decode(second.Jpeg);
        for (var y = 16; y < 880; y += 16)
            for (var x = 16; x < 1580; x += 16) Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
        var changedPixels = 0;
        for (var y = 920; y < 992; y++)
            for (var x = 16; x < 400; x++)
                if (Math.Abs(a.GetPixel(x, y).Red - b.GetPixel(x, y).Red) > 30) changedPixels++;
        Assert.True(changedPixels > 100, "The changed date/time must change actual footer pixels.");

        var thumbnail = service.Thumbnail(second);
        using var actual = SKBitmap.Decode(thumbnail.Jpeg);
        using var expected = new SKBitmap(640, 90);
        using var canvas = new SKCanvas(expected);
        using var image = SKImage.FromBitmap(b);
        canvas.DrawImage(image, new SKRect(0, 900, 800, 1012), new SKRect(0, 0, 640, 90), new SKSamplingOptions(SKFilterMode.Linear));
        long error = 0;
        var samples = 0;
        for (var y = 12; y < 78; y++)
            for (var x = 4; x < 636; x++)
            {
                error += Math.Abs(expected.GetPixel(x, y).Red - actual.GetPixel(x, actual.Height - 90 + y).Red);
                samples++;
            }
        Assert.True((double)error / samples < 6, "The entire thumbnail footer must derive from the original stamped pixels (JPEG tolerance).");
    }

    [Fact]
    public async Task Png_text_metadata_is_not_copied_to_output()
    {
        var png = StampFixtures.Image(100, 80, true);
        byte[] withText = [.. png[..33], .. StampFixtures.Chunk("tEXt", Encoding.UTF8.GetBytes("Comment\0synthetic-private-location")), .. png[33..]];
        using var input = new MemoryStream(withText);
        var output = await Service().StampAsync(input, Stamp, CancellationToken.None);
        Assert.DoesNotContain("synthetic-private-location", Encoding.Latin1.GetString(output.Jpeg));
    }

    private static IImageStampService Service() => new ImageStampService(TimeProvider.System);

    private static async Task RejectAsync(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ImageProcessingException>(action);
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData(1, 19999999, true)]
    [InlineData(1, 20000000, true)]
    [InlineData(1, 20000001, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    [InlineData(long.MaxValue, long.MaxValue, false)]
    [InlineData(0, 1, false)]
    public void Pixel_arithmetic_is_bounded_before_native_allocation(long width, long height, bool accepted)
    {
        if (accepted) ImageLimits.ValidateDimensions(width, height);
        else Assert.Equal("image-invalid", Assert.Throws<ImageProcessingException>(() => ImageLimits.ValidateDimensions(width, height)).Code);
    }

    [Theory]
    [InlineData(800, 400, EvidenceAction.CheckIn, "landscape-in-midnight")]
    [InlineData(800, 400, EvidenceAction.CheckOut, "landscape-out-midnight")]
    [InlineData(4000, 5000, EvidenceAction.CheckIn, "portrait-in-midnight")]
    [InlineData(5000, 4000, EvidenceAction.CheckOut, "landscape-large-out")]
    public async Task Synthetic_visual_artifacts(int width, int height, EvidenceAction action, string name)
    {
        var service = Service();
        using var input = new MemoryStream(StampFixtures.Image(width, height, true));
        var full = await service.StampAsync(input, Stamp with { Action = action }, CancellationToken.None);
        var thumbnail = service.Thumbnail(full);
        Assert.InRange(Math.Max(thumbnail.Width, thumbnail.Height), 1, 640);
        using var decoded = SKBitmap.Decode(thumbnail.Jpeg);
        var inkRows = 0;
        for (var y = decoded.Height - 90; y < decoded.Height; y++)
        {
            var ink = 0;
            for (var x = 12; x < Math.Min(460, decoded.Width); x++)
            {
                var pixel = decoded.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200) ink++;
            }
            if (ink > 10) inkRows++;
        }
        Assert.InRange(inkRows, 16, 45); // 32px source at 0.8 scale: white ink covers at least 16 rows.
        var directory = Environment.GetEnvironmentVariable("TPR10_STAMP_ARTIFACTS");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + "-full.jpg"), full.Jpeg);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + "-thumbnail.jpg"), thumbnail.Jpeg);
        }
    }
}
