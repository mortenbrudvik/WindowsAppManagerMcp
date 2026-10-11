namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The outcome of one window change (move, resize, bounds, state, snap, move to a monitor) by IWindowService (roadmap
/// M2.2): whether Windows made the change and, when it did not, why. A refused SetWindowPos reports its Win32 error
/// (5 is <see cref="WindowErrorCode.AccessDenied"/>), and a state change is a success only when the window was seen in
/// the requested state afterwards.
/// </summary>
/// <param name="Handle">The window the change was for.</param>
/// <param name="Success">True when the change was made.</param>
/// <param name="ErrorCode">Why it was not; <see cref="WindowErrorCode.None"/> on success.</param>
/// <param name="Error">What went wrong and what the agent can do about it; null on success.</param>
public sealed record WindowChangeResult(nint Handle, bool Success, WindowErrorCode ErrorCode = WindowErrorCode.None, string? Error = null)
{
    /// <summary>The change was made.</summary>
    public static WindowChangeResult Done(nint handle) => new(handle, true);

    /// <summary>The change was not made, because of <paramref name="errorCode"/>.</summary>
    public static WindowChangeResult Failed(nint handle, WindowErrorCode errorCode, string error) => new(handle, false, errorCode, error);

    /// <summary>The errorCode as the tools report it, or null on success.</summary>
    public string? ErrorCodeName => Success ? null : ErrorCode.ToString();
}
