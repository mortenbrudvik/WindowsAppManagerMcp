using System.Globalization;
using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What a capture returns: the image, a file, both, or only the metadata. Also the capture tools' output argument
/// (roadmap M2.2): an unknown value fails with InvalidOutputMode.
/// </summary>
[ToolEnum(nameof(ScreenshotErrorCode.InvalidOutputMode), "output", NullMeans = "image")]
public enum CaptureOutputMode
{
    [JsonStringEnumMemberName("image")]
    Image,

    [JsonStringEnumMemberName("file")]
    File,

    [JsonStringEnumMemberName("both")]
    Both,

    [JsonStringEnumMemberName("metadata")]
    Metadata
}

/// <summary>An image encoding the capture tools can produce.</summary>
public enum ImageEncoding
{
    Png,
    Jpeg
}

/// <summary>
/// A capture failure with its error code. <see cref="ScreenshotService"/> turns it into a result with
/// success false; the message is shown to the agent.
/// </summary>
public sealed class CaptureException : Exception
{
    public CaptureException(ScreenshotErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ScreenshotErrorCode Code { get; }
}

/// <summary>
/// Checked and resolved <see cref="CaptureOptions"/>. <see cref="Resolve"/> rejects every invalid value
/// with an error code, before anything is captured, instead of falling back to a guess.
/// </summary>
/// <param name="Format">The requested encoding, or null for "auto" (decided by the output size).</param>
/// <param name="Quality">JPEG quality, 1-100.</param>
/// <param name="MaxDimension">Long-edge limit in pixels, or null for none.</param>
/// <param name="MaxWidth">Width limit in pixels, or null for none.</param>
/// <param name="Output">What the capture returns.</param>
/// <param name="OutputPath">Normalized absolute file path, or null for a generated temp file.</param>
/// <param name="Overwrite">Whether <paramref name="OutputPath"/> may replace an existing file.</param>
/// <param name="IncludeCursor">Draw the mouse pointer into the image (roadmap M7.1).</param>
/// <param name="GridSpacing">Draw a coordinate grid every this many screen pixels; null for none (roadmap M7.1).</param>
/// <param name="Zoom">Enlarge the image this many times (1-4) before the size limits apply (roadmap M7.1).</param>
public sealed record CaptureRequest(
    ImageEncoding? Format,
    int Quality,
    int? MaxDimension,
    int? MaxWidth,
    CaptureOutputMode Output,
    string? OutputPath,
    bool Overwrite,
    bool IncludeCursor = false,
    int? GridSpacing = null,
    double Zoom = 1)
{
    /// <summary>The largest zoom a capture takes.</summary>
    public const double MaxZoom = 4;

    internal const string MaxWidthMustBePositive = "maxWidth must be a positive number of pixels.";
    internal const string MaxDimensionOutOfRange = "maxDimension must be 0 (full resolution) or a positive number of pixels.";

    /// <summary>True when the result carries the image content block.</summary>
    public bool ReturnsImage => Output is CaptureOutputMode.Image or CaptureOutputMode.Both;

    /// <summary>True when the image is written to a file.</summary>
    public bool WritesFile => Output is CaptureOutputMode.File or CaptureOutputMode.Both;

    /// <summary>
    /// Checks <paramref name="options"/> and resolves the defaults and legacy aliases.
    /// </summary>
    /// <exception cref="CaptureException">An option is invalid; nothing has been captured.</exception>
    public static CaptureRequest Resolve(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var format = EncodingOf(options.Format);
        var (maxDimension, maxWidth) = ResolveSizeLimits(options.MaxDimension, options.MaxWidth);
        var output = ResolveOutput(options.Output, options.SaveToFile);

        string? outputPath = null;
        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            if (output is not (CaptureOutputMode.File or CaptureOutputMode.Both))
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidOutputMode,
                    $"outputPath is only used when output is 'file' or 'both', but output is '{Name(output)}'.");
            }

            (outputPath, format) = ResolveOutputPath(options.OutputPath, format, options.Overwrite);
        }

        if (!double.IsFinite(options.Zoom) || options.Zoom < 1 || options.Zoom > MaxZoom)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidImageSize,
                $"zoom must be 1-{MaxZoom:0}, but it is {options.Zoom}. Nothing was captured.");
        }

        if (options.GridSpacing is { } spacing && (spacing < CaptureGrid.MinSpacing || spacing > CaptureGrid.MaxSpacing))
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidImageSize,
                $"gridSpacing must be {CaptureGrid.MinSpacing}-{CaptureGrid.MaxSpacing} screen pixels, but it is {spacing}. Nothing was captured.");
        }

        return new CaptureRequest(
            format,
            Math.Clamp(options.Quality, 1, 100),
            maxDimension,
            maxWidth,
            output,
            outputPath,
            options.Overwrite,
            options.IncludeCursor,
            options.GridSpacing,
            options.Zoom);
    }

    /// <summary>
    /// Checks <paramref name="options"/> without capturing: null when they are valid, otherwise the
    /// failure <see cref="Resolve"/> would throw.
    /// </summary>
    public static CaptureException? Validate(CaptureOptions options)
    {
        try
        {
            Resolve(options);
            return null;
        }
        catch (CaptureException ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// The output size of a <paramref name="width"/> x <paramref name="height"/> capture: <see cref="Zoom"/> times its size
    /// (1 unless asked), as large as possible within <see cref="MaxDimension"/> and <see cref="MaxWidth"/>, keeping the
    /// aspect ratio. Without a zoom a capture that already fits keeps its size; nothing is scaled up. An edge that would
    /// round to zero pixels (a long, thin strip) keeps one pixel: the image to screen transform has a scale per axis, so
    /// points still map exactly.
    /// </summary>
    public (int Width, int Height) OutputSize(int width, int height)
    {
        var scale = Math.Max(1.0, Zoom);
        var longEdge = Math.Max(width, height);
        if (MaxDimension is int maxDimension && longEdge * scale > maxDimension)
        {
            scale = Math.Min(scale, (double)maxDimension / longEdge);
        }

        if (MaxWidth is int maxWidth && width * scale > maxWidth)
        {
            scale = Math.Min(scale, (double)maxWidth / width);
        }

        if (scale == 1.0)
        {
            return (width, height);
        }

        // Rounding (not truncation) keeps the limiting edge exactly at its limit despite floating-point error.
        var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
        return (scaledWidth, scaledHeight);
    }

    /// <summary>
    /// Refuses a <paramref name="width"/> x <paramref name="height"/> capture whose output image (<see cref="OutputSize"/>) is
    /// enlarged beyond what a capture may be: <see cref="ScreenCaptureWrapper.MaxCapturePixels"/> (64 megapixels) and
    /// <see cref="ScreenCaptureWrapper.MaxCaptureSize"/> pixels on an edge. Only <see cref="Zoom"/> makes an image larger than
    /// its capture, which is held to those limits itself, so an image that is not enlarged passes as it is; zoom enlarges
    /// each edge up to 4 times, 16 times the pixels, and maxDimension 0 lifts the only cap after it: capture_region over a
    /// 4K monitor would be a 15360 x 8640 bitmap (about 530 MB) before the resize, the encoding and base64. Called with the
    /// size on the screen before anything is captured, and again before the image is made.
    /// </summary>
    /// <exception cref="CaptureException">InvalidImageSize: the image would be over the budget; nothing was captured.</exception>
    public void ThrowIfOutputTooLarge(int width, int height)
    {
        var (outputWidth, outputHeight) = OutputSize(width, height);
        if ((outputWidth <= width && outputHeight <= height) ||
            ((long)outputWidth * outputHeight <= ScreenCaptureWrapper.MaxCapturePixels &&
             outputWidth <= ScreenCaptureWrapper.MaxCaptureSize && outputHeight <= ScreenCaptureWrapper.MaxCaptureSize))
        {
            return;
        }

        var zoom = Zoom.ToString("0.##", CultureInfo.InvariantCulture);
        throw new CaptureException(ScreenshotErrorCode.InvalidImageSize,
            $"At zoom {zoom} the {width}x{height} capture would be a {outputWidth}x{outputHeight} image, more than one image " +
            $"may hold ({ScreenCaptureWrapper.MaxCapturePixels / (1024 * 1024)} megapixels, {ScreenCaptureWrapper.MaxCaptureSize} " +
            "pixels on an edge). Nothing was captured. Capture a smaller region, use a lower zoom, or pass a maxDimension " +
            $"(the default, {CaptureOptions.DefaultMaxDimension}, caps the long edge).");
    }

    /// <summary>The encoding for an output image of this size: <see cref="Format"/>, or the "auto" choice.</summary>
    public ImageEncoding EncodingFor(int outputWidth, int outputHeight) =>
        Format ?? (Math.Max(outputWidth, outputHeight) <= CaptureOptions.AutoPngMaxEdge ? ImageEncoding.Png : ImageEncoding.Jpeg);

    /// <summary>"png" or "jpeg", as the results report it.</summary>
    public static string FormatName(ImageEncoding encoding) => encoding == ImageEncoding.Png ? "png" : "jpeg";

    /// <summary>"image/png" or "image/jpeg".</summary>
    public static string MimeType(ImageEncoding encoding) => encoding == ImageEncoding.Png ? "image/png" : "image/jpeg";

    /// <summary>The file extension written for an encoding, with the dot.</summary>
    public static string Extension(ImageEncoding encoding) => encoding == ImageEncoding.Png ? ".png" : ".jpg";

    /// <summary>
    /// The encoding <paramref name="format"/> asks for, or null for Auto. The tools bind the argument to
    /// <see cref="CaptureFormat"/> and refuse an unknown name there (InvalidFormat); a value outside the enum is refused
    /// the same way here.
    /// </summary>
    private static ImageEncoding? EncodingOf(CaptureFormat format) =>
        format switch
        {
            CaptureFormat.Auto => null,
            CaptureFormat.Png => ImageEncoding.Png,
            CaptureFormat.Jpeg => ImageEncoding.Jpeg,
            _ => throw new CaptureException(ScreenshotErrorCode.InvalidFormat,
                $"Unknown format '{format}'. Use 'auto', 'png' or 'jpeg'.")
        };

    private static (int? MaxDimension, int? MaxWidth) ResolveSizeLimits(int? maxDimension, int? maxWidth)
    {
        if (maxDimension is < 0)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidImageSize, MaxDimensionOutOfRange);
        }

        if (maxWidth is <= 0)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidImageSize, MaxWidthMustBePositive);
        }

        // Legacy callers that pass only maxWidth keep its old meaning: a width limit and nothing else.
        var longEdge = maxDimension switch
        {
            null => maxWidth is null ? CaptureOptions.DefaultMaxDimension : (int?)null,
            0 => null,
            var value => value
        };

        return (longEdge, maxWidth);
    }

    /// <summary>
    /// <paramref name="output"/> with the legacy saveToFile alias applied. The tools bind the argument to
    /// <see cref="CaptureOutputMode"/> and refuse an unknown name there (InvalidOutputMode); a value outside the enum is
    /// refused the same way here.
    /// </summary>
    private static CaptureOutputMode ResolveOutput(CaptureOutputMode output, bool saveToFile)
    {
        if (!Enum.IsDefined(output))
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputMode,
                $"Unknown output '{output}'. Use 'image', 'file', 'both' or 'metadata'.");
        }

        // The legacy saveToFile flag meant "write a file instead of returning the image".
        return saveToFile && output != CaptureOutputMode.Both ? CaptureOutputMode.File : output;
    }

    private static (string Path, ImageEncoding? Format) ResolveOutputPath(string outputPath, ImageEncoding? format, bool overwrite)
    {
        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(outputPath))
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                    $"outputPath must be an absolute path such as C:\\captures\\shot.png, not '{outputPath}'.");
            }

            // Before anything touches the file system: checking or writing \\server\share\x.png opens an
            // SMB or WebDAV connection, which can leak the user's NTLM hash and copies the screen off
            // the computer. A prompt-injected agent could be told to do exactly that.
            if (!IsOnADriveLetter(outputPath))
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                    $"outputPath must be on a drive letter, such as C:\\captures\\shot.png, not '{outputPath}'. " +
                    "Network paths (\\\\server\\share) and device paths (\\\\?\\, \\\\.\\) are not accepted.");
            }

            fullPath = Path.GetFullPath(outputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath, $"outputPath '{outputPath}' is not a valid path: {ex.Message}");
        }

        var fileName = Path.GetFileName(fullPath);
        if (fileName.Contains(':', StringComparison.Ordinal))
        {
            // "file.txt:stream.png" would write into an alternate data stream of file.txt.
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                $"outputPath '{fullPath}' names an alternate data stream. Use a plain file name.");
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        ImageEncoding pathFormat = extension switch
        {
            ".png" => ImageEncoding.Png,
            ".jpg" or ".jpeg" => ImageEncoding.Jpeg,
            _ => throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                $"outputPath must end in .png, .jpg or .jpeg, not '{fileName}'.")
        };

        if (format is { } requested && requested != pathFormat)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                $"outputPath ends in {extension}, but format is '{FormatName(requested)}'. " +
                "Use a matching extension, or leave format at 'auto' to follow the extension.");
        }

        if (Directory.Exists(fullPath))
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath, $"outputPath '{fullPath}' is a folder.");
        }

        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidOutputPath,
                $"The folder of outputPath '{fullPath}' does not exist. The server does not create folders: " +
                "use an existing folder, or leave outputPath empty to write a new temp file.");
        }

        if (!overwrite && File.Exists(fullPath))
        {
            throw FileExists(fullPath);
        }

        return (fullPath, pathFormat);
    }

    /// <summary>
    /// True for a path rooted at a drive letter ("C:\" or "C:/"). Every other fully qualified path
    /// starts with two separators or "\?": a UNC path (\\server\share, //server/share) or a device
    /// path (\\?\, \\.\, \??\), which can reach another computer or a raw device.
    /// </summary>
    /// <remarks>
    /// A drive letter the user mapped to a network share is accepted: it names a destination the
    /// user set up, not one an agent can introduce. A configurable write root (R55) is the follow-up.
    /// </remarks>
    private static bool IsOnADriveLetter(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>The failure for an existing file that may not be replaced.</summary>
    internal static CaptureException FileExists(string path) =>
        new(ScreenshotErrorCode.FileExists,
            $"'{path}' already exists. Pass overwrite: true to replace it, or choose a new file name.");

    private static string Name(CaptureOutputMode mode) => mode.ToString().ToLowerInvariant();
}
