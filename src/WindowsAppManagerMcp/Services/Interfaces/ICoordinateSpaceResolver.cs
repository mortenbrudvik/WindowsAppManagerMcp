using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Converts a mouse tool's point from image, window or client coordinates to a screen point (roadmap M3.11), and says
/// how to check, inside the input gate, that what it was measured on has not changed since. Reads only: the input guard
/// still checks the converted point as it checks any screen point.
/// </summary>
public interface ICoordinateSpaceResolver
{
    /// <summary>
    /// A point of the image the capture <paramref name="captureId"/> returned, through that capture's imageToScreen:
    /// CaptureNotFound for an unknown or expired id, CoordinatesOutOfBounds for a point outside the image, and
    /// StaleCapture when the captured window moved or resized by more than 2 pixels, closed, was minimized or its handle
    /// was reused, or when the monitors changed since a screen capture. The result names the captured window, which is
    /// the click's expected target.
    /// </summary>
    PointResolution FromImage(string captureId, int x, int y);

    /// <summary>
    /// A point relative to <paramref name="window"/>: <see cref="CoordinateSpace.Window"/> from its rectangle's top-left
    /// corner (GetWindowRect), <see cref="CoordinateSpace.Client"/> from its client area's top-left corner on screen
    /// (MapWindowPoints, also right for a mirrored right-to-left window). InvalidHandle when the window cannot be read,
    /// CoordinatesOutOfBounds when it is minimized.
    /// </summary>
    PointResolution FromWindow(CoordinateSpace space, nint window, int x, int y);
}

/// <summary>A converted point, or why it could not be converted.</summary>
public sealed record PointResolution
{
    /// <summary>None when the point was converted.</summary>
    public InputErrorCode ErrorCode { get; init; }

    /// <summary>Why not, and what to do; null when converted.</summary>
    public string? Error { get; init; }

    /// <summary>True when the point was converted.</summary>
    public bool Resolved => ErrorCode == InputErrorCode.None;

    /// <summary>The screen point.</summary>
    public int X { get; init; }

    /// <inheritdoc cref="X"/>
    public int Y { get; init; }

    /// <summary>For an image point: the window the capture was of, or null for a screen capture.</summary>
    public nint? CapturedWindow { get; init; }

    /// <summary>
    /// Checks again that what the point was measured on is unchanged (StaleCapture otherwise); passed to the input guard
    /// as <see cref="InputTarget.PointRecheck"/>.
    /// </summary>
    public Func<SelectorRefusal?>? Recheck { get; init; }

    /// <summary>A converted point.</summary>
    public static PointResolution At(int x, int y, Func<SelectorRefusal?> recheck, nint? capturedWindow = null) =>
        new() { X = x, Y = y, Recheck = recheck, CapturedWindow = capturedWindow };

    /// <summary>A point that could not be converted.</summary>
    public static PointResolution Fail(InputErrorCode errorCode, string error) => new() { ErrorCode = errorCode, Error = error };
}
