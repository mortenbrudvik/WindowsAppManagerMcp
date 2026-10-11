using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
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
        "Alternative to handle: a window selector with find_windows' filters (processName is the whole name; plus foreground, " +
        "index), resolved to exactly one window or refused (AmbiguousWindow lists candidates).";

    /// <summary>The description of the window parameter of the input tools, whose window argument is <c>windowHandle</c>.</summary>
    internal const string InputWindowDescription =
        "Alternative to windowHandle: a window selector with find_windows' filters (processName is the whole name; plus foreground, " +
        "index), resolved to exactly one window and then used as windowHandle. {\"foreground\": true} alone counts as no " +
        "windowHandle, and allowHostTarget needs the window named by handle.";

    /// <summary>
    /// The handle a tool that needs a window acts on: <paramref name="handle"/>, or <paramref name="window"/> resolved.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// Neither or both were given (InvalidArguments), or the selector does not name exactly one window (its errorCode, with
    /// candidates for AmbiguousWindow).
    /// </exception>
    public static nint Require(
        IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle", bool requireNamedWindow = false) =>
        Optional(windows, handle, window, handleName, requireNamedWindow) ?? throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
            $"Pass {handleName} (a window handle from find_windows) or window (a selector such as {{\"processName\": \"notepad\"}}). " +
            "Nothing was done.");

    /// <summary>
    /// The handle of an optional window argument: <paramref name="handle"/>, <paramref name="window"/> resolved, or null
    /// when neither was given.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// Both were given (InvalidArguments), or the selector does not name exactly one window.
    /// </exception>
    public static nint? Optional(
        IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle", bool requireNamedWindow = false)
    {
        if (Resolve(windows, handle, window, handleName, requireNamedWindow) is not { } target)
            return null;

        return target.Refusal is { } refusal
            ? throw new ToolErrorException(refusal.ErrorCode, refusal.Error, refusal.Candidates)
            : target.Handle;
    }

    /// <summary>
    /// The handle of one item of a batch, or why the item names no single window. Null when the item names no window at
    /// all; the batch reports that itself.
    /// </summary>
    /// <param name="windows">Resolves the selector.</param>
    /// <param name="handle">The tool's handle argument.</param>
    /// <param name="window">The tool's window selector.</param>
    /// <param name="handleName">The name of the handle argument, for messages.</param>
    /// <param name="requireNamedWindow">
    /// True for the close tools: a bare <c>{"foreground": true}</c> names whatever window is in front, not a window the caller
    /// chose, and is refused (InvalidArguments).
    /// </param>
    public static Target? Resolve(
        IWindowService? windows, long? handle, WindowSelector? window, string handleName = "handle", bool requireNamedWindow = false)
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

        if (requireNamedWindow && IsBareForeground(window))
        {
            return Target.Refused(ToolErrorReporting.InvalidArgumentsErrorCode,
                "window {\"foreground\": true} is whatever window is in front, not a window you named, so it is not closed. Name " +
                "the window to close: its handle (with its processId), or processName, a title or className beside foreground. " +
                "Nothing was done.");
        }

        if (windows == null)
            throw new InvalidOperationException("A window selector needs the window service; the server always registers it.");

        var resolution = windows.ResolveWindow(window);

        // The audit log names the window by the title and process it had now: a close leaves nothing to read afterwards
        AuditScope.NoteWindow(resolution.Window);
        return resolution.Success
            ? Target.Of(resolution.Handle)
            : Target.Refused(resolution.ErrorCode ?? nameof(WindowErrorCode.WindowNotFound), resolution.Error ?? "No single window matched.",
                resolution.Candidates.Select(WindowCandidateDto.From).ToList());
    }

    /// <summary>
    /// True when the selector names its window only as the one in front: foreground with no handle and no filter. That is
    /// the implicit target under another name, so the rules for a window the caller chose (an input tool's destructive
    /// chords and allowHostTarget, the close tools) do not treat it as one.
    /// </summary>
    public static bool IsBareForeground(WindowSelector? window) =>
        window is { Foreground: true, Handle: null, Index: null } && !window.HasFilters;

    /// <summary>
    /// The resolved window checked again inside the input gate, just before the tool acts (roadmap M2.4): a selector with
    /// filters was resolved before the tool waited for the gate, and the window may have changed meanwhile, or closed and
    /// had its handle reused. HandleMismatch when it no longer matches the filters; null when it still does, when the
    /// selector has no filters (a bare handle or foreground), or when the handle is no window any more (the tool's own
    /// check reports InvalidHandle).
    /// </summary>
    public static Refusal? Recheck(IWindowService windows, WindowSelector? window, nint handle)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (window is not { HasFilters: true } || windows.GetWindowInfo(handle) is not { } now)
            return null;

        List<string> differences;
        try
        {
            differences = WindowMatching.Mismatches(now, window);
        }
        catch (RegexMatchTimeoutException)
        {
            return new Refusal(ToolErrorReporting.InvalidArgumentsErrorCode, WindowService.RegexTimeoutMessage(window.TitleRegex), null);
        }

        return differences.Count == 0
            ? null
            : new Refusal(nameof(WindowErrorCode.HandleMismatch),
                $"Window {handle} no longer matches {window.Describe()}: {string.Join("; ", differences)}. It changed, or it closed and " +
                $"its handle was reused ({now.ProcessName}, PID {now.ProcessId}), while this call waited to run. Nothing was done. " +
                "Find the window again with find_windows.", null);
    }

    /// <summary>
    /// <see cref="Recheck"/> for a tool that reports a refusal by throwing.
    /// </summary>
    /// <exception cref="ToolErrorException">The window no longer matches the selector (HandleMismatch).</exception>
    public static void ThrowIfChanged(IWindowService windows, WindowSelector? window, nint handle)
    {
        if (Recheck(windows, window, handle) is { } refusal)
            throw new ToolErrorException(refusal.ErrorCode, refusal.Error, refusal.Candidates);
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
