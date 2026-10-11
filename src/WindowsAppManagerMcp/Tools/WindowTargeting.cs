using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The window a tool acts on (roadmap M2.4): its handle argument, kept for compatibility, or its <c>window</c> selector,
/// resolved by <see cref="IWindowService.ResolveWindow"/> to exactly one window. Exactly one of the two may be given.
/// </summary>
/// <remarks>
/// Resolution only turns the selector into a handle: the tool then goes on exactly as if the caller had passed that handle,
/// so the input guard, the host guard, the close guards and the target policy check the resolved window as they check any
/// handle; nothing a selector names gets past them. A selector that only names a handle is that handle, unchecked, so the
/// tool reports a handle that is no window as it always has. Resolution reads windows and never waits for one; the tools
/// that take the input gate resolve before they take it.
/// </remarks>
internal static class WindowTargeting
{
    /// <summary>The description of the window parameter of the tools whose window argument is <c>handle</c>.</summary>
    internal const string WindowDescription =
        "Alternative to handle: a window selector with find_windows' filters (plus foreground, index), resolved to exactly " +
        "one window or refused (AmbiguousWindow lists candidates).";

    /// <summary>The description of the window parameter of the input tools, whose window argument is <c>windowHandle</c>.</summary>
    internal const string InputWindowDescription =
        "Alternative to windowHandle: a window selector with find_windows' filters (plus foreground, index), resolved to " +
        "exactly one window and then used as windowHandle.";

    /// <summary>
    /// The handle a tool that needs a window acts on: <paramref name="handle"/>, or <paramref name="window"/> resolved.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// Neither or both were given (InvalidArguments), or the selector does not name exactly one window (its errorCode, with
    /// candidates for AmbiguousWindow).
    /// </exception>
    public static nint Require(IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle") =>
        Optional(windows, handle, window, handleName) ?? throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
            $"Pass {handleName} (a window handle from find_windows) or window (a selector such as {{\"processName\": \"notepad\"}}). " +
            "Nothing was done.");

    /// <summary>
    /// The handle of an optional window argument: <paramref name="handle"/>, <paramref name="window"/> resolved, or null
    /// when neither was given.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// Both were given (InvalidArguments), or the selector does not name exactly one window.
    /// </exception>
    public static nint? Optional(IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle")
    {
        if (Resolve(windows, handle, window, handleName) is not { } target)
            return null;

        return target.Refusal is { } refusal
            ? throw new ToolErrorException(refusal.ErrorCode, refusal.Error, refusal.Candidates)
            : target.Handle;
    }

    /// <summary>
    /// The handle of one item of a batch, or why the item names no single window. Null when the item names no window at
    /// all; the batch reports that itself.
    /// </summary>
    public static Target? Resolve(IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle")
    {
        if (handle.HasValue && window != null)
        {
            return Target.Refused(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass either {handleName} or window, not both: a selector that names a handle is window.handle. Nothing was done.");
        }

        if (handle is { } value)
            return Target.Of((nint)value);

        if (window == null)
            return null;

        if (window.IsHandleOnly)
            return Target.Of((nint)window.Handle!.Value);

        if (windows == null)
            throw new InvalidOperationException("A window selector needs the window service; the server always registers it.");

        var resolution = windows.ResolveWindow(window);
        return resolution.Success
            ? Target.Of(resolution.Handle)
            : Target.Refused(resolution.ErrorCode ?? nameof(WindowErrorCode.WindowNotFound), resolution.Error ?? "No single window matched.",
                resolution.Candidates.Select(WindowCandidateDto.From).ToList());
    }

    /// <summary>A resolved window, or why there is none.</summary>
    internal sealed record Target(nint Handle, Refusal? Refusal)
    {
        public static Target Of(nint handle) => new(handle, null);

        public static Target Refused(string errorCode, string error, IReadOnlyList<WindowCandidateDto>? candidates = null) =>
            new(nint.Zero, new Refusal(errorCode, error, candidates is { Count: > 0 } ? candidates : null));
    }

    /// <summary>Why a window argument names no single window: the errorCode, the message and the candidates, if any.</summary>
    internal sealed record Refusal(string ErrorCode, string Error, IReadOnlyList<WindowCandidateDto>? Candidates);
}
