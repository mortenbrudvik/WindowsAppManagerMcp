using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// browser_handle_dialog (roadmap M5.8, dialogs): reads and answers the JavaScript dialog (alert, confirm, prompt,
/// beforeunload) a Chrome or Edge window shows, through UI Automation. DevTools cannot answer it once it is open: Chrome and
/// Edge (155) neither report nor answer a dialog opened before the connection, on a page connection, a flattened or a
/// non-flattened session of the browser connection alike ("No dialog is showing"); the actions' dialog option answers the
/// dialog they open on their own connection instead.
/// </summary>
/// <remarks>
/// <para>
/// Chromium draws the dialog as views inside the browser window (no window of its own, no #32770), the same in every
/// display language: a view of class JavaScriptTabModalDialogViewViews holds the message (Text) and a prompt's field (Edit,
/// Value pattern), and the DialogClientView around it holds the buttons (Invoke), OK first and Cancel second in Chrome and
/// Edge, English and Norwegian alike. Nothing is found by its name: accept presses the first button, dismiss the last
/// (an alert's only button either way), and a prompt's text is set through the Value pattern. Both go through
/// element_action's checks (the target policy, the pause, the audit); no keystrokes are sent.
/// </para>
/// <para>
/// Read before it is accepted: a confirm or prompt is accepted only when its text was read first, by a read of this
/// service (no accept) or the report of the action that opened it, for the same window; otherwise the call reads it and
/// is refused, so the next call can accept it. Dismissing and an alert need no read.
/// </para>
/// </remarks>
public interface IBrowserDialogService
{
    /// <summary>
    /// Reads the dialog <paramref name="window"/> shows, and with <paramref name="accept"/> answers it (setting a prompt's
    /// text to <paramref name="promptText"/> first). DialogNotFound when the window shows none.
    /// </summary>
    Task<BrowserDialogResult> HandleAsync(nint window, bool? accept, string? promptText, CancellationToken cancellationToken = default);

    /// <summary>Notes that the agent was shown the dialog <paramref name="window"/> has open, with its message.</summary>
    void NoteShown(nint window, string message);
}
