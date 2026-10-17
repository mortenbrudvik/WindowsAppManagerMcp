namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Windows OCR (Windows.Media.Ocr, roadmap M6.3) on pixels the server already holds: it reads no screen and no window,
/// and changes nothing. The only seam to the WinRT projection (NativeCallGuardTests' WinRtProjection tests), so the OCR service is tested
/// over a fake. The server never installs an OCR language: <see cref="GetCapabilities"/> reports the ones the user
/// installed.
/// </summary>
public interface IOcrEngineWrapper
{
    /// <summary>The installed recognizer languages, the user-profile language OCR picks, and the largest image edge.</summary>
    /// <exception cref="OcrUnavailableException">Windows OCR cannot be used on this system.</exception>
    OcrCapabilities GetCapabilities();

    /// <summary>
    /// Recognizes the text of a top-down BGRA image (4 bytes per pixel, the 4th undefined and ignored, rows
    /// <paramref name="width"/> * 4 bytes apart) in the language <paramref name="languageTag"/>, one of
    /// <see cref="OcrCapabilities.Languages"/>. Both edges must be at most <see cref="OcrCapabilities.MaxImageDimension"/>.
    /// Boxes are in pixels of the image.
    /// </summary>
    /// <exception cref="OcrUnavailableException">No engine for the language could be created, or the recognition failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<OcrPage> RecognizeAsync(byte[] bgra, int width, int height, string languageTag, CancellationToken cancellationToken);
}

/// <summary>What Windows OCR can do here.</summary>
/// <param name="Languages">The installed recognizer languages, as BCP-47 tags ("en-US", "nb"); empty when none is installed.</param>
/// <param name="ProfileLanguage">
/// The language OCR picks from the user's profile languages (the first of them it has a recognizer for), or null when it
/// has one for none of them.
/// </param>
/// <param name="MaxImageDimension">The largest width or height an image may have.</param>
public sealed record OcrCapabilities(IReadOnlyList<string> Languages, string? ProfileLanguage, int MaxImageDimension);

/// <summary>What OCR read from one image: its lines in reading order, and the text's angle when the image is skewed.</summary>
/// <param name="TextAngle">The angle of the text in degrees clockwise, or null when OCR does not report one.</param>
/// <param name="Lines">The lines, in the engine's reading order.</param>
public sealed record OcrPage(double? TextAngle, IReadOnlyList<OcrPageLine> Lines);

/// <summary>A line of an <see cref="OcrPage"/>: its text as the engine joined it, and its words in reading order.</summary>
/// <param name="Text">
/// The line's text: its words joined with spaces, or without them in languages written without spaces (Chinese, Japanese).
/// </param>
/// <param name="Words">The words, each a part of <paramref name="Text"/>.</param>
public sealed record OcrPageLine(string Text, IReadOnlyList<OcrPageWord> Words);

/// <summary>A word of an <see cref="OcrPageLine"/> and its box in pixels of the image.</summary>
public sealed record OcrPageWord(string Text, double X, double Y, double Width, double Height);

/// <summary>Windows OCR cannot be used: the API is missing, no engine can be created, or a recognition failed.</summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
