using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>What kind of window a paste of text would go to, as type_text's paste rules see it (roadmap M4.4).</summary>
public enum PasteWindowKind
{
    /// <summary>An application window, in which Ctrl+V pastes the text.</summary>
    Application,

    /// <summary>
    /// A terminal (<see cref="ClipboardOrigin.RightClickPasteClasses"/>, or a terminal's process of
    /// <see cref="ClipboardOrigin.RightClickPasteProcesses"/>): Ctrl+V does not paste in all of them (PuTTY sends it on as a
    /// control character), and a pasted line break runs the line as a command.
    /// </summary>
    Terminal,

    /// <summary>
    /// An editor with a terminal panel (VS Code and the editors built on it): the server cannot tell the panel, where a pasted
    /// line break runs a command, from the editor.
    /// </summary>
    EditorWithTerminal
}

/// <summary>The clipboard lent to one paste (<see cref="IPasteClipboard.Lend"/>): what to put back, or why it was not lent.</summary>
public sealed class ClipboardLoan
{
    private ClipboardLoan(string? refusal, string? savedText, bool savedIsAgents, uint pasteSequence)
    {
        Refusal = refusal;
        SavedText = savedText;
        SavedIsAgents = savedIsAgents;
        PasteSequence = pasteSequence;
    }

    /// <summary>True when the text is on the clipboard, which must be given back.</summary>
    public bool Lent => Refusal == null;

    /// <summary>Why the clipboard was not lent, in words that fit after "the clipboard was not used: "; null when it was.</summary>
    public string? Refusal { get; }

    /// <summary>The text the clipboard held before; null when it was empty.</summary>
    internal string? SavedText { get; }

    /// <summary>True when that text was the agent's (set_clipboard's, or its own copy), so it stays the agent's when put back.</summary>
    internal bool SavedIsAgents { get; }

    /// <summary>The clipboard's sequence number with the pasted text on it.</summary>
    internal uint PasteSequence { get; }

    internal static ClipboardLoan Refused(string refusal) => new(refusal, null, false, 0);

    internal static ClipboardLoan Of(string? savedText, bool savedIsAgents, uint pasteSequence) =>
        new(null, savedText, savedIsAgents, pasteSequence);
}

/// <summary>What giving the clipboard back did.</summary>
/// <param name="Restored">True when the clipboard holds what it held before the paste again (or is empty again).</param>
/// <param name="Warning">What it holds instead when it does not; null when it was restored.</param>
public sealed record ClipboardReturn(bool Restored, string? Warning = null);

/// <summary>
/// The clipboard for type_text's paste (roadmap M4.4): it saves the clipboard's text, puts the text to paste there and
/// puts the saved text back after the paste. Only a clipboard it can put back exactly is lent: empty, or plain text that
/// is not marked private. Formatted text, images, files, an application's own data and content a password manager marked
/// private are never replaced, so type_text types instead (auto) or refuses (paste).
/// </summary>
public interface IPasteClipboard
{
    /// <summary>What kind of window <paramref name="window"/> (a top-level window) is for a paste.</summary>
    PasteWindowKind Classify(nint window);

    /// <summary>
    /// Saves the clipboard and puts <paramref name="text"/> there, kept out of clipboard history and cloud sync, as the
    /// agent's (set_clipboard's) content, so the paste rule lets Ctrl+V paste it only while it is still there. Changes
    /// nothing when the clipboard cannot be put back afterwards, cannot be opened, or changes while it is saved.
    /// </summary>
    ClipboardLoan Lend(string text);

    /// <summary>
    /// Puts the saved text back (or empties the clipboard when it was empty), only while the clipboard still holds the
    /// pasted text: content another application put there meanwhile is left alone.
    /// </summary>
    ClipboardReturn GiveBack(ClipboardLoan loan);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The saved text is read into the server's memory for the length of the call and never returned or logged; content
/// marked private is not even read (<see cref="INativeClipboardWrapper.Read"/>). The text put back carries the same
/// privacy markers as set_clipboard's, so the paste adds no entry to clipboard history or cloud sync; get_clipboard then
/// treats it as private, unless it was the agent's own before, which stays the agent's.
/// </para>
/// <para>
/// Lossy, and best effort: the text goes back after a fixed wait, so an application that reads the clipboard later than
/// that pastes the saved text instead; and a clipboard another application opened, or changed, during the paste is not
/// put back.
/// </para>
/// </remarks>
public sealed class PasteClipboard : IPasteClipboard
{
    /// <summary>The longest clipboard text that is saved and put back; a longer one is not replaced.</summary>
    public const int MaxRestorableLength = 4_000_000;

    /// <summary>
    /// The editors of <see cref="ClipboardOrigin.RightClickPasteProcesses"/>: the rest of that list are terminals of their
    /// own.
    /// </summary>
    private static readonly IReadOnlySet<string> EditorProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf"
    };

    private readonly INativeClipboardWrapper _clipboard;
    private readonly IClipboardOrigin _origin;
    private readonly INativeWindowWrapper _windows;

    public PasteClipboard(INativeClipboardWrapper clipboard, IClipboardOrigin origin, INativeWindowWrapper windows)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public PasteWindowKind Classify(nint window)
    {
        if (window == nint.Zero)
        {
            return PasteWindowKind.Application;
        }

        var processId = _windows.GetWindowThreadProcessId(window);
        var process = processId == 0 ? null : _windows.GetProcessName(processId);
        if (process != null && EditorProcesses.Contains(process))
        {
            return PasteWindowKind.EditorWithTerminal;
        }

        return ClipboardOrigin.IsRightClickPasteWindow(_windows.GetClassName(window), process)
            ? PasteWindowKind.Terminal
            : PasteWindowKind.Application;
    }

    public ClipboardLoan Lend(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // set_clipboard's own text carries the privacy markers, and may be read; nothing else marked private is
        var before = _clipboard.GetSequenceNumber();
        var kind = _origin.Classify(before);
        var saved = _clipboard.Read(ClipboardFormat.Text, MaxRestorableLength, kind == ClipboardOriginKind.SetClipboard ? before : 0);
        if (!saved.Opened)
        {
            return ClipboardLoan.Refused("another application held the clipboard open, so it could not be saved; try again in a moment");
        }

        var unchanged = saved.SequenceNumber == before;
        var ours = unchanged && kind == ClipboardOriginKind.SetClipboard && saved.HasText && !saved.HasOtherFormats &&
                   _origin.IsServerText(saved.Text, saved.TextLength);
        if (Unrestorable(saved, ours) is { } why)
        {
            return ClipboardLoan.Refused(why);
        }

        var savedText = saved.HasText ? saved.Text : null;
        var lent = _clipboard.ReplaceText(text, saved.SequenceNumber);
        if (lent.Changed)
        {
            return ClipboardLoan.Refused("another application changed the clipboard while it was saved; try again in a moment");
        }

        if (!lent.Opened)
        {
            return ClipboardLoan.Refused("another application held the clipboard open, so the text was not put on it; try again in a moment");
        }

        if (!lent.Success)
        {
            // The clipboard may have been emptied before the write failed: the saved content goes back
            if (_clipboard.GetSequenceNumber() is var now && now != saved.SequenceNumber)
            {
                _clipboard.ReplaceText(savedText, now);
            }

            return ClipboardLoan.Refused($"Windows did not put the text on the clipboard ({lent.Error})");
        }

        // The paste rule pastes only the agent's content: this text is, while it is there
        _origin.ServerWrote(lent.SequenceNumber, text);
        return ClipboardLoan.Of(savedText, ours || (unchanged && kind == ClipboardOriginKind.AgentInput), lent.SequenceNumber);
    }

    public ClipboardReturn GiveBack(ClipboardLoan loan)
    {
        ArgumentNullException.ThrowIfNull(loan);
        if (!loan.Lent)
        {
            return new ClipboardReturn(true);
        }

        // A second try when another application holds the clipboard open: the pasted text must not stay there
        var back = _clipboard.ReplaceText(loan.SavedText, loan.PasteSequence);
        if (!back.Opened)
        {
            back = _clipboard.ReplaceText(loan.SavedText, loan.PasteSequence);
        }

        if (back.Changed)
        {
            _origin.ServerWrote(0, string.Empty);
            return new ClipboardReturn(false,
                "Another application put something on the clipboard during the paste, so the clipboard was left as it is: what it " +
                "held before the paste was not put back.");
        }

        if (!back.Opened || !back.Success)
        {
            return new ClipboardReturn(false,
                $"The clipboard could not be put back ({back.Error ?? "another application held it open"}): the pasted text is " +
                "still on it, kept out of clipboard history. Tell the user that their clipboard was replaced.");
        }

        if (loan.SavedIsAgents && loan.SavedText != null)
        {
            _origin.ServerWrote(back.SequenceNumber, loan.SavedText);
        }
        else
        {
            _origin.ServerWrote(0, string.Empty);
        }

        return new ClipboardReturn(true);
    }

    /// <summary>Why the clipboard of <paramref name="saved"/> cannot be put back exactly after a paste; null when it can.</summary>
    private static string? Unrestorable(ClipboardSnapshot saved, bool ours)
    {
        if (saved.IsPrivate && !ours)
        {
            return "the clipboard holds content marked private (as password managers mark what they copy), which the server " +
                   "neither reads nor replaces";
        }

        if (saved.HasOtherFormats)
        {
            return "the clipboard holds more than plain text (formatted text, an image, files or an application's own data), " +
                   "which the server could not put back after the paste";
        }

        if (saved.Error != null)
        {
            return $"the clipboard's text could not be read ({saved.Error}), so it could not be put back after the paste";
        }

        return saved.HasText && (saved.Text == null || saved.TextLength > MaxRestorableLength)
            ? $"the clipboard's text is longer than {MaxRestorableLength.ToString("N0", CultureInfo.InvariantCulture)} characters, which the server does not save"
            : null;
    }
}
