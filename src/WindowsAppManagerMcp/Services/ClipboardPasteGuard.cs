using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The paste rule of the clipboard's privacy guard: the agent's input pastes only what get_clipboard would return. A
/// paste followed by a read (get_ui_elements, a capture, the page) would otherwise disclose what the user copied, a
/// password a password manager copied included, which get_clipboard refuses.
/// </summary>
public interface IClipboardPasteGuard
{
    /// <summary>
    /// Null when <paramref name="batch"/> does not paste, or may paste what is on the clipboard now; otherwise why it must
    /// not be sent (InputErrorCode.PasteNotAllowed). Call it right before the batch is sent, inside the input gate.
    /// </summary>
    /// <param name="batch">The input about to be sent.</param>
    /// <param name="receivingWindow">The top-level window it goes to (a right click pastes in a terminal); 0 when unknown.</param>
    string? Check(IReadOnlyList<InputEvent> batch, nint receivingWindow);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Input that pastes (<see cref="ClipboardOrigin.PasteTrigger"/>) is sent only when the clipboard holds what set_clipboard
/// put there, the agent's own copy (<see cref="IClipboardOrigin"/>), or content the user let the agent read (a yes to
/// get_clipboard's question, or WAM_ALLOW_USER_CLIPBOARD); and never content marked private unless set_clipboard put it
/// there. Win+V (clipboard history, which holds the user's earlier copies) is sent only with WAM_ALLOW_USER_CLIPBOARD.
/// A clipboard that cannot be checked refuses the paste.
/// </para>
/// <para>
/// Not covered: paste through an application's own menu (Edit, Paste, or a context menu's Paste item chosen by a click
/// or by its access key), drag and drop, and paste shortcuts an application or the user defined. The check runs right
/// before the input; content the user copies in the instant between the check and the input can still be pasted.
/// </para>
/// </remarks>
public sealed class ClipboardPasteGuard : IClipboardPasteGuard
{
    /// <summary>How many characters of set_clipboard's text the check reads to see that the clipboard still holds it.</summary>
    private const int ServerTextProbe = 256;

    private readonly INativeClipboardWrapper _clipboard;
    private readonly IClipboardOrigin _origin;
    private readonly ClipboardOptions _options;
    private readonly INativeWindowWrapper _windows;

    public ClipboardPasteGuard(INativeClipboardWrapper clipboard, IClipboardOrigin origin, ClipboardOptions options, INativeWindowWrapper windows)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public string? Check(IReadOnlyList<InputEvent> batch, nint receivingWindow)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // The class of the window is read only for a right click, the one mouse input that can paste
        var rightButton = batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & Native.NativeEnums.MOUSEEVENTF_RIGHTDOWN) != 0);
        var receivingClass = rightButton && receivingWindow != nint.Zero ? _windows.GetClassName(receivingWindow) : null;
        switch (ClipboardOrigin.PasteTrigger(batch, receivingClass))
        {
            case null:
                return null;
            case ClipboardPasteKind.History:
                return _options.AllowUserClipboard
                    ? null
                    : "The keys press Win+V, which opens clipboard history: it holds what the user copied before, which the agent " +
                      "may not paste or read without the user's consent. Nothing was sent. Put the text on the clipboard with " +
                      "set_clipboard and paste it with '^v', or type it with type_text.";
        }

        var what = rightButton
            ? "A right click in a terminal pastes the clipboard"
            : "The input pastes the clipboard (Ctrl+V, Shift+Insert or the SYN character)";

        // Decided on the number read while the clipboard is open, which belongs to the content that would be pasted
        var before = _clipboard.GetSequenceNumber();
        var kind = _origin.Classify(before);
        if (!IsAgents(kind) && !UserAllowed(before))
        {
            // Copy keys in the same batch have not copied yet when the paste is checked
            return ClipboardOrigin.CanCopy(batch)
                ? NotFromAgent(what) + " The copy keys in this call have not copied yet when the paste is checked: send them " +
                  "in a call of their own first, then the paste."
                : NotFromAgent(what);
        }

        var snapshot = _clipboard.Read(ClipboardFormat.Text, ServerTextProbe, kind == ClipboardOriginKind.SetClipboard ? before : 0);
        if (!snapshot.Opened || snapshot.SequenceNumber != before)
        {
            return $"{what}, but the clipboard could not be checked (another application held it open or changed it meanwhile), " +
                   "so nothing was sent. Try again in a moment.";
        }

        // Content numbered as set_clipboard's is the server's only while it is the server's text
        if (kind == ClipboardOriginKind.SetClipboard &&
            (snapshot.HasHtml || snapshot.HasFiles || (snapshot.HasText && !_origin.IsServerText(snapshot.Text, snapshot.TextLength))))
        {
            kind = ClipboardOriginKind.NotFromAgent;
            if (!UserAllowed(before))
            {
                return NotFromAgent(what);
            }
        }

        if (snapshot.IsPrivate && kind != ClipboardOriginKind.SetClipboard)
        {
            return $"{what}, but the clipboard's content is marked private, as password managers mark the passwords and codes " +
                   "they copy. The agent never pastes it, whoever copied it and whatever the user allowed, because a paste " +
                   "puts it where the agent can read it. Nothing was sent. Ask the user to paste it.";
        }

        return null;
    }

    private bool UserAllowed(uint sequenceNumber) => _options.AllowUserClipboard || _origin.IsUserAllowed(sequenceNumber);

    private static bool IsAgents(ClipboardOriginKind kind) =>
        kind is ClipboardOriginKind.SetClipboard or ClipboardOriginKind.AgentInput;

    private static string NotFromAgent(string what) =>
        $"{what}, but the clipboard holds content the agent did not put there: the user or another application copied it, " +
        "and a paste would put it where the agent can read it. Nothing was sent. Put the text on the clipboard with " +
        "set_clipboard first (or copy it yourself in the app that shows it), or type it with type_text. To paste what the " +
        "user copied, call get_clipboard first: when the user lets the agent read it, the agent may paste it too.";
}
