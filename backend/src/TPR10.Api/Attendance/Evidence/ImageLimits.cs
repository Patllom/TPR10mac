namespace TPR10.Api.Attendance.Evidence;

public static class ImageLimits
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const long MaxPixels = 20_000_000;
    public const int PhotoLongSide = 2048;
    public const int FooterHeight = 112;
    public const int MinWidth = 800;
    public const int ThumbnailLongSide = 640;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static void ValidateDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaxPixels || height > MaxPixels || checked(width * height) > MaxPixels)
            throw new ImageProcessingException("image-invalid");
    }
}

public sealed class ImageProcessingException(string code) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode => Code switch { "image-busy" => 429, "image-timeout" => 503, _ => 400 };
    public int? RetryAfterSeconds => Code == "image-busy" ? 5 : null;
}
