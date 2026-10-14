using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// get_desktop_state (roadmap M3.12): one compact observation of the desktop, the recommended first call. It reads, in one
/// call and under one deadline: the window in front, the cursor, the modifiers that are down, Caps Lock and the pause (always);
/// and, as asked, the user-facing windows front first, the element with keyboard focus, the target window's open popups, its
/// interactive elements as text lines with refs, and a capture of it. element_action's <c>observe</c> returns the same
/// observation after the action.
/// </summary>
/// <remarks>
/// <para><b>Never long.</b> The parts run side by side; whatever has not answered by the deadline is reported in the
/// result's errors (OperationTimedOut) and the rest is returned. A part that fails (a window that does not respond, a provider
/// that throws, a capture refused) is listed the same way: only a named window that is no window fails the call.</para>
/// <para><b>Through the shared reads.</b> Elements come from the UI Automation walk of get_ui_elements (refs registered in the
/// element registry, the hung-window precheck, the web-content warm-up), the focused element from get_focused_element's read,
/// the popups from <see cref="IPopupService"/> (get_active_popups and find_ui_elements' windowAndPopups scope, with the same
/// default), and the capture from capture_window's path (a captureId the mouse tools take). A capture that marks the
/// elements (<see cref="DesktopStateRequest.ScreenshotMarks"/>, roadmap M3.F32) takes capture_with_elements' path instead,
/// after the walk, with the walk's own elements, and draws no marks on a window that moved during the walk. Nothing here
/// sends input, activates a window or changes one.</para>
/// <para><b>What is not read.</b> When no window is named, the window in front is read, unless it is the agent's own host
/// window (TargetIsAgentHost: the agent's terminal holds the conversation, not the app it means) or one the user's target
/// policy denies (TargetDeniedByPolicy): a sweep the agent did not aim reads neither, as find_ui_elements' desktop scope.
/// A named window is read as get_ui_elements reads it. The focused element follows the same rule for the window that holds
/// it (the window in front when the read does not say): it is left out, with a focus error, when that window was not named
/// and is the host or denied, or cannot be read to be checked. The capture follows capture_window: never of a denied window
/// (nor of one that cannot be read to be checked), and only while capture_window is served
/// (<see cref="ScreenshotUnavailable"/>).</para>
/// </remarks>
public interface IDesktopStateService
{
    /// <summary>
    /// Why a capture cannot be part of the observation, or null when it can: the user turned captures off (capture_window is
    /// not served: the capture toolset, an exclusion or read-only mode). The tools refuse a screenshot request with it before
    /// they do anything.
    /// </summary>
    string? ScreenshotUnavailable();

    /// <summary>
    /// Why the observation cannot be returned by another tool (element_action's observe), or null when it can: it is
    /// get_desktop_state's read, so it follows get_desktop_state being served (not served: the elements toolset is off, or
    /// an exclusion). Those tools refuse observe with it before they do anything.
    /// </summary>
    string? StateUnavailable();

    /// <summary>Reads the desktop as <paramref name="request"/> says, within its deadline.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DesktopStateSnapshot> GetStateAsync(DesktopStateRequest request, CancellationToken cancellationToken);
}
