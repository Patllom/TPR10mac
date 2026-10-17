using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using TPR10.Api.Attendance.Evidence;

namespace TPR10.Api.Attendance.Recording;

internal sealed class CaptureInputException(int status) : Exception("invalid-capture-request")
{ internal int Status { get; } = status; }

internal static class CaptureHttpInput
{
    internal const int RequestLimit = 11 * 1024 * 1024;
    internal const int MetadataLimit = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<EvidenceAction>(allowIntegerValues: false) }
    };
    internal static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            CheckNames(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new CaptureInputException(400);
        }
        catch (JsonException) { throw new CaptureInputException(400); }
    }
    private static void CheckNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new CaptureInputException(400); CheckNames(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) CheckNames(value);
    }
    internal static async Task<byte[]> ReadAsync(Stream source, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), ct);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new CaptureInputException(413);
        }
    }
    internal static async Task<(CaptureMetadata Metadata, byte[] Photo)> MultipartAsync(HttpRequest request, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || media.MediaType != "multipart/form-data") throw new CaptureInputException(415);
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new CaptureInputException(400);
        if (request.ContentLength > RequestLimit) throw new CaptureInputException(413);
        using var bounded = new LimitedReadStream(request.Body, RequestLimit);
        var reader = new MultipartReader(boundary, bounded) { HeadersCountLimit = 8, HeadersLengthLimit = MetadataLimit };
        byte[]? metadata = null, photo = null;
        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } part)
            {
                if (!ContentDispositionHeaderValue.TryParse(part.ContentDisposition, out var disposition)
                    || disposition.DispositionType != "form-data") throw new CaptureInputException(400);
                if (part.Headers?.ContainsKey("Content-Transfer-Encoding") == true) throw new CaptureInputException(415);
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name == "metadata" && metadata is null)
                {
                    if (!MediaTypeHeaderValue.TryParse(part.ContentType, out var type) || type.MediaType != "application/json") throw new CaptureInputException(415);
                    metadata = await ReadAsync(part.Body, MetadataLimit, ct);
                }
                else if (name == "photo" && photo is null)
                {
                    if (!MediaTypeHeaderValue.TryParse(part.ContentType, out var type) || type.MediaType != "image/jpeg" && type.MediaType != "image/png") throw new CaptureInputException(415);
                    photo = await ReadAsync(part.Body, ImageLimits.MaxBytes, ct);
                }
                else throw new CaptureInputException(400);
            }
            // Include any trailing bytes in the request budget; never persist raw input to disk.
            var buffer = new byte[81920]; while (await bounded.ReadAsync(buffer, ct) != 0) { }
        }
        catch (InvalidDataException) { throw new CaptureInputException(400); }
        if (metadata is null || photo is null || photo.Length == 0) throw new CaptureInputException(400);
        return (Parse<CaptureMetadata>(metadata), photo);
    }

    private sealed class LimitedReadStream(Stream source, long limit) : Stream
    {
        private long count;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - count + 1)], ct);
            count += read; if (count > limit) throw new CaptureInputException(413); return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, length), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
    }
}
