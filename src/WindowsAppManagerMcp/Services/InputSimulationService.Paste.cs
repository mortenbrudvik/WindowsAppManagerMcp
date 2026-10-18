using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>type_text's paste (roadmap M4.4): the text goes in through the clipboard, which is lent and given back.</summary>
public partial class InputSimulationService
{
    /// <summary>
    /// How long the pasted text stays on the clipboard after Ctrl+V was sent: the application reads the clipboard when it
    /// handles the keys, after SendInput has returned (a browser or Electron app in another process, a moment later).
    /// The clipboard's earlier text goes back after it, so an application slower than this pastes that text instead.
    /// </summary>
    public static readonly TimeSpan PasteSettleTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The virtual key of V: Ctrl+V is that key on every layout, the one Windows calls V.</summary>
    private const ushort VkV = 0x56;

    /// <summary>
    /// What came of a paste inside the input gate: the call's result, or (auto) why it was not pasted, so the text is
    /// typed instead with <paramref name="Approved"/>; with <paramref name="Back"/> when the clipboard was lent and given
    /// back before that.
    /// </summary>
    private sealed record PasteAttempt(TypeTextResult? Result, string? NotPasted, InputGuardResult Approved, ClipboardReturn? Back = null);

    /// <summary>
    /// Why the text must not be pasted, before anything is checked: with delayMs (method paste only; auto types then)
    /// or longer than <see cref="MaxPasteLength"/>. Null when it may be.
    /// </summary>
    private (InputErrorCode Code, string Error)? PasteRefusalBeforeInput(bool explicitPaste, int length, int delayBetweenKeysMs)
    {
        if (explicitPaste && _pasteClipboard == null)
        {
            throw new InvalidOperationException("type_text's paste needs the paste clipboard; the server always registers it.");
        }

        if (explicitPaste && delayBetweenKeysMs > 0)
        {
            return (InputErrorCode.InvalidArguments,
                "delayMs paces keystrokes, and a paste sends none but Ctrl+V. Leave delayMs out to paste, or type the text with " +
                "method 'unicode'. Nothing was typed.");
        }

        return length > MaxPasteLength
            ? (InputErrorCode.TextTooLong,
                $"The text has {Count(length)} characters, more than the {Count(MaxPasteLength)} type_text pastes. Nothing was typed. " +
                "Paste it in parts.")
            : null;
    }

    /// <summary>
    /// Pastes <paramref name="typed"/> inside the input gate, after the guard approved the window. Never into a terminal
    /// (method paste refuses with PasteNotAllowed; auto types instead), nor, for auto, text with a line break into an editor
    /// with a terminal panel. Then lends the clipboard (<see cref="IPasteClipboard.Lend"/>; one that cannot be put back is
    /// ClipboardUnavailable, or auto types), checks the window, the modifiers and the paste rule again right before
    /// Ctrl+V, sends it, waits <see cref="PasteSettleTime"/> and gives the clipboard back, whatever happened in between.
    /// </summary>
    private async Task<PasteAttempt> PasteAsync(string typed, int length, bool explicitPaste, InputTarget target, InputGuardResult approved)
    {
        var clipboard = _pasteClipboard!;
        switch (clipboard.Classify(approved.Receiving))
        {
            case PasteWindowKind.Terminal when explicitPaste:
                return new PasteAttempt(Finish(new TypeTextResult(false, length, 0,
                    $"Nothing was pasted: {Describe(approved.ReceivingWindow)} is a terminal. Ctrl+V does not paste in every " +
                    "terminal, and a pasted line break runs the line as a command, so type_text does not paste there. Type the " +
                    "text with method 'unicode' (with controlChars 'keys' only when each line is meant to run), or ask the user to " +
                    "paste it.", InputErrorCode.PasteNotAllowed), approved), null, approved);
            case PasteWindowKind.Terminal:
                return new PasteAttempt(null, "the window in front is a terminal, where Ctrl+V does not paste everywhere and a " +
                    "pasted line break runs a command", approved);
            case PasteWindowKind.EditorWithTerminal when !explicitPaste && typed.Contains('\n'):
                return new PasteAttempt(null, "the text has line breaks, and the window in front is an editor whose focus may be " +
                    "in its terminal panel, where a pasted line break runs a command (pass method 'paste' to paste into its " +
                    "editor)", approved);
        }

        // A Windows line break is what applications expect of clipboard text
        var loan = clipboard.Lend(typed.Replace("\n", "\r\n"));
        if (!loan.Lent)
        {
            return explicitPaste
                ? new PasteAttempt(Finish(new TypeTextResult(false, length, 0,
                    $"Nothing was pasted: {loan.Refusal}. The clipboard is as it was. Type the text with method 'unicode' (at " +
                    $"most {Count(MaxTypedLength)} characters); or ask the user to empty the clipboard, or, if the user agrees to lose " +
                    "what is on it, put the text there with set_clipboard and paste it with send_keys '^v'.",
                    InputErrorCode.ClipboardUnavailable), approved), null, approved)
                : new PasteAttempt(null, loan.Refusal, approved);
        }

        PasteSend? sent = null;
        ClipboardReturn back;
        try
        {
            sent = SendPaste(length, explicitPaste, target, approved);
        }
        finally
        {
            // Not cut short by a pause or cancellation: given back before the application read it, the clipboard's earlier
            // content would be pasted. Unknown after an exception, so it waits then too
            if (sent?.KeysInserted != false)
            {
                await Task.Delay(_pasteSettleTime).ConfigureAwait(false);
            }

            back = clipboard.GiveBack(loan);
        }

        return sent!.Result is { } result
            ? new PasteAttempt(result with { ClipboardRestored = back.Restored, Warning = back.Warning }, null, sent.Approved)
            : new PasteAttempt(null, sent.NotPasted, sent.Approved, back);
    }

    /// <summary>What sending Ctrl+V did: the call's result, or (auto) why it was not sent.</summary>
    /// <param name="Result">The result; null when auto types instead.</param>
    /// <param name="NotPasted">Why auto did not paste; null with a result.</param>
    /// <param name="Approved">The guard's last approval.</param>
    /// <param name="KeysInserted">True when SendInput inserted any of the keys, so the application may read the clipboard.</param>
    private sealed record PasteSend(TypeTextResult? Result, string? NotPasted, InputGuardResult Approved, bool KeysInserted);

    /// <summary>
    /// Ctrl+V, with the text lent: right before it the guard checks the window again, no modifier may be down, and the
    /// paste rule checks that the clipboard still holds the text (another application can replace it any time).
    /// </summary>
    private PasteSend SendPaste(int length, bool explicitPaste, InputTarget target, InputGuardResult approved)
    {
        const string NothingPasted = "Nothing was pasted.";

        var recheck = _guard.RecheckKeyboard(approved);
        if (!recheck.Allowed)
        {
            return new PasteSend(Finish(new TypeTextResult(false, length, 0, $"{NothingPasted} {recheck.Error}", recheck.ErrorCode), recheck),
                null, recheck, false);
        }

        approved = recheck;
        if (ModifiersDownNow("pasted") is { } downNow)
        {
            return new PasteSend(Finish(new TypeTextResult(false, length, 0, $"{NothingPasted} {downNow.Error}", InputErrorCode.ModifiersHeld)
            {
                HeldModifiers = downNow.Names
            }, approved), null, approved, false);
        }

        var keys = PasteKeys(LayoutFor(target));
        if (PasteRefusal(keys, approved) is { } refused)
        {
            return explicitPaste
                ? new PasteSend(Finish(new TypeTextResult(false, length, 0, refused, InputErrorCode.PasteNotAllowed), approved), null, approved, false)
                : new PasteSend(null, "the clipboard could not be used right before Ctrl+V (another application changed it, or copy " +
                    "keys sent moments ago have not copied yet)", approved, false);
        }

        var sent = Send(keys, approved);
        if (sent.PausedByUser)
        {
            return new PasteSend(Finish(new TypeTextResult(false, length, 0, PausedMessage(NothingPasted), InputErrorCode.PausedByUser), approved),
                null, approved, sent.Inserted > 0);
        }

        if (!sent.Complete)
        {
            return new PasteSend(Finish(new TypeTextResult(false, length, 0,
                $"SendInput inserted {sent.Inserted} of the {sent.Requested} events of Ctrl+V, so the text may or may not have been " +
                "pasted; the keys it pressed were released again. Check the window before you paste again.",
                InputErrorCode.SendInputFailed), approved), null, approved, sent.Inserted > 0);
        }

        return new PasteSend(Finish(new TypeTextResult(true, length, length), approved), null, approved, true);
    }

    /// <summary>A count as the messages write it, the same in every culture: 5,000.</summary>
    private static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Ctrl+V with the scan codes of <paramref name="layout"/>: Ctrl down, V down and up, Ctrl up.</summary>
    private static List<InputEvent> PasteKeys(KeyboardLayout layout) =>
    [
        KeyEvent(NativeEnums.VK_CONTROL, NativeEnums.KEYEVENTF_KEYDOWN, layout), KeyEvent(VkV, NativeEnums.KEYEVENTF_KEYDOWN, layout),
        KeyEvent(VkV, NativeEnums.KEYEVENTF_KEYUP, layout), KeyEvent(NativeEnums.VK_CONTROL, NativeEnums.KEYEVENTF_KEYUP, layout)
    ];
}
