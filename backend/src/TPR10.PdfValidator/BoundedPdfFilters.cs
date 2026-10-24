using System.IO.Compression;
using System.Buffers.Binary;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace TPR10.PdfValidator;

// Deliberately narrow supported filter set. Unsupported compression/predictors fail closed.
// The process limit is an outer bound; decompression has independent per-stream/aggregate limits.
internal sealed class BoundedPdfFilters : BaseFilterProvider
{
    public BoundedPdfFilters() : this(new BoundedFlate()) { }
    private BoundedPdfFilters(BoundedFlate flate) : base(new Dictionary<string, IFilter> { ["FlateDecode"] = flate, ["Fl"] = flate }) { }

    private sealed class BoundedFlate : IFilter
    {
        private long decodedTotal;
        public bool IsSupported => true;
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
        {
            if (streamDictionary.Data.TryGetValue("DecodeParms", out var parameters) && parameters is not NullToken
                || streamDictionary.Data.TryGetValue("DP", out parameters) && parameters is not NullToken)
                throw new InvalidDataException("Unsupported predictor parameters.");
            if (input.Length < 6 || (input.Span[0] & 15) != 8 || input.Span[0] >> 4 > 7
                || ((input.Span[0] << 8) + input.Span[1]) % 31 != 0 || (input.Span[1] & 32) != 0)
                throw new InvalidDataException("Invalid zlib framing.");
            using var compressed = new MemoryStream(input.ToArray(), false);
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = inflater.Read(buffer)) != 0)
            {
                decodedTotal += read;
                if (output.Length + read > 16 * 1024 * 1024 || decodedTotal > 64 * 1024 * 1024) throw new InvalidDataException("PDF expansion limit.");
                output.Write(buffer, 0, read);
            }
            var result = output.ToArray(); uint a = 1, b = 0;
            foreach (var value in result) { a = (a + value) % 65521; b = (b + a) % 65521; }
            if (BinaryPrimitives.ReadUInt32BigEndian(input.Span[^4..]) != (b << 16 | a))
                throw new InvalidDataException("Invalid or truncated zlib checksum.");
            return result;
        }
    }
}
