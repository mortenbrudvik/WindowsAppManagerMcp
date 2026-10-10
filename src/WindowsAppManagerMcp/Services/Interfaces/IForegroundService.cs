using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Verified window activation: gets a window in front despite the Windows foreground lock and reports
/// which window is actually in front afterwards. focus_window and apply_layout use it, and so does the
/// input guard: keyboard input may only be sent once <see cref="FocusOutcome.IsForeground"/> is true.
/// </summary>
public interface IForegroundService
{
    /// <summary>
    /// Brings <paramref name="handle"/> (its top-level window) to the foreground and verifies it. A window the
    /// server's target policy denies is refused first (TargetDeniedByPolicy), and so is a hidden one
    /// (WindowHidden: keys would go to a window the user cannot see). Then the steps, each followed by a check
    /// of the foreground window:
    /// <list type="number">
    /// <item>Already in front (the window or a dialog it owns, by the owner chain): done, nothing is
    /// changed.</item>
    /// <item>A window that is not responding or is cloaked is refused (NotResponding, Cloaked).</item>
    /// <item>A minimized window is restored. A window that a modal dialog disables is activated through
    /// that dialog, unless the policy denies the dialog's app (TargetDeniedByPolicy).</item>
    /// <item>SetForegroundWindow.</item>
    /// <item>If the foreground lock refused it: attach the activation thread's input to the foreground
    /// thread, BringWindowToTop and SetForegroundWindow, and detach again. Skipped when the foreground
    /// window is not responding or its process has a higher integrity level (UIPI refuses the attach).</item>
    /// <item>Last resort: a zero-distance mouse move makes the server the process that received the last
    /// input, then SetForegroundWindow. No key is sent: an Alt tap would reach the window in front,
    /// often the agent's terminal, and can leave the target in menu mode.</item>
    /// </list>
    /// When an activation step brought the window in front and it is in menu mode, one Esc is sent to
    /// close the menu, but only while the window is verified to be in front and no modifier key is down
    /// (the Esc would be a shortcut such as Ctrl+Esc or Alt+Esc; the menu is left open). A menu that is still active
    /// afterwards, or that is open in a window that was already in front, is reported in
    /// <see cref="FocusOutcome.InMenuMode"/>; keys would then select menu items.
    /// All attach and activation calls run on one dedicated long-lived thread, so an attach and its
    /// detach always happen on the same thread.
    /// </summary>
    /// <param name="handle">The window to bring to the front.</param>
    /// <param name="timeoutMs">
    /// How long to wait, in total, for Windows to show the window in front (clamped to 0-10000). The
    /// call can take somewhat longer when a window is slow to answer.
    /// </param>
    FocusOutcome EnsureForeground(nint handle, int timeoutMs = 1000);

    /// <summary>
    /// True when <paramref name="handle"/> (its top-level window) or a window it owns, such as a dialog,
    /// is the foreground window right now: the target is in the foreground window's owner chain, so WPF
    /// and WinForms dialogs count too (WindowOwnerChain, the owner rule the close and input guards use).
    /// The same rule <see cref="EnsureForeground"/> verifies with. Only
    /// reads, on the calling thread, so it is cheap enough to check again before every input batch.
    /// </summary>
    bool IsInFront(nint handle);
}
