using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace OneCode.Infrastructure.Media;

public sealed class ImagePipeline
{
    private readonly ILogger<ImagePipeline> _logger;
    private readonly string _tempDir;

    private const int MaxWidth = 2048;
    private const int MaxHeight = 2048;
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public ImagePipeline(ILogger<ImagePipeline>? logger = null)
    {
        _logger = logger ?? NullLogger<ImagePipeline>.Instance;
        _tempDir = Path.Combine(Path.GetTempPath(), "OneCode-images");
        if (!Directory.Exists(_tempDir))
            Directory.CreateDirectory(_tempDir);
    }

    public async Task<ImageProcessResult> ProcessAsync(
        byte[] rawData, ImageProcessOptions? options = null, CancellationToken ct = default)
    {
        var opts = options ?? new ImageProcessOptions();
        var id = Guid.NewGuid().ToString("N")[..12];

        try
        {
            Validate(rawData, Path.GetExtension(opts.FileName ?? "image.png"));

            using var skData = SKData.CreateCopy(rawData);
            using var bitmap = SKBitmap.Decode(skData)
                ?? throw new ImageProcessException("Failed to decode image data");

            var originalWidth = bitmap.Width;
            var originalHeight = bitmap.Height;

            var outputExt = DetermineOutputFormat(opts);
            var fileName = $"{id}{outputExt}";
            var outputPath = Path.Combine(_tempDir, fileName);

            SKBitmap workBitmap = bitmap;
            var ownsWorkBitmap = false;
            try
            {
                if (bitmap.Width > MaxWidth || bitmap.Height > MaxHeight)
                {
                    var (targetW, targetH) = CalculateTargetSize(bitmap.Width, bitmap.Height);
                    if (targetW != bitmap.Width || targetH != bitmap.Height)
                    {
                        workBitmap = bitmap.Resize(
                            new SKImageInfo(targetW, targetH),
                            SKSamplingOptions.Default)
                            ?? throw new ImageProcessException("Failed to resize image");
                        ownsWorkBitmap = true;
                    }
                }

                await using (var fs = File.Create(outputPath))
                {
                    using var image = SKImage.FromBitmap(workBitmap);
                    using var encoded = image.Encode(GetSkFormat(outputExt), 95);
                    encoded.SaveTo(fs);
                }
            }
            finally
            {
                if (ownsWorkBitmap)
                    workBitmap.Dispose();
            }

            var fileInfo = new FileInfo(outputPath);
            _logger.LogDebug(
                "Image processed: {Width}x{Height} -> {OutputPath} ({Size} bytes)",
                originalWidth, originalHeight, outputPath, fileInfo.Length);

            return new ImageProcessResult(
                FilePath: outputPath,
                OriginalWidth: originalWidth,
                OriginalHeight: originalHeight,
                FileSizeBytes: fileInfo.Length,
                Format: outputExt.TrimStart('.'));
        }
        catch (ImageProcessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image processing failed");
            throw new ImageProcessException("Failed to process image", ex);
        }
    }

    public Task CleanupAsync(string filePath, CancellationToken ct = default)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    public Task CleanupOldFilesAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (!Directory.Exists(_tempDir))
            return Task.CompletedTask;

        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var file in Directory.EnumerateFiles(_tempDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up temp image: {File}", file);
            }
        }

        return Task.CompletedTask;
    }

    private static void Validate(byte[] rawData, string extension)
    {
        if (rawData.Length == 0)
            throw new ImageProcessException("Image data is empty");

        if (rawData.Length > MaxFileSizeBytes)
            throw new ImageProcessException($"Image exceeds maximum size of {MaxFileSizeBytes} bytes");

        if (!string.IsNullOrEmpty(extension) && !SupportedExtensions.Contains(extension))
            throw new ImageProcessException($"Unsupported image format: {extension}");
    }

    private static (int width, int height) CalculateTargetSize(int originalWidth, int originalHeight)
    {
        var ratioW = (double)MaxWidth / originalWidth;
        var ratioH = (double)MaxHeight / originalHeight;
        var ratio = Math.Min(ratioW, ratioH);

        if (ratio >= 1.0)
            return (originalWidth, originalHeight);

        return ((int)(originalWidth * ratio), (int)(originalHeight * ratio));
    }

    private static string DetermineOutputFormat(ImageProcessOptions options)
    {
        if (options.TargetFormat is not null)
            return options.TargetFormat.StartsWith('.') ? options.TargetFormat : $".{options.TargetFormat}";

        return ".png";
    }

    private static SKEncodedImageFormat GetSkFormat(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
        ".gif" => SKEncodedImageFormat.Gif,
        ".bmp" => SKEncodedImageFormat.Bmp,
        ".webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png,
    };
}

public sealed class ImageProcessOptions
{
    public string? TargetFormat { get; set; }
    public string? FileName { get; set; }
}

public sealed record ImageProcessResult(
    string FilePath,
    int OriginalWidth,
    int OriginalHeight,
    long FileSizeBytes,
    string Format);

public sealed class ImageProcessException : Exception
{
    public ImageProcessException(string message) : base(message) { }
    public ImageProcessException(string message, Exception inner) : base(message, inner) { }
}
