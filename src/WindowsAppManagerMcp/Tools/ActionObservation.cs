using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// observe on the action tools (roadmap M3.12, M3.F32): get_desktop_state's default observation of the desktop after the
/// action, returned as the result's <c>observed</c>, and with 'screenshot' a capture of the window as image content before
/// the JSON. element_action has it, and so do click, right_click, double_click, type_text, send_keys and respond_to_dialog.
/// </summary>
/// <remarks>
/// The observation is get_desktop_state's read, so it follows get_desktop_state being served, as its screenshot follows
/// capture_window: a tool refuses an observe it cannot honour before it does anything (<see cref="Error"/>). It reads the
/// window the action was for while that is still a window, else the window in front, which is what the agent sees next. A
/// window the action was refused for (a denied app, the agent's own host) is never read by name: the agent asked for an
/// action there, not a read, so the observation follows the rules for the window in front.
/// </remarks>
public static class ActionObservation
{
    /// <summary>The observe parameter of the input and dialog tools: one text for all of them, so tools/list repeats a short one.</summary>
    public const string ObserveDescription =
        "'state' adds get_desktop_state's observation of the window acted on (else of the window in front) after the action, " +
        "as observed; 'screenshot' also a capture of it as image content. Default: 'none'.";

    /// <summary>
    /// Why observe cannot be honoured (checked before anything is done), or null: the observation is get_desktop_state's
    /// read, so it follows get_desktop_state being served, as the screenshot follows capture_window.
    /// </summary>
    /// <exception cref="InvalidOperationException">observe without the desktop state service, which the server always registers.</exception>
    public static string? Error(IDesktopStateService? desktopState, ObserveMode observe) => observe switch
    {
        ObserveMode.None => null,
        _ when desktopState == null => throw new InvalidOperationException("observe needs the desktop state service; the server always registers it."),
        _ when desktopState.StateUnavailable() is { } stateUnavailable => stateUnavailable,
        ObserveMode.Screenshot => desktopState.ScreenshotUnavailable() is { } unavailable
            ? $"observe 'screenshot': {unavailable} Use observe 'state'."
            : null,
        _ => null
    };

    /// <summary>Refuses an observe that cannot be honoured (<see cref="Error"/>), before the tool does anything.</summary>
    /// <param name="nothingDone">The tool's own last sentence, such as "Nothing was sent."</param>
    /// <exception cref="ToolErrorException">InvalidArguments: get_desktop_state, or for a screenshot capture_window, is not served.</exception>
    public static void Check(IDesktopStateService? desktopState, ObserveMode observe, string nothingDone)
    {
        if (Error(desktopState, observe) is { } error)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, $"{error} {nothingDone}");
        }
    }

    /// <summary>True for an error code that refuses the action for its window: TargetDeniedByPolicy or TargetIsAgentHost.</summary>
    public static bool RefusesWindow(string? errorCode) =>
        errorCode is nameof(InputErrorCode.TargetDeniedByPolicy) or nameof(InputErrorCode.TargetIsAgentHost);

    /// <summary>
    /// The tool result: <paramref name="result"/> alone (isError when it failed), or with observe get_desktop_state's
    /// observation after the action, added by <paramref name="withObserved"/>, and with 'screenshot' its capture as image
    /// content first. The observation is of <paramref name="window"/> while it is still a window, else of the window in front;
    /// also of the window in front when the action was refused for that window (<paramref name="refusedWindow"/>).
    /// </summary>
    /// <param name="window">The window the action was for (zero for none).</param>
    public static async Task<CallToolResult> ResultAsync<T>(
        T result, bool success, long window, bool refusedWindow, ObserveMode observe, Func<T, DesktopState, T> withObserved,
        IDesktopStateService? desktopState, IWindowService? windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(withObserved);
        if (observe == ObserveMode.None || desktopState == null)
        {
            return ToolImages.Create(result, null, isError: !success, ToolJson.SerializerOptions);
        }

        var include = observe == ObserveMode.Screenshot
            ? new HashSet<DesktopStateSection>(DesktopStateRequest.DefaultInclude) { DesktopStateSection.Screenshot }
            : DesktopStateRequest.DefaultInclude;
        nint? observed = !refusedWindow && window != 0 && windows?.IsValidWindow((nint)window) != false ? (nint)window : null;
        var snapshot = await desktopState.GetStateAsync(DesktopStateTools.Request(observed, include), cancellationToken).ConfigureAwait(false);
        return ToolImages.Create(withObserved(result, snapshot.State), snapshot.Image, isError: !success, ToolJson.SerializerOptions);
    }
}
