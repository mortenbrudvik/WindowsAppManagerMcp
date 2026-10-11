using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// MCP tools for simulating mouse and keyboard input.
/// </summary>
[McpServerToolType]
public class InputSimulationTools
{
    private const string XDescription =
        "X coordinate in physical virtual-desktop pixels (negative on monitors left of the primary monitor)";

    private const string YDescription =
        "Y coordinate in physical virtual-desktop pixels (negative on monitors above the primary monitor)";

    internal const string MouseWindowHandleDescription =
        "The window the input is for (handle from find_windows). Strongly recommended: the input is sent only if the point is on " +
        "this window or a popup or dialog of it (otherwise errorCode TargetOccluded) and the window is not disabled by a modal " +
        "dialog (BlockedByModal). A child window's handle stands for its top-level window. Default: none";

    internal const string KeyboardWindowHandleDescription =
        "The window the input is for (handle from find_windows). Strongly recommended: the server brings it to the foreground " +
        "first and sends the keys only while it, or a dialog it owns, is the foreground window (otherwise errorCode " +
        "TargetNotForeground, or BlockedByModal when a modal dialog disables it). A child window's handle stands for its " +
        "top-level window; click the field first to put the caret in it. Default: none";

    internal const string AllowHostTargetDescription =
        "Allow input to the agent's own host window: the terminal, editor or chat app this server was started from. Works only " +
        "together with windowHandle naming that window; set it only when the user explicitly asked for input there. Default: false";

    internal const string ReleaseHeldModifiersDescription =
        "Release modifier keys (Shift, Ctrl, Alt, Windows) that are down before the input is sent, instead of refusing with " +
        "ModifiersHeld. Set it only when nobody is pressing them (a stuck key) or the user asked. Nothing is released while a " +
        "modifier of the user's pause hotkey is down. Default: false";

    private const string ClickTargetNote =
        " Pass windowHandle with the window you mean. Without it the click goes to whatever window is at that point (check it with get_foreground_window and get_element_at_point first), " +
        "and is refused when that is the agent's own terminal or chat window (TargetIsAgentHost), or in the cases below." +
        " Take coordinates from get_ui_elements bounds or capture_with_elements screenBounds, not from positions measured on a screenshot image." +
        " The button is pressed exactly at the checked point; if the pointer does not stay there (it is confined to a window, or the user is moving the mouse), nothing is clicked (CursorNotAtPoint)." +
        " Refused while a mouse button is held down or another window has captured the mouse (MouseInUse): the press would go there. " +
        "MouseInUse also when the capture cannot be read (a window just closed): retry." +
        GuardNote;

    private const string KeyboardTargetNote =
        " Pass windowHandle with the window you mean. Without it the keys go to the current foreground window, whichever it is, so call get_foreground_window immediately before and check its handle;" +
        " the keys are refused when that window is the agent's own terminal or chat window (TargetIsAgentHost)." +
        GuardNote;

    private const string GuardNote =
        " Refused, with nothing sent, for windows of apps running as administrator (TargetElevated), apps the user's policy denies (TargetDeniedByPolicy), " +
        "a locked session (SessionLocked) or a UAC prompt (DesktopUnavailable). The result reports receivingWindow (where the input went) and foregroundAfter." +
        SessionNote;

    private const string SessionNote =
        " While the user has paused input with the pause hotkey, nothing is sent (PausedByUser): stop and ask the user, and never try to resume." +
        " Refused while a modifier key is held down (ModifiersHeld) unless releaseHeldModifiers is true, and after waiting 30 s for another call (InputBusy).";

    private readonly IInputSimulationService _inputService;

    public InputSimulationTools(IInputSimulationService inputService)
    {
        _inputService = inputService;
    }

    [McpServerTool(Name = "click", Title = "Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a left mouse click at the specified screen coordinates. To click an element, take its bounds from get_ui_elements and click the center point." + ClickTargetNote +
                 " Mouse results also report actualX and actualY, the cursor position afterwards.")]
    public ClickResult Click(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.Click(x, y, Target(windowHandle, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    [McpServerTool(Name = "right_click", Title = "Right-Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a right mouse click at the specified screen coordinates. To click an element, take its bounds from get_ui_elements and click the center point." + ClickTargetNote)]
    public ClickResult RightClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.RightClick(x, y, Target(windowHandle, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    [McpServerTool(Name = "double_click", Title = "Double-Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a double left click at the specified screen coordinates. Useful for opening files, selecting words, or other double-click actions." + ClickTargetNote)]
    public ClickResult DoubleClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description("Delay between clicks in milliseconds (default: 50, range: 10-500)")]
        int delayMs = 50,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.DoubleClick(x, y, Math.Clamp(delayMs, 10, 500), Target(windowHandle, allowHostTarget), releaseHeldModifiers,
            cancellationToken);
    }

    [McpServerTool(Name = "mouse_move", Title = "Move Mouse Pointer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move the mouse cursor to the specified screen coordinates without clicking. Useful for hovering over elements to trigger tooltips or hover states." +
                 " Pass windowHandle with the window you mean; the same checks as for click apply (TargetOccluded, BlockedByModal, TargetIsAgentHost, CursorNotAtPoint, MouseInUse)." + GuardNote)]
    public MouseMoveResult MouseMove(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.MoveMouse(x, y, Target(windowHandle, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    [McpServerTool(Name = "type_text", Title = "Type Text", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Type text by simulating Unicode keyboard input. Works with any language/characters." + KeyboardTargetNote +
                 " The foreground window is checked again every 32 characters: if another window comes to the front, typing stops with " +
                 "TargetLostFocus and charactersTyped says how much was typed. Refused while a menu is open (TargetInMenuMode)." +
                 " Cancelling the request, or the user's pause, stops typing between batches.")]
    public Task<TypeTextResult> TypeText(
        [Description("The text to type")]
        string text,
        [Description("Delay between keystrokes in milliseconds (0-100, default 0). Use for visible typing effect.")]
        int delayMs = 0,
        [Description(KeyboardWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        delayMs = Math.Clamp(delayMs, 0, 100);
        return _inputService.TypeTextAsync(text, delayMs, Target(windowHandle, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    [McpServerTool(Name = "send_keys", Title = "Send Keys", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Send keys and key combinations. Use {KEY} for special keys (ENTER, TAB, F1-F12, DELETE, etc.), ^ for Ctrl, % for Alt, + for Shift. Examples: '^c' (Ctrl+C), '{TAB}' (Tab key), '+{HOME}' (Shift+Home)." + KeyboardTargetNote +
                 " Chords that close, quit or lock (closing a window or tab, quitting an app, Task Manager) are refused without windowHandle (DestructiveChordNeedsTarget)." +
                 " A chord that hands the keyboard to another window (Alt+Tab, Alt+Esc, Ctrl+Esc, the Windows key, Alt+Space, or a closing chord) must be the last key: keys after it are refused (KeysAfterFocusChange), so send them in a new call." +
                 " Never send keys that close, quit, submit or delete to a window you have not targeted. To close a window, use close_window with its handle instead of a key combination." +
                 " The user's pause hotkey chord is refused (PauseChordNotAllowed).")]
    public Task<SendKeysResult> SendKeys(
        [Description("Keys to send in SendKeys format. Special keys: {ENTER}, {TAB}, {ESC}, {BACKSPACE}, {DELETE}, {INSERT}, {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PGUP}, {PGDN}, {F1}-{F12}, {SPACE}. Modifiers: ^ (Ctrl), % (Alt), + (Shift).")]
        string keys,
        [Description(KeyboardWindowHandleDescription)]
        long? windowHandle = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.SendKeysAsync(keys, Target(windowHandle, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    private static InputTarget Target(long? windowHandle, bool allowHostTarget) =>
        new(windowHandle.HasValue ? (nint)windowHandle.Value : null, allowHostTarget);
}
