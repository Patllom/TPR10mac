using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using TPR10.PdfValidator;

try
{
    const int maxInput = 10 * 1024 * 1024;
    using var input = Console.OpenStandardInput(); using var bytes = new MemoryStream(); var buffer = new byte[65536];
    int read;
    while ((read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, maxInput - bytes.Length + 1))) != 0)
    { if (bytes.Length + read > maxInput) return 1; bytes.Write(buffer, 0, read); }
    var content = bytes.ToArray();
    if (!content.AsSpan().StartsWith("%PDF-"u8) || !System.Text.Encoding.ASCII.GetString(content.AsSpan(Math.Max(0, content.Length - 32))).TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal)) return 1;
    var filters = new BoundedPdfFilters(); var diagnostics = new RejectingPdfLog();
    using var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = false, MaxStackDepth = 64, SkipMissingFonts = false, FilterProvider = filters, Logger = diagnostics });
    var structure = document.Structure;
    if (document.IsEncrypted || structure.Trailer.EncryptionToken != null || document.NumberOfPages is < 1 or > 1000
        || structure.CrossReferenceTable.ObjectOffsets.Count is < 1 or > 10000 || structure.Trailer.Size > 10001
        || structure.CrossReferenceTable.Parts.Count != 1) return 1;
    var blocked = new HashSet<string>(StringComparer.Ordinal)
    { "Encrypt", "JavaScript", "JS", "EmbeddedFiles", "EmbeddedFile", "Filespec", "EF", "OpenAction", "AA", "Launch",
        "RichMedia", "XFA", "GoToR", "GoToE", "SubmitForm", "ImportData", "Rendition", "Movie", "Sound", "3D", "Collection" };
    var contentRoots = new List<IToken>(); var decodedStreams = new Dictionary<StreamToken, Memory<byte>>();
    var visited = new HashSet<IndirectReference>(); var pending = new Stack<(IToken Token, int Depth)>(); var nodes = 0;
    foreach (var reference in structure.CrossReferenceTable.ObjectOffsets.Keys)
        pending.Push((new IndirectReferenceToken(reference), 0));
    pending.Push((structure.Catalog.CatalogDictionary, 0));
    while (pending.TryPop(out var current))
    {
        if (++nodes > 100000 || current.Depth > 64) return 1;
        void Push(IToken token) => pending.Push((token, current.Depth + 1));
        switch (current.Token)
        {
            case IndirectReferenceToken reference:
                if (visited.Add(reference.Data)) Push(structure.GetObject(reference.Data).Data);
                break;
            case DictionaryToken dictionary:
                foreach (var pair in dictionary.Data)
                {
                    if (blocked.Contains(pair.Key)) return 1;
                    if (pair.Key is "Contents" or "CharProcs") contentRoots.Add(pair.Value);
                    Push(pair.Value);
                }
                break;
            case ArrayToken array:
                foreach (var item in array.Data) Push(item);
                break;
            case StreamToken stream:
                // External file streams and unsupported filters are not inspectable by this worker.
                if (stream.StreamDictionary.Data.ContainsKey("F")) return 1;
                Push(stream.StreamDictionary);
                var decoded = stream.Data;
                var chain = structure.FilterProvider.GetFilters(stream.StreamDictionary, structure.TokenScanner);
                if (chain.Count > 4) return 1;
                for (var i = 0; i < chain.Count; i++) decoded = chain[i].Decode(decoded, stream.StreamDictionary, filters, i);
                if (decoded.Length > 16 * 1024 * 1024) return 1;
                decodedStreams[stream] = decoded;
                if (stream.StreamDictionary.Data.TryGetValue("Subtype", out var subtype) && subtype is NameToken { Data: "Form" }
                    || stream.StreamDictionary.Data.ContainsKey("PatternType")) contentRoots.Add(stream);
                break;
            case NameToken name:
                if (blocked.Contains(name.Data)) return 1;
                break;
            case NumericToken number:
                if (!double.IsFinite(number.Data)) return 1;
                break;
            case StringToken text:
                if (text.Data.Length > 1024 * 1024) return 1;
                break;
            case HexToken hex:
                if (hex.Data.Length > 1024 * 1024) return 1;
                break;
            case BooleanToken:
            case NullToken:
                break;
            default: return 1;
        }
    }
    var contentVisited = new HashSet<IndirectReference>(); var contentNodes = 0;
    void ValidateContent(IToken token, int depth)
    {
        if (++contentNodes > 100000 || depth > 64) throw new InvalidDataException("Content graph limit.");
        switch (token)
        {
            case IndirectReferenceToken reference:
                if (contentVisited.Add(reference.Data)) ValidateContent(structure.GetObject(reference.Data).Data, depth + 1);
                break;
            case ArrayToken array:
                foreach (var item in array.Data) ValidateContent(item, depth + 1);
                break;
            case DictionaryToken dictionary:
                foreach (var item in dictionary.Data.Values) ValidateContent(item, depth + 1);
                break;
            case StreamToken stream:
                StrictPdfContent.Validate(decodedStreams[stream]);
                break;
            default: throw new InvalidDataException("Uninspectable content reference.");
        }
    }
    foreach (var contentRoot in contentRoots) ValidateContent(contentRoot, 0);
    // Exercise page/content parsing too; a valid catalog alone is not a valid PDF.
    for (var i = 1; i <= document.NumberOfPages; i++) _ = document.GetPage(i);
    if (diagnostics.Rejected) return 1;
    Console.WriteLine("valid"); return 0;
}
catch (Exception) { return 1; } // never print document contents or parser diagnostics
