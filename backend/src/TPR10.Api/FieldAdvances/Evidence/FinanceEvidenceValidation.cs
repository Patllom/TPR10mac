using SkiaSharp;
namespace TPR10.Api.FieldAdvances.Evidence;

// Shared bounded decoding only; authorization and persistence belong to each workflow.
internal sealed class FinanceEvidenceValidation(IFinancePdfValidator pdf, FinanceEvidenceOptions options)
{
    internal async Task<byte[]?> BoundedRead(Stream content, CancellationToken ct)
    {
        using var memory = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, options.MaxFileBytes - memory.Length + 1)), ct)) != 0)
        {
            if (memory.Length + read > options.MaxFileBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.Length == 0 ? null : memory.ToArray();
    }
    internal async Task<bool> ValidContent(byte[] bytes, string declaredType, CancellationToken ct)
    {
        if (declaredType == "application/pdf") return bytes.AsSpan().StartsWith("%PDF-"u8) && await pdf.ValidateAsync(bytes, ct);
        if (declaredType is not ("image/png" or "image/jpeg")) return false;
        using var codec = SKCodec.Create(new MemoryStream(bytes, false));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > options.MaxImagePixels
            || codec.EncodedFormat != (declaredType == "image/png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg)) return false;
        if (declaredType == "image/png" && !bytes.AsSpan().EndsWith(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })) return false;
        if (declaredType == "image/jpeg" && !bytes.AsSpan().EndsWith(new byte[] { 255, 217 })) return false;
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888));
        return codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success;
    }
}
