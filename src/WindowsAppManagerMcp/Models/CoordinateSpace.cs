using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What a mouse tool's x and y are measured in (roadmap M3.11). The server converts them to a screen point before the
/// input guard checks it, so every guard applies to the converted point exactly as to screen coordinates.
/// </summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "coordinate space")]
public enum CoordinateSpace
{
    /// <summary>Physical pixels of the virtual screen, as every screen coordinate the server reports.</summary>
    [JsonStringEnumMemberName("screen")]
    Screen,

    /// <summary>Pixels from the top-left corner of the target window's rectangle (GetWindowRect, find_windows' bounds).</summary>
    [JsonStringEnumMemberName("window")]
    Window,

    /// <summary>
    /// Pixels from the top-left corner of the target window's client area on screen (its rectangle mapped with
    /// MapWindowPoints), x to the right also in a mirrored right-to-left window, as in an image of it.
    /// </summary>
    [JsonStringEnumMemberName("client")]
    Client,

    /// <summary>Pixels of the image a capture returned, named by its captureId.</summary>
    [JsonStringEnumMemberName("image")]
    Image
}

/// <summary>
/// The point a mouse call was given, when it was not in screen coordinates (roadmap M3.11): the result's x and y are
/// the screen point it was converted to.
/// </summary>
/// <param name="X">The x the call was given, in <paramref name="Space"/>.</param>
/// <param name="Y">The y the call was given.</param>
/// <param name="Space">window, client or image.</param>
/// <param name="CaptureId">For image: the capture the point was measured on.</param>
public sealed record RequestedPoint(int X, int Y, string Space, string? CaptureId = null);
