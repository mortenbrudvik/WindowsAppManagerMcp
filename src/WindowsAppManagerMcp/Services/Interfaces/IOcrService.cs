using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// read_screen_text and find_text_on_screen (roadmap M6.3): reads a window, region or monitor through the capture's raw pixel
/// seam (the capture tools' refusals, warnings, crop and policy check of a window target), blacks out the windows the user's
/// policy denies in a screen copy, enlarges small text, recognizes it with Windows OCR and maps every box to screen pixels.
/// The capture is registered 1:1, so a point in it is a click target for the mouse tools with the result's captureId. Only
/// reads: no window is restored, focused or changed. Failures are results with success false, never exceptions, except
/// cancellation and a regular expression that runs too long (<see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>).
/// </summary>
public interface IOcrService
{
    /// <summary>The lines OCR reads in the target, in reading order, at most <paramref name="maxLines"/>.</summary>
    Task<ScreenTextResult> ReadTextAsync(
        OcrRequest request, OcrDetail detail, int maxLines, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The places where <paramref name="matcher"/> matches the lines OCR reads in the target, top to bottom and then left to
    /// right, at most <paramref name="maxResults"/>, each with its box and centre in screen pixels and in the capture.
    /// </summary>
    Task<TextFindResult> FindTextAsync(
        OcrRequest request, OcrTextMatcher matcher, int maxResults, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>What to read: the target, and the OCR language (an installed tag, or null for the user's).</summary>
public sealed record OcrRequest(CaptureTarget Target, string? Language);
