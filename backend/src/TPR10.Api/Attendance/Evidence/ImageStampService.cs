using System.Security.Cryptography;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace TPR10.Api.Attendance.Evidence;

public interface IImageStampService
{
    Task<StampedImage> StampAsync(Stream source, StampRequest stamp, CancellationToken ct);
    StampedImage Thumbnail(StampedImage full);
}

public sealed class ImageStampService(TimeProvider timeProvider) : IImageStampService
{
    public Task<StampedImage> StampAsync(Stream source, StampRequest stamp, CancellationToken ct) =>
        ImageWorkBudget.RunAsync(async token =>
        {
            var bytes = await ReadAsync(source, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Stamp(bytes, StampFormatter.Format(stamp), token);
        }, timeProvider, ct);

    public StampedImage Thumbnail(StampedImage full) => ImageWorkBudget.RunAsync(token =>
    {
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(full.Jpeg)), full.Sha256, StringComparison.Ordinal))
            throw new ImageProcessingException("image-invalid");
        using var bitmap = Decode(full.Jpeg, out _);
        if (bitmap.Width != full.Width || bitmap.Height != full.Height || bitmap.Width < ImageLimits.MinWidth || bitmap.Height <= ImageLimits.FooterHeight)
            throw new ImageProcessingException("image-invalid");
        const int footerHeight = 90;
        var photoHeight = bitmap.Height - ImageLimits.FooterHeight;
        var ratio = Math.Min((double)ImageLimits.ThumbnailLongSide / bitmap.Width,
            (double)(ImageLimits.ThumbnailLongSide - footerHeight) / photoHeight);
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(photoHeight * ratio));
        using var output = new SKBitmap(ImageLimits.ThumbnailLongSide, height + footerHeight);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        var left = (output.Width - width) / 2f;
        canvas.DrawImage(image, new SKRect(0, 0, bitmap.Width, photoHeight), new SKRect(left, 0, left + width, height), new SKSamplingOptions(SKFilterMode.Linear));
        // Resample the existing stamped pixels; discard only blank right-side footer space.
        // Never shape a second stamp or read a clock when producing this derivative.
        canvas.DrawImage(image, new SKRect(0, photoHeight, ImageLimits.MinWidth, bitmap.Height),
            new SKRect(0, height, output.Width, output.Height), new SKSamplingOptions(SKFilterMode.Linear));
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Encode(output, full.StampText));
    }, timeProvider, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<byte[]> ReadAsync(Stream source, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ImageLimits.MaxBytes + 1 - (int)output.Length)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > ImageLimits.MaxBytes) throw new ImageProcessingException("image-too-large");
        }
    }

    private static SKBitmap Decode(byte[] bytes, out SKEncodedOrigin origin)
    {
        ImageHeader.Validate(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new ImageProcessingException("image-invalid");
        ImageLimits.ValidateDimensions(codec.Info.Width, codec.Info.Height);
        if (codec.FrameCount > 1 || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
            throw new ImageProcessingException("image-invalid");
        origin = codec.EncodedOrigin;
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new ImageProcessingException("image-invalid");
        }
        return bitmap;
    }

    private static StampedImage Stamp(byte[] bytes, string text, CancellationToken token)
    {
        using var source = Decode(bytes, out var origin);
        token.ThrowIfCancellationRequested();
        var rotated = (int)origin >= 5;
        var width = rotated ? source.Height : source.Width;
        var height = rotated ? source.Width : source.Height;
        var ratio = Math.Min(1d, (double)ImageLimits.PhotoLongSide / Math.Max(width, height));
        var photoWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var photoHeight = Math.Max(1, (int)Math.Round(height * ratio));
        using var output = new SKBitmap(Math.Max(ImageLimits.MinWidth, photoWidth), photoHeight + ImageLimits.FooterHeight);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.White);
        canvas.Save();
        canvas.Translate((output.Width - photoWidth) / 2f, 0);
        canvas.Scale((float)photoWidth / width, (float)photoHeight / height);
        Orient(canvas, origin, source.Width, source.Height);
        using var photo = SKImage.FromBitmap(source);
        canvas.DrawImage(photo, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
        using var black = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(0, photoHeight, output.Width, ImageLimits.FooterHeight, black);
        using var stream = typeof(ImageStampService).Assembly.GetManifestResourceStream("TPR10.Evidence.NotoSansThai.ttf")
            ?? throw new InvalidOperationException("Bundled evidence font is missing.");
        using var typeface = SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("Bundled evidence font is invalid.");
        using var font = new SKFont(typeface, 32);
        if (!font.ContainsGlyphs(text)) throw new InvalidOperationException("Bundled evidence font does not cover the stamp.");
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var shaper = new SKShaper(typeface);
        if (shaper.Shape(text, font).Width > ImageLimits.MinWidth - 32) throw new InvalidOperationException("Evidence stamp does not fit.");
        var baseline = photoHeight + (ImageLimits.FooterHeight - font.Metrics.Descent - font.Metrics.Ascent) / 2;
        canvas.DrawShapedText(shaper, text, 16, baseline, SKTextAlign.Left, font, white);
        token.ThrowIfCancellationRequested();
        return Encode(output, text);
    }

    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
            _ => SKMatrix.Identity
        };
        canvas.Concat(matrix);
    }

    private static StampedImage Encode(SKBitmap bitmap, string text)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90) ?? throw new ImageProcessingException("image-invalid");
        if (data.Size > ImageLimits.MaxBytes) throw new ImageProcessingException("image-too-large");
        var bytes = data.ToArray();
        using var verified = Decode(bytes, out _);
        if (verified.Width != bitmap.Width || verified.Height != bitmap.Height) throw new ImageProcessingException("image-invalid");
        return new StampedImage(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), bitmap.Width, bitmap.Height, text);
    }
}
