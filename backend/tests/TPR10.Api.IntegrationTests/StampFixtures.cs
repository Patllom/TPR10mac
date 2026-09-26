using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace TPR10.Api.IntegrationTests;

internal static class StampFixtures
{
    public static readonly SKColor[] Colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];

    public static byte[] Image(int w, int h, bool png, bool transparent = false)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (!transparent)
        {
            using var paint = new SKPaint();
            for (var i = 0; i < 4; i++)
            {
                paint.Color = Colors[i];
                canvas.DrawRect((i % 2) * w / 2f, (i / 2) * h / 2f, w / 2f, h / 2f, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    public static byte[] Exif(byte[] jpeg, int orientation)
    {
        byte[] exif = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        return [.. jpeg[..2], .. Segment(0xe1, exif), .. Segment(0xfe, Encoding.ASCII.GetBytes("private-gps=synthetic")), .. jpeg[2..]];
    }

    public static byte[] PaddedJpeg(int length)
    {
        var image = Image(100, 80, false);
        using var output = new MemoryStream();
        output.Write(image.AsSpan(0, 2));
        var remaining = length - image.Length;
        while (remaining > 0)
        {
            var part = Math.Min(65537, remaining);
            if (remaining - part is > 0 and < 4) part -= 4;
            output.Write(Segment(0xef, new byte[part - 4]));
            remaining -= part;
        }
        output.Write(image.AsSpan(2));
        return output.ToArray();
    }

    public static byte[] Invalid(string kind)
    {
        if (kind == "empty") return [];
        if (kind == "svg") return Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        if (kind == "gif") return Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        if (kind == "truncated-jpeg") return Image(100, 80, false)[..^2];
        if (kind == "jpeg-trailing-payload") return [.. Image(100, 80, false), 42];
        var png = Image(100, 80, true);
        if (kind == "truncated-png") return png[..^1];
        if (kind == "apng") return [.. png[..33], .. Chunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]), .. png[33..]];
        if (kind == "bad-png-crc") { png[32] ^= 1; return png; }
        var header = png[16..29];
        BinaryPrimitives.WriteUInt32BigEndian(header, kind == "zero-width" ? 0u : kind == "pixel-overflow" ? uint.MaxValue : 20000001u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), kind == "pixel-overflow" ? uint.MaxValue : 1u);
        return [.. png[..8], .. Chunk("IHDR", header), .. png[33..]];
    }

    private static byte[] Segment(byte marker, byte[] payload) => [255, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    internal static byte[] Chunk(string name, byte[] payload)
    {
        var chunk = new byte[payload.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(chunk, 4);
        payload.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, payload.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return chunk;
    }

    internal sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
