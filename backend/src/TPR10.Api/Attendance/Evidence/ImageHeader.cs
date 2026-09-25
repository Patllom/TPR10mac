using System.Buffers.Binary;

namespace TPR10.Api.Attendance.Evidence;

// Validate the complete envelope before native allocation: codecs can tolerate truncated input.
internal static class ImageHeader
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ImageLimits.MaxBytes) throw new ImageProcessingException("image-too-large");
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) Png(bytes);
        else if (bytes.StartsWith(new byte[] { 255, 216 })) Jpeg(bytes);
        else Invalid();
    }

    private static void Png(ReadOnlySpan<byte> bytes)
    {
        var position = 8;
        var hasData = false;
        while (position <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            if (length > (uint)(bytes.Length - position - 12)) Invalid();
            var size = (int)length;
            var type = bytes.Slice(position + 4, 4);
            var payload = bytes.Slice(position + 8, size);
            if (Crc(bytes.Slice(position + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + size + 8)..])) Invalid();
            if (position == 8)
            {
                if (!type.SequenceEqual("IHDR"u8) || size != 13) Invalid();
                ImageLimits.ValidateDimensions(BinaryPrimitives.ReadUInt32BigEndian(payload), BinaryPrimitives.ReadUInt32BigEndian(payload[4..]));
            }
            else if (type.SequenceEqual("IHDR"u8)) Invalid();
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) Invalid();
            if (type.SequenceEqual("IDAT"u8)) hasData = true;
            position += size + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (size != 0 || !hasData || position != bytes.Length) Invalid();
                return;
            }
        }
        Invalid();
    }

    private static void Jpeg(ReadOnlySpan<byte> bytes)
    {
        var position = 2;
        var hasFrame = false;
        var hasScan = false;
        var entropy = false;
        while (position < bytes.Length)
        {
            if (entropy)
            {
                while (position < bytes.Length && bytes[position] != 255) position++;
            }
            if (position >= bytes.Length || bytes[position++] != 255) Invalid();
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position >= bytes.Length) Invalid();
            var marker = bytes[position++];
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            entropy = false;
            if (marker == 0xd9)
            {
                if (!hasFrame || !hasScan || position != bytes.Length) Invalid();
                return;
            }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7 || position > bytes.Length - 2) Invalid();
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (length < 2 || length > bytes.Length - position) Invalid();
            var payload = bytes.Slice(position + 2, length - 2);
            // Multi-picture JPEG is not a single camera frame.
            if (marker == 0xe2 && payload.StartsWith("MPF\0"u8)) Invalid();
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (hasFrame || payload.Length < 6) Invalid();
                ImageLimits.ValidateDimensions(BinaryPrimitives.ReadUInt16BigEndian(payload[3..]), BinaryPrimitives.ReadUInt16BigEndian(payload[1..]));
                hasFrame = true;
            }
            position += length;
            if (marker == 0xda) { hasScan = true; entropy = true; }
        }
        Invalid();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return ~crc;
    }

    private static void Invalid() => throw new ImageProcessingException("image-invalid");
}
