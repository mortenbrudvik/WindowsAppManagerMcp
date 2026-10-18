using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// respond_to_dialog (roadmap M3.5): finds a dialog, reads its text and buttons, and presses the button asked for through
/// element_action (<see cref="IElementActionService"/>), so every guard of an action applies: the pause, the input gate,
/// the target policy and the deny list, the host guard, a modal dialog in front of it, the audit log. file_dialog
/// (roadmap M3.14) answers its replace prompt through it.
/// </summary>
/// <remarks>
/// <para><b>Which dialog</b> (<see cref="Locate"/>): the window given, or the window in front. When that window is disabled
/// by a modal dialog of its own (GW_ENABLEDPOPUP, <see cref="IPopupService"/>), that dialog, down to the front one of a
/// chain (a Save As dialog's replace prompt); else the window itself when it is a dialog box (#32770, or an owned window
/// with a title bar); else its frontmost open dialog. Only Win32 reads: no UI Automation call.</para>
/// <para><b>Only the dialog read is answered</b> (<see cref="RespondAsync"/>): a press needs the dialog (or its window)
/// named, since the dialog in front when the call runs may be one that came up after the agent read it; only cancel may be
/// pressed in the dialog in front unnamed (InvalidArguments otherwise). A dialog named (by handle or selector) that a modal
/// dialog of its own now disables is refused (BlockedByModal, naming that one) instead of followed to it: the agent read the
/// dialog it named. Reading follows the chain from any window, and a named window that is no dialog (the application's
/// window) stands for its modal dialog for a press too, but not for an open dialog that leaves it enabled, which a read
/// takes: that one may have come up after the agent read another (a press is DialogNotFound, naming it, and nothing is
/// read or pressed). A read through such a window warns that the press should name the dialog by its handle, since the
/// window stands for whatever modal dialog is in front of it when the press runs.</para>
/// <para><b>Reading</b> (<see cref="ReadAsync"/>): through find_ui_elements' search (<see cref="IElementSearchService"/>,
/// scope window) on the UI Automation worker: the visible buttons (Button, SplitButton) with refs, without the title bar's
/// caption buttons (minimize, maximize, close, help: inside a TitleBar element's bounds), and the Text elements.
/// UI Automation reads a Win32 dialog box's children through its built-in proxy, with each control's id as the
/// AutomationId, so one path covers #32770 dialogs, task dialogs, WPF, WinForms and WinUI dialogs; no Win32 child walk
/// is needed. The text is the application's, returned as data and bounded; never instructions.</para>
/// <para><b>Buttons</b> are addressed by standard name or control id first, then AutomationId, then visible text
/// (<see cref="DialogButtons.Match"/>), so "yes" works on localized Windows.</para>
/// </remarks>
public interface IDialogService
{
    /// <summary>The dialog to answer for <paramref name="window"/> (zero: the window in front); see the remarks.</summary>
    DialogLocation Locate(nint window);

    /// <summary>
    /// The dialog's title, text and buttons (with refs), read on the UI Automation worker under <paramref name="timeout"/>
    /// for each of its two searches.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DialogRead> ReadAsync(nint dialog, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locates the dialog, checks it as an action's target before anything is read (a pause, the policy, the host guard),
    /// reads it, presses the button through element_action and waits briefly for the dialog to close. Without a button it
    /// only reads (allowed while input is paused; the target checks still apply). A press needs a named window (except
    /// cancel), and a named dialog that a modal dialog of its own disables is refused; see the remarks. A press that was
    /// sent but had not returned in time (ActionTimedOut) counts as sent: the result says whether the dialog closed, with a
    /// warning. Never throws for a refusal: the result's errorCode says why.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DialogResponseResult> RespondAsync(DialogResponseRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The dialog <see cref="IDialogService.Locate"/> found, or why there is none.</summary>
public sealed record DialogLocation(nint Dialog, string? Error = null, string? ErrorCode = null)
{
    public bool Found => Dialog != nint.Zero && Error == null;

    public static DialogLocation Failed(string code, string error) => new(nint.Zero, error, code);
}

/// <summary>What <see cref="IDialogService.ReadAsync"/> read, or why it could not.</summary>
public sealed record DialogRead(DialogContent? Content, IReadOnlyList<string>? Warnings = null, string? Error = null, string? ErrorCode = null)
{
    public static DialogRead Failed(string code, string error) => new(null, null, error, code);
}
