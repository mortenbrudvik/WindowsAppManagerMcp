using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A key, character or mouse button that is down: one the server sent down and has not sent up yet, or a
/// modifier key that Windows reports down (roadmap M1.10).
/// </summary>
/// <param name="Kind">A virtual key, a character sent as Unicode, or a mouse button.</param>
/// <param name="Code">The virtual-key code, the character, or the button's MOUSEEVENTF_*DOWN flag.</param>
/// <param name="Flags">
/// For a virtual key, KEYEVENTF_EXTENDEDKEY when the key was sent as an extended key; for an X button (MOUSEEVENTF_XDOWN),
/// which one: XBUTTON1 or XBUTTON2, as in the event's mouseData; otherwise 0.
/// </param>
/// <param name="Scan">
/// For a virtual key, the scan code it went down with (KEYBDINPUT.wScan, without the 0xE0 prefix, which
/// <paramref name="Flags"/>' extended flag carries), sent again with its key-up: remote desktop and virtual machine clients
/// and games track keys by scan code, and a key-up without it leaves the key down there. 0 when not known; otherwise 0.
/// </param>
public readonly record struct HeldInput(InputEventKind Kind, ushort Code, uint Flags = 0, ushort Scan = 0)
{
    /// <summary>A virtual key.</summary>
    public static HeldInput Key(ushort virtualKey, bool extended = false) =>
        new(InputEventKind.VirtualKey, virtualKey, extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0);

    /// <summary>
    /// A modifier key Windows reports down, which the server did not press (the user's, or a stuck one): with its
    /// extended flag and the scan code that key has on every keyboard (<see cref="KeyNames.ModifierScanCode"/>), so that
    /// its release reaches the applications that track keys by scan code.
    /// </summary>
    public static HeldInput Modifier(ushort virtualKey) =>
        Key(virtualKey, KeyNames.IsExtended(virtualKey)) with { Scan = KeyNames.ModifierScanCode(virtualKey) };

    /// <summary>A mouse button, by its MOUSEEVENTF_LEFTDOWN, RIGHTDOWN or MIDDLEDOWN flag.</summary>
    public static HeldInput Button(uint downFlag) => new(InputEventKind.Mouse, (ushort)downFlag);

    /// <summary>An X button (roadmap M4.3), by its mouseData: XBUTTON1 or XBUTTON2.</summary>
    public static HeldInput XButton(uint xButton) => new(InputEventKind.Mouse, (ushort)NativeEnums.MOUSEEVENTF_XDOWN, xButton);

    /// <summary>The name reported to the agent, such as "LeftCtrl", "A" or "left mouse button".</summary>
    public string Name => Kind switch
    {
        InputEventKind.VirtualKey => KeyNames.Of(Code),
        InputEventKind.Unicode => char.IsControl((char)Code) || char.IsWhiteSpace((char)Code)
            ? $"character U+{Code:X4}"
            : $"character '{(char)Code}'",
        _ => Code switch
        {
            (ushort)NativeEnums.MOUSEEVENTF_LEFTDOWN => "left mouse button",
            (ushort)NativeEnums.MOUSEEVENTF_RIGHTDOWN => "right mouse button",
            (ushort)NativeEnums.MOUSEEVENTF_MIDDLEDOWN => "middle mouse button",
            (ushort)NativeEnums.MOUSEEVENTF_XDOWN when Flags == NativeEnums.XBUTTON1 => "X1 mouse button",
            (ushort)NativeEnums.MOUSEEVENTF_XDOWN when Flags == NativeEnums.XBUTTON2 => "X2 mouse button",
            _ => $"mouse button 0x{Code:X}"
        }
    };

    /// <summary>True for Shift, Ctrl, Alt and the Windows keys, generic or left/right.</summary>
    public bool IsModifier => Kind == InputEventKind.VirtualKey && KeyNames.IsModifier(Code);

    /// <summary>
    /// True for Alt and the Windows keys: releasing one of them on its own opens the menu bar or the Start
    /// menu, so a release sends a masking key first.
    /// </summary>
    public bool OpensAMenuWhenReleasedAlone => Kind == InputEventKind.VirtualKey &&
        Code is NativeEnums.VK_MENU or NativeEnums.VK_LMENU or NativeEnums.VK_RMENU or NativeEnums.VK_LWIN or NativeEnums.VK_RWIN;
}

/// <summary>Names of the virtual keys the input session reports and the pause hotkey setting understands.</summary>
public static class KeyNames
{
    /// <summary>VK_CANCEL: what the Pause/Break key sends while Ctrl is down (Ctrl+Break).</summary>
    public const ushort VkCancel = 0x03;

    /// <summary>The modifier keys Windows reports left and right apart, in the order they are reported.</summary>
    public static IReadOnlyList<ushort> SidedModifiers { get; } =
    [
        NativeEnums.VK_LCONTROL, NativeEnums.VK_RCONTROL, NativeEnums.VK_LMENU, NativeEnums.VK_RMENU,
        NativeEnums.VK_LSHIFT, NativeEnums.VK_RSHIFT, NativeEnums.VK_LWIN, NativeEnums.VK_RWIN
    ];

    private static readonly Dictionary<ushort, string> Names = BuildNames();

    /// <summary>The name of a virtual key, such as "LeftCtrl", "F4" or "A"; "VK 0xNN" for one without a name.</summary>
    public static string Of(ushort virtualKey) =>
        Names.TryGetValue(virtualKey, out var name) ? name : $"VK 0x{virtualKey:X2}";

    /// <summary>True for Shift, Ctrl, Alt and the Windows keys, generic or left/right.</summary>
    public static bool IsModifier(ushort virtualKey) => virtualKey is
        NativeEnums.VK_SHIFT or NativeEnums.VK_CONTROL or NativeEnums.VK_MENU or
        NativeEnums.VK_LSHIFT or NativeEnums.VK_RSHIFT or NativeEnums.VK_LCONTROL or NativeEnums.VK_RCONTROL or
        NativeEnums.VK_LMENU or NativeEnums.VK_RMENU or NativeEnums.VK_LWIN or NativeEnums.VK_RWIN;

    /// <summary>
    /// The scan code (without the 0xE0 prefix) of a modifier key: Shift, Ctrl, Alt or Windows, generic (its left key) or
    /// left/right; 0 for any other key. The modifiers sit at the same place on every PC keyboard, so their scan codes are
    /// the same in every layout: left Shift 2A, right Shift 36, Ctrl 1D (the right one E0 1D), Alt 38 (the right one
    /// E0 38), Windows E0 5B and E0 5C.
    /// </summary>
    public static ushort ModifierScanCode(ushort virtualKey) => virtualKey switch
    {
        NativeEnums.VK_SHIFT or NativeEnums.VK_LSHIFT => 0x2A,
        NativeEnums.VK_RSHIFT => 0x36,
        NativeEnums.VK_CONTROL or NativeEnums.VK_LCONTROL or NativeEnums.VK_RCONTROL => 0x1D,
        NativeEnums.VK_MENU or NativeEnums.VK_LMENU or NativeEnums.VK_RMENU => 0x38,
        NativeEnums.VK_LWIN => 0x5B,
        NativeEnums.VK_RWIN => 0x5C,
        _ => 0
    };

    /// <summary>
    /// True for the keys Windows sends as extended keys: the right Ctrl and Alt, the Windows keys and the
    /// navigation block.
    /// </summary>
    public static bool IsExtended(ushort virtualKey) => virtualKey is
        NativeEnums.VK_RCONTROL or NativeEnums.VK_RMENU or NativeEnums.VK_LWIN or NativeEnums.VK_RWIN or
        NativeEnums.VK_APPS or NativeEnums.VK_INSERT or NativeEnums.VK_DELETE or NativeEnums.VK_HOME or
        NativeEnums.VK_END or NativeEnums.VK_PRIOR or NativeEnums.VK_NEXT or NativeEnums.VK_LEFT or
        NativeEnums.VK_RIGHT or NativeEnums.VK_UP or NativeEnums.VK_DOWN or NativeEnums.VK_NUMLOCK or
        NativeEnums.VK_SNAPSHOT;

    /// <summary>
    /// The virtual key a key name in the pause hotkey setting stands for (case-insensitive): a letter, a
    /// digit, F1-F24, or one of the named keys. Null for anything else, modifiers included.
    /// </summary>
    public static ushort? Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = name.Trim().ToUpperInvariant();
        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            return key[0];
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 24)
        {
            return (ushort)(NativeEnums.VK_F1 + function - 1);
        }

        return key switch
        {
            "PAUSE" or "BREAK" => NativeEnums.VK_PAUSE,
            "ESC" or "ESCAPE" => NativeEnums.VK_ESCAPE,
            "SPACE" => NativeEnums.VK_SPACE,
            "ENTER" or "RETURN" => NativeEnums.VK_RETURN,
            "TAB" => NativeEnums.VK_TAB,
            "BACKSPACE" => NativeEnums.VK_BACK,
            "INSERT" or "INS" => NativeEnums.VK_INSERT,
            "DELETE" or "DEL" => NativeEnums.VK_DELETE,
            "HOME" => NativeEnums.VK_HOME,
            "END" => NativeEnums.VK_END,
            "PGUP" or "PAGEUP" => NativeEnums.VK_PRIOR,
            "PGDN" or "PAGEDOWN" => NativeEnums.VK_NEXT,
            "UP" => NativeEnums.VK_UP,
            "DOWN" => NativeEnums.VK_DOWN,
            "LEFT" => NativeEnums.VK_LEFT,
            "RIGHT" => NativeEnums.VK_RIGHT,
            "SCROLLLOCK" or "SCROLL" => NativeEnums.VK_SCROLL,
            _ => null
        };
    }

    private static Dictionary<ushort, string> BuildNames()
    {
        var names = new Dictionary<ushort, string>
        {
            [NativeEnums.VK_SHIFT] = "Shift",
            [NativeEnums.VK_CONTROL] = "Ctrl",
            [NativeEnums.VK_MENU] = "Alt",
            [NativeEnums.VK_LSHIFT] = "LeftShift",
            [NativeEnums.VK_RSHIFT] = "RightShift",
            [NativeEnums.VK_LCONTROL] = "LeftCtrl",
            [NativeEnums.VK_RCONTROL] = "RightCtrl",
            [NativeEnums.VK_LMENU] = "LeftAlt",
            [NativeEnums.VK_RMENU] = "RightAlt",
            [NativeEnums.VK_LWIN] = "LeftWin",
            [NativeEnums.VK_RWIN] = "RightWin",
            [NativeEnums.VK_BACK] = "Backspace",
            [NativeEnums.VK_TAB] = "Tab",
            [NativeEnums.VK_RETURN] = "Enter",
            [NativeEnums.VK_PAUSE] = "Pause",
            [VkCancel] = "Break",
            [NativeEnums.VK_CAPITAL] = "CapsLock",
            [NativeEnums.VK_ESCAPE] = "Esc",
            [NativeEnums.VK_SPACE] = "Space",
            [NativeEnums.VK_PRIOR] = "PgUp",
            [NativeEnums.VK_NEXT] = "PgDn",
            [NativeEnums.VK_END] = "End",
            [NativeEnums.VK_HOME] = "Home",
            [NativeEnums.VK_LEFT] = "Left",
            [NativeEnums.VK_UP] = "Up",
            [NativeEnums.VK_RIGHT] = "Right",
            [NativeEnums.VK_DOWN] = "Down",
            [NativeEnums.VK_SNAPSHOT] = "PrintScreen",
            [NativeEnums.VK_INSERT] = "Insert",
            [NativeEnums.VK_DELETE] = "Delete",
            [NativeEnums.VK_APPS] = "Apps",
            [NativeEnums.VK_NUMLOCK] = "NumLock",
            [NativeEnums.VK_SCROLL] = "ScrollLock"
        };

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }

        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = c.ToString();
        }

        for (var f = 1; f <= 24; f++)
        {
            names[(ushort)(NativeEnums.VK_F1 + f - 1)] = $"F{f}";
        }

        return names;
    }
}

/// <summary>
/// What <c>IInputSession.Send</c> did with one batch: refused it because the user paused input, or passed
/// it to SendInput, which inserted <see cref="Inserted"/> of <see cref="Requested"/> events.
/// </summary>
public readonly record struct InputSendOutcome(bool PausedByUser, int Inserted, int Requested)
{
    /// <summary>True when every event of the batch was inserted.</summary>
    public bool Complete => !PausedByUser && Inserted == Requested;
}

/// <summary>What a release of held input did.</summary>
/// <param name="Released">The keys and buttons whose up event was sent.</param>
/// <param name="StillHeld">The keys and buttons that could not be released (SendInput inserted too few events, or failed).</param>
/// <param name="PausedByUser">True when the release was refused because the user paused input (user-held keys only).</param>
/// <param name="Error">Why SendInput failed, when it threw.</param>
public sealed record InputReleaseOutcome(
    IReadOnlyList<string> Released,
    IReadOnlyList<string> StillHeld,
    bool PausedByUser = false,
    string? Error = null)
{
    /// <summary>Nothing was down, so nothing was sent.</summary>
    public static InputReleaseOutcome Nothing { get; } = new([], []);

    /// <summary>Refused: the user paused input.</summary>
    public static InputReleaseOutcome Paused { get; } = new([], [], PausedByUser: true);

    /// <summary>True when nothing is left down.</summary>
    public bool Complete => !PausedByUser && StillHeld.Count == 0;
}

/// <summary>The last release of held input that sent something, for get_control_status.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Reason">Why: a short SendInput count, a cancelled call, the pause, the server stopping, release_all_input, ...</param>
/// <param name="Released">What was released.</param>
/// <param name="StillHeld">What could not be released.</param>
/// <param name="Error">Why SendInput failed, when it threw.</param>
public sealed record InputReleaseRecord(
    DateTimeOffset At,
    string Reason,
    IReadOnlyList<string> Released,
    IReadOnlyList<string> StillHeld,
    string? Error = null);

/// <summary>Whether the user paused the server's input, and since when.</summary>
/// <param name="Paused">True while input is paused.</param>
/// <param name="PausedBy">How it was paused ("hotkey", or "shared_pause_unreadable" when the shared pause could not be read); null while running.</param>
/// <param name="PausedAt">When this server paused; null while running.</param>
/// <param name="ResumedBy">
/// How it was last resumed ("hotkey", "resume_input" after the user confirmed, or "another_server"); null if never.
/// </param>
/// <param name="ResumedAt">When it was last resumed; null if never.</param>
/// <param name="PauseNumber">
/// How many times this server has paused since it started: resume_input resumes only the pause it asked the user
/// about, not one the user made while the question was open.
/// </param>
public sealed record InputPauseState(
    bool Paused,
    string? PausedBy = null,
    DateTimeOffset? PausedAt = null,
    string? ResumedBy = null,
    DateTimeOffset? ResumedAt = null,
    long PauseNumber = 0)
{
    /// <summary>Input is running and was never paused.</summary>
    public static InputPauseState Running { get; } = new(false);
}

/// <summary>How the input was paused or resumed.</summary>
public static class InputControlSources
{
    /// <summary>The user pressed the pause hotkey.</summary>
    public const string Hotkey = "hotkey";

    /// <summary>resume_input, after the user confirmed it.</summary>
    public const string ResumeInput = "resume_input";

    /// <summary>
    /// Another server on this desktop resumed the shared pause: its pause hotkey, or its resume_input after the
    /// user confirmed.
    /// </summary>
    public const string OtherServer = "another_server";

    /// <summary>
    /// The pause every server on the desktop shares could not be read, so this server cannot tell whether the user paused
    /// input in another one: it pauses itself (fails closed) until the user resumes.
    /// </summary>
    public const string SharedPauseUnreadable = "shared_pause_unreadable";

    /// <summary>
    /// Why input is paused, as the PausedByUser messages tell the agent: the user pressed the pause hotkey
    /// (<paramref name="chord"/>, when known), or, for <see cref="SharedPauseUnreadable"/>, the server paused itself. The
    /// user did not make that pause, so the agent must not tell them they did; what it does is the same: stop and ask.
    /// </summary>
    /// <param name="pausedBy">How it was paused (<see cref="InputPauseState.PausedBy"/>).</param>
    /// <param name="chord">The pause hotkey's chord; null when it is not known.</param>
    public static string PauseCause(string? pausedBy, string? chord) => pausedBy == SharedPauseUnreadable
        ? "this server could not read the pause it shares with the other Windows App Manager servers on this desktop, so it " +
          "paused itself in case the user paused input in another one"
        : $"the user pressed the pause hotkey{(chord is null ? string.Empty : $" ({chord})")}";
}

/// <summary>Whether the pause hotkey is registered.</summary>
public enum PauseHotkeyState
{
    /// <summary>The server has not tried to register it yet (it registers it when it starts).</summary>
    NotStarted,

    /// <summary>Registered: pressing the chord pauses or resumes input.</summary>
    Registered,

    /// <summary>Windows refused to register it, usually because another program already has the chord.</summary>
    Failed,

    /// <summary>Turned off by the user (WAM_PAUSE_HOTKEY=none).</summary>
    Disabled,

    /// <summary>
    /// Another Windows App Manager server on this desktop has registered the chord. Its presses pause and resume
    /// this server too, since every server shares the pause; this server registers the chord itself once that
    /// server stops.
    /// </summary>
    OtherServer
}

/// <summary>The pause hotkey and whether it works.</summary>
/// <param name="Chord">The chord, such as "Ctrl+Alt+Shift+Pause"; null when it is turned off.</param>
/// <param name="State">Whether it is registered.</param>
/// <param name="Source">Where the chord comes from: "default" or "WAM_PAUSE_HOTKEY".</param>
/// <param name="Error">Why it could not be registered, or why the configured chord was not used.</param>
public sealed record PauseHotkeyStatus(string? Chord, PauseHotkeyState State, string Source, string? Error = null);

/// <summary>Held input as get_control_status reports it.</summary>
/// <param name="ServerHeld">Keys and buttons the server sent down and could not release yet; normally empty.</param>
/// <param name="ModifiersDown">Modifier keys Windows reports down now: the user's, or stuck.</param>
/// <param name="LastRelease">The last release of held input that sent something; null if none.</param>
public sealed record HeldInputStatus(
    IReadOnlyList<string> ServerHeld,
    IReadOnlyList<string> ModifiersDown,
    InputReleaseRecord? LastRelease);

/// <summary>Result of get_control_status.</summary>
/// <param name="Paused">
/// True while input is paused (by the user's hotkey, or by the server itself, as <paramref name="PausedBy"/> says): every
/// input tool returns PausedByUser.
/// </param>
/// <param name="PausedBy">How it was paused ("hotkey", or "shared_pause_unreadable" when the shared pause could not be read); null while running.</param>
/// <param name="PausedAt">When it was paused; null while running.</param>
/// <param name="ResumedBy">How it was last resumed; null if never.</param>
/// <param name="ResumedAt">When it was last resumed; null if never.</param>
/// <param name="Hotkey">The pause hotkey and whether it is registered.</param>
/// <param name="SharedPause">
/// True when the pause is shared by every Windows App Manager server on this desktop: the hotkey pauses and resumes
/// all of them, and so does resume_input.
/// </param>
/// <param name="HeldInput">The keys and buttons the server could not release, and the modifier keys that are down.</param>
/// <param name="Message">What this means for the agent.</param>
/// <param name="Takeover">
/// Takeover detection (roadmap M7.4): off, warn or stop, and whether its hooks work. Null when the server has none.
/// </param>
public sealed record ControlStatusResult(
    bool Paused,
    string? PausedBy,
    DateTimeOffset? PausedAt,
    string? ResumedBy,
    DateTimeOffset? ResumedAt,
    PauseHotkeyStatus Hotkey,
    bool SharedPause,
    HeldInputStatus HeldInput,
    string Message,
    TakeoverStatus? Takeover = null);

/// <summary>Result of resume_input.</summary>
/// <param name="Success">True when input is running after the call.</param>
/// <param name="WasPaused">True when input was paused when the call started.</param>
/// <param name="ResumedBy">
/// How it was resumed ("resume_input", "hotkey" or "another_server"); null when it was not paused or stays paused.
/// </param>
/// <param name="Error">Why input stays paused.</param>
/// <param name="ErrorCode">ResumeNeedsUser or ResumeDeclined; None on success.</param>
public sealed record ResumeInputResult(
    bool Success,
    bool WasPaused,
    string? ResumedBy = null,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None);

/// <summary>Result of release_all_input.</summary>
/// <param name="Success">True when nothing the call was asked to release is still down.</param>
/// <param name="Released">The keys and buttons released (their up event was sent).</param>
/// <param name="StillHeld">The keys and buttons that could not be released.</param>
/// <param name="ModifiersDown">Modifier keys Windows still reports down after the call (not released unless releaseHeldModifiers).</param>
/// <param name="Error">What went wrong.</param>
/// <param name="ErrorCode">
/// SendInputFailed; PausedByUser when the user's modifiers were not released because input is paused; ModifiersHeld when
/// none was released because one of the keys down is a modifier of the pause hotkey's chord.
/// </param>
public sealed record ReleaseInputResult(
    bool Success,
    IReadOnlyList<string> Released,
    IReadOnlyList<string> StillHeld,
    IReadOnlyList<string> ModifiersDown,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None);
