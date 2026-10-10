namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The outcome of one close request (IWindowService.CloseWindow). Success means WM_CLOSE was posted to
/// the window, not that it closed: the application can still show a save prompt or ignore the request.
/// </summary>
/// <param name="Handle">The window the request was for.</param>
/// <param name="Success">True when the close message was posted.</param>
/// <param name="ErrorCode">Why nothing was posted; <see cref="WindowErrorCode.None"/> on success.</param>
/// <param name="Error">What went wrong and what the agent can do about it; null on success.</param>
public sealed record WindowCloseResult(nint Handle, bool Success, WindowErrorCode ErrorCode = WindowErrorCode.None, string? Error = null)
{
    /// <summary>The close message was posted.</summary>
    public static WindowCloseResult Sent(nint handle) => new(handle, true);

    /// <summary>Nothing was posted, because of <paramref name="errorCode"/>.</summary>
    public static WindowCloseResult Failed(nint handle, WindowErrorCode errorCode, string error) =>
        new(handle, false, errorCode, error);
}
