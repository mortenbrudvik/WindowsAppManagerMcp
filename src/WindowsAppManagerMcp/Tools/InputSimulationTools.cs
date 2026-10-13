using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// MCP tools for simulating mouse and keyboard input.
/// </summary>
[McpServerToolType]
public class InputSimulationTools
{
    private const string XDescription =
        "X coordinate: physical virtual-desktop pixels (negative left of the primary monitor), or as space says";

    private const string YDescription =
        "Y coordinate: physical virtual-desktop pixels (negative above the primary monitor), or as space says";

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
        "together with windowHandle (or window.handle) naming that window; set it only when the user explicitly asked for input there. Default: false";

    internal const string ReleaseHeldModifiersDescription =
        "Release modifier keys (Shift, Ctrl, Alt, Windows) that are down before the input is sent, instead of refusing with " +
        "ModifiersHeld. Set it only when nobody is pressing them (a stuck key) or the user asked. Nothing is released while a " +
        "modifier of the user's pause hotkey is down. Default: false";

    internal const string SpaceDescription =
        "What x and y are measured in: 'screen' (physical virtual-desktop pixels), 'window' (from the top-left corner of the " +
        "windowHandle window's rectangle, find_windows' bounds), 'client' (from the top-left corner of its client area) or " +
        "'image' (pixels of the image a capture returned, with captureId). The server converts the point; the result's x and y " +
        "are the screen point and requested echoes what was given. Default: 'image' with captureId, else 'screen'";

    internal const string CaptureIdDescription =
        "The captureId of a take_screenshot, capture_region, capture_window or capture_with_elements result: x and y are then " +
        "pixels of that image as returned, converted through its imageToScreen. A window capture's window is the input's target " +
        "(another windowHandle is refused). Nothing is sent when the captured window moved or resized by more than 2 pixels, " +
        "closed or was minimized since, or the monitors changed (StaleCapture), or when the id is unknown or older than 10 " +
        "minutes (CaptureNotFound): capture again. Default: none";

    private const string ClickTargetNote =
        " Pass windowHandle with the window you mean. Without it the click goes to whatever window is at that point (check it with get_foreground_window and get_element_at_point first), " +
        "and is refused when that is the agent's own host (terminal, editor or chat app; TargetIsAgentHost), or in the cases below." +
        " To act on an element, element_action with its ref needs no point; to click one, take find_ui_elements' clickPoint, the centre of its get_ui_elements bounds, or its capture_with_elements legend centre with that capture's captureId (any point you see on a capture works so: the server converts it)." +
        " The button is pressed exactly at the checked point; if the pointer does not stay there (it is confined to a window, or the user is moving the mouse), nothing is clicked (CursorNotAtPoint)." +
        " Refused while a mouse button is held down or another window has captured the mouse (MouseInUse): the press would go there. " +
        "MouseInUse also when the capture cannot be read (a window just closed): retry." +
        GuardNote;

    private const string KeyboardTargetNote =
        " Pass windowHandle with the window you mean. Without it the keys go to the current foreground window, whichever it is, so call get_foreground_window immediately before and check its handle;" +
        " the keys are refused when that window is the agent's own host (terminal, editor or chat app; TargetIsAgentHost)." +
        GuardNote;

    private const string GuardNote =
        " Refused, with nothing sent, for windows of apps running as administrator (TargetElevated), apps the user's policy denies (TargetDeniedByPolicy), " +
        "a locked session (SessionLocked) or a UAC prompt (DesktopUnavailable). The result reports receivingWindow (where the input went) and foregroundAfter." +
        SessionNote;

    private const string SessionNote =
        " While the user has paused input with the pause hotkey, nothing is sent (PausedByUser): stop and ask the user, and never try to resume." +
        " Refused while a modifier key is held down (ModifiersHeld) unless releaseHeldModifiers is true, and after waiting 30 s for another call (InputBusy).";

    private readonly IInputSimulationService _inputService;
    private readonly IWindowService? _windowService;
    private readonly ICoordinateSpaceResolver? _coordinates;

    /// <param name="inputService">The guarded input.</param>
    /// <param name="windowService">Resolves the window selector (roadmap M2.4); the server always passes it.</param>
    /// <param name="coordinates">
    /// Converts window, client and image points to screen points (roadmap M3.11); the server always passes it. Without it
    /// only screen points can be sent.
    /// </param>
    public InputSimulationTools(IInputSimulationService inputService, IWindowService? windowService = null,
        ICoordinateSpaceResolver? coordinates = null)
    {
        _inputService = inputService;
        _windowService = windowService;
        _coordinates = coordinates;
    }

    [McpServerTool(Name = "click", Title = "Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a left mouse click at the specified screen coordinates, or at a point of a capture's image (space)." + ClickTargetNote +
                 " Mouse results also report actualX and actualY, the cursor position afterwards.")]
    public ClickResult Click(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        [Description(SpaceDescription)]
        CoordinateSpace? space = null,
        [Description(CaptureIdDescription)]
        string? captureId = null,
        CancellationToken cancellationToken = default)
    {
        var point = Point(x, y, space, captureId, windowHandle, window, allowHostTarget);
        if (point.Failure is { } failure)
        {
            return new ClickResult(false, x, y, "left", failure.Error, failure.ErrorCode) { Requested = point.Requested };
        }

        return Echo(_inputService.Click(point.X, point.Y, point.Target, releaseHeldModifiers, cancellationToken), point.Requested);
    }

    [McpServerTool(Name = "right_click", Title = "Right-Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a right mouse click at the specified screen coordinates, or at a point of a capture's image (space)." +
                 " In a terminal (console, Windows Terminal, PuTTY, and anywhere in VS Code) a right click pastes the clipboard, so there it follows send_keys' paste rule (PasteNotAllowed)." + ClickTargetNote)]
    public ClickResult RightClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        [Description(SpaceDescription)]
        CoordinateSpace? space = null,
        [Description(CaptureIdDescription)]
        string? captureId = null,
        CancellationToken cancellationToken = default)
    {
        var point = Point(x, y, space, captureId, windowHandle, window, allowHostTarget);
        if (point.Failure is { } failure)
        {
            return new ClickResult(false, x, y, "right", failure.Error, failure.ErrorCode) { Requested = point.Requested };
        }

        return Echo(_inputService.RightClick(point.X, point.Y, point.Target, releaseHeldModifiers, cancellationToken), point.Requested);
    }

    [McpServerTool(Name = "double_click", Title = "Double-Click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Perform a double left click at the specified screen coordinates, or at a point of a capture's image (space). Useful for opening files, selecting words, or other double-click actions." + ClickTargetNote)]
    public ClickResult DoubleClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description("Delay between clicks in milliseconds (default: 50, range: 10-500)")]
        int delayMs = 50,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        [Description(SpaceDescription)]
        CoordinateSpace? space = null,
        [Description(CaptureIdDescription)]
        string? captureId = null,
        CancellationToken cancellationToken = default)
    {
        var point = Point(x, y, space, captureId, windowHandle, window, allowHostTarget);
        if (point.Failure is { } failure)
        {
            return new ClickResult(false, x, y, "double", failure.Error, failure.ErrorCode) { Requested = point.Requested };
        }

        return Echo(_inputService.DoubleClick(point.X, point.Y, Math.Clamp(delayMs, 10, 500), point.Target, releaseHeldModifiers,
            cancellationToken), point.Requested);
    }

    [McpServerTool(Name = "mouse_move", Title = "Move Mouse Pointer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move the mouse cursor to the specified screen coordinates, or to a point of a capture's image with its captureId, without clicking. Useful for hovering over elements to trigger tooltips or hover states." +
                 " Pass windowHandle with the window you mean; the same checks as for click apply (TargetOccluded, BlockedByModal, TargetIsAgentHost, CursorNotAtPoint, MouseInUse)." + GuardNote)]
    public MouseMoveResult MouseMove(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        [Description(SpaceDescription)]
        CoordinateSpace? space = null,
        [Description(CaptureIdDescription)]
        string? captureId = null,
        CancellationToken cancellationToken = default)
    {
        var point = Point(x, y, space, captureId, windowHandle, window, allowHostTarget);
        if (point.Failure is { } failure)
        {
            return new MouseMoveResult(false, x, y, failure.Error, failure.ErrorCode) { Requested = point.Requested };
        }

        return Echo(_inputService.MoveMouse(point.X, point.Y, point.Target, releaseHeldModifiers, cancellationToken), point.Requested);
    }

    [McpServerTool(Name = "scroll", Title = "Scroll", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Turn the mouse wheel over a window to scroll it: deltaY notches down (negative: up) and deltaX notches right " +
                 "(negative: left); one notch usually scrolls 3 lines. Pass windowHandle. Without x and y the wheel turns at the " +
                 "centre of that window's client area; to scroll one list or pane, pass a point inside it (the centre of its bounds " +
                 "from get_ui_elements). The pointer moves to the point and the notches are sent with it in one batch. Windows " +
                 "sends the wheel to the window under the pointer; when the user turned 'Scroll inactive windows' off it goes to " +
                 "the focused window, so scroll is then refused unless the window at the point is in front (WheelFollowsFocus): " +
                 "call focus_window first; even then the wheel reaches the control with the keyboard focus, not the one at the " +
                 "point, so click into the content first. The wheel over a combo box, slider or number box changes its value, so point at the " +
                 "content, not at such a control. To see whether the content moved, read it again (get_ui_elements, a capture)." +
                 " The same checks as for click apply (TargetOccluded, BlockedByModal, TargetIsAgentHost, CursorNotAtPoint, MouseInUse)." +
                 GuardNote)]
    public ScrollResult Scroll(
        [Description(MouseWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(XDescription + ", together with y. Default: the centre of windowHandle's client area")]
        int? x = null,
        [Description(YDescription + ", together with x. Default: the centre of windowHandle's client area")]
        int? y = null,
        [Description("Vertical wheel notches: positive scrolls down, negative scrolls up, at most 100 either way. Default: 0")]
        int deltaY = 0,
        [Description("Horizontal wheel notches: positive scrolls right, negative scrolls left, at most 100 either way. Default: 0")]
        int deltaX = 0,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        [Description(SpaceDescription)]
        CoordinateSpace? space = null,
        [Description(CaptureIdDescription)]
        string? captureId = null,
        CancellationToken cancellationToken = default)
    {
        if (x.HasValue != y.HasValue)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "Pass both x and y, or neither (then the wheel turns at the centre of windowHandle's client area). Nothing was scrolled.");
        }

        const int Max = InputSimulationService.MaxScrollNotches;
        if ((deltaY == 0 && deltaX == 0) || deltaY is < -Max or > Max || deltaX is < -Max or > Max)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"deltaY and deltaX must be between -{InputSimulationService.MaxScrollNotches} and {InputSimulationService.MaxScrollNotches}, " +
                $"and not both 0 (got deltaY {deltaY}, deltaX {deltaX}). Nothing was scrolled.");
        }

        if (x is { } pointX && y is { } pointY)
        {
            var point = Point(pointX, pointY, space, captureId, windowHandle, window, allowHostTarget);
            if (point.Failure is { } failure)
            {
                return new ScrollResult(false, pointX, pointY, deltaY, deltaX, failure.Error, failure.ErrorCode) { Requested = point.Requested };
            }

            return Echo(_inputService.Scroll(point.X, point.Y, deltaY, deltaX, point.Target, releaseHeldModifiers, cancellationToken),
                point.Requested);
        }

        if ((space != null && space != CoordinateSpace.Screen) || captureId != null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "space and captureId convert a point: pass x and y with them, or leave them out to scroll at the centre of " +
                "windowHandle's client area. Nothing was scrolled.");
        }

        // Without a point the wheel turns at the centre of a named window; a bare {"foreground": true} names none
        var target = Target(windowHandle, window, allowHostTarget);
        if (!target.HasWindow)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "Pass windowHandle or a window selector that names a window (the wheel turns at the centre of its client area), or " +
                "x and y. {\"foreground\": true} alone names no window here. Nothing was scrolled.");
        }

        return _inputService.Scroll(null, null, deltaY, deltaX, target, releaseHeldModifiers, cancellationToken);
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
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        delayMs = Math.Clamp(delayMs, 0, 100);
        return _inputService.TypeTextAsync(text, delayMs, Target(windowHandle, window, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    [McpServerTool(Name = "send_keys", Title = "Send Keys", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Send keys and key combinations. Use {KEY} for special keys (ENTER, TAB, F1-F12, DELETE, etc.), ^ for Ctrl, % for Alt, + for Shift. Examples: '^c' (Ctrl+C), '{TAB}' (Tab key), '+{HOME}' (Shift+Home)." + KeyboardTargetNote +
                 " Chords that close, quit or lock (closing a window or tab, quitting an app, Task Manager) are refused without windowHandle (DestructiveChordNeedsTarget)." +
                 " A chord that hands the keyboard to another window (Alt+Tab, Alt+Esc, Ctrl+Esc, the Windows key, Alt+Space, or a closing chord) must be the last key: keys after it are refused (KeysAfterFocusChange), so send them in a new call." +
                 " Never send keys that close, quit, submit or delete to a window you have not targeted. To close a window, use close_window with its handle instead of a key combination." +
                 " The user's pause hotkey chord is refused (PauseChordNotAllowed)." +
                 " A paste (^v, +{INSERT}) is sent only when the clipboard holds what set_clipboard put there, your own copy, or content the user let get_clipboard read; otherwise, and for content marked private, it is refused (PasteNotAllowed)." +
                 " Keys that copy and then paste (^c^v) are refused too (PasteNotAllowed): send the copy and the paste in separate calls.")]
    public Task<SendKeysResult> SendKeys(
        [Description("Keys to send in SendKeys format. Special keys: {ENTER}, {TAB}, {ESC}, {BACKSPACE}, {DELETE}, {INSERT}, {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PGUP}, {PGDN}, {F1}-{F12}, {SPACE}. Modifiers: ^ (Ctrl), % (Alt), + (Shift).")]
        string keys,
        [Description(KeyboardWindowHandleDescription)]
        long? windowHandle = null,
        [Description(WindowTargeting.InputWindowDescription)]
        WindowSelector? window = null,
        [Description(AllowHostTargetDescription)]
        bool allowHostTarget = false,
        [Description(ReleaseHeldModifiersDescription)]
        bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        return _inputService.SendKeysAsync(keys, Target(windowHandle, window, allowHostTarget), releaseHeldModifiers, cancellationToken);
    }

    /// <summary>The result with the point as the call gave it, when that was not a screen point.</summary>
    private static ClickResult Echo(ClickResult result, RequestedPoint? requested) =>
        requested == null ? result : result with { Requested = requested };

    /// <inheritdoc cref="Echo(ClickResult, RequestedPoint?)"/>
    private static MouseMoveResult Echo(MouseMoveResult result, RequestedPoint? requested) =>
        requested == null ? result : result with { Requested = requested };

    /// <inheritdoc cref="Echo(ClickResult, RequestedPoint?)"/>
    private static ScrollResult Echo(ScrollResult result, RequestedPoint? requested) =>
        requested == null ? result : result with { Requested = requested };

    /// <summary>A mouse point converted to the screen with its target, or why it could not be converted.</summary>
    /// <param name="X">The screen point; the given point when it could not be converted.</param>
    /// <param name="Y">See <paramref name="X"/>.</param>
    /// <param name="Target">The input's target, with the point's recheck.</param>
    /// <param name="Requested">The point as given, when it was not a screen point.</param>
    /// <param name="Failure">Why it could not be converted (nothing is sent), or null.</param>
    private sealed record MousePoint(int X, int Y, InputTarget Target, RequestedPoint? Requested, PointResolution? Failure);

    /// <summary>
    /// The screen point and target of a mouse call (roadmap M3.11). A screen point is sent as given. A window or client
    /// point is converted from the target window's rectangle or client area. An image point is converted through its
    /// capture's imageToScreen, and a window capture's window becomes the target: the call's own target must be that
    /// window, and a target the capture supplied never lifts the host guard (allowHostTarget needs the window named by
    /// its handle, as always). The converted point carries a recheck the input guard runs inside the input gate, so it is
    /// checked exactly as a screen point is, and refused (StaleCapture) when what it was measured on changed meanwhile.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// InvalidArguments: image without captureId, a captureId with another space, window or client without a window, or an
    /// image point of one window with another window as target. Nothing is sent.
    /// </exception>
    private MousePoint Point(int x, int y, CoordinateSpace? space, string? captureId, long? windowHandle, WindowSelector? window,
        bool allowHostTarget)
    {
        var effective = space ?? (captureId != null ? CoordinateSpace.Image : CoordinateSpace.Screen);
        if (captureId != null && effective != CoordinateSpace.Image)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"captureId names an image, so x and y must be image pixels: pass space 'image' (or leave space out), not " +
                $"'{ToolEnum<CoordinateSpace>.Name(effective)}'. Nothing was sent.");
        }

        if (effective == CoordinateSpace.Image && string.IsNullOrWhiteSpace(captureId))
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "space 'image' needs captureId: the captureId of the capture whose image x and y were measured on. Nothing was sent.");
        }

        var target = Target(windowHandle, window, allowHostTarget);
        if (effective == CoordinateSpace.Screen)
        {
            return new MousePoint(x, y, target, null, null);
        }

        var spaceName = ToolEnum<CoordinateSpace>.Name(effective);
        var requested = new RequestedPoint(x, y, spaceName, effective == CoordinateSpace.Image ? captureId : null);
        var coordinates = _coordinates
            ?? throw new InvalidOperationException("Window, client and image points need the coordinate resolver; the server always registers it.");

        PointResolution point;
        if (effective == CoordinateSpace.Image)
        {
            point = coordinates.FromImage(captureId!, x, y);
            if (point is { Resolved: true, CapturedWindow: { } captured })
            {
                if (target.HasWindow && target.WindowHandle != captured)
                {
                    throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                        $"Capture {captureId} is of window {(long)captured}, but the call names window {(long)target.WindowHandle!.Value}: " +
                        "a point of one window's image is never sent to another. Leave windowHandle and window out (the captured " +
                        "window is the target), or capture the window you mean. Nothing was sent.");
                }

                if (!target.HasWindow)
                {
                    target = InputTarget.Window(captured);
                }
            }
        }
        else
        {
            if (target.WindowHandle is not { } handle)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    $"space '{spaceName}' is relative to a window: pass windowHandle, or a window selector that names one " +
                    "({\"foreground\": true} alone names none). Nothing was sent.");
            }

            point = coordinates.FromWindow(effective, handle, x, y);
        }

        return point.Resolved
            ? new MousePoint(point.X, point.Y, target with { PointRecheck = point.Recheck }, requested, null)
            : new MousePoint(x, y, target, requested, point);
    }

    /// <summary>
    /// The input's target: windowHandle, or the window selector resolved to one window (roadmap M2.4), before the input
    /// service takes the input gate. The resolved window is passed exactly as a windowHandle would be, so the input guard,
    /// the host guard and the target policy check it as they check any windowHandle.
    /// </summary>
    /// <remarks>
    /// The guard's rules for a window the caller chose do not take a selector that chose nothing: a bare
    /// <c>{"foreground": true}</c> is the implicit target (whatever is in front) under another name, so it is passed as no
    /// windowHandle, and destructive chords and the host rule still apply to it as without windowHandle. allowHostTarget
    /// lifts the host guard only for a window named by its handle (windowHandle or window.handle): a selector that finds
    /// windows by name could resolve to the agent's own terminal without the caller ever seeing it. A selector's window is
    /// checked against it again inside the input gate (<see cref="InputTarget.SelectorRecheck"/>), as the window tools do.
    /// </remarks>
    /// <exception cref="ToolErrorException">
    /// Both were given, allowHostTarget came with a selector that names no handle, or the selector names no single window;
    /// nothing is sent.
    /// </exception>
    private InputTarget Target(long? windowHandle, WindowSelector? window, bool allowHostTarget)
    {
        if (windowHandle == null && WindowTargeting.IsBareForeground(window))
            return new InputTarget(null, allowHostTarget);

        if (windowHandle == null && allowHostTarget && window is { Handle: null })
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "allowHostTarget lifts the guard on the agent's own terminal or chat window only for a window named by its handle " +
                "(windowHandle, or window.handle), not for a selector that finds windows by name. Find the window with find_windows " +
                "and pass its handle, only when the user explicitly asked for input there. Nothing was sent.");
        }

        var handle = WindowTargeting.Optional(_windowService, windowHandle, window, "windowHandle");
        if (handle is not { } resolved || window == null || _windowService is not { } windows)
            return new InputTarget(handle, allowHostTarget);

        // The selector is checked again inside the gate, by the input guard, as the window tools check theirs
        return new InputTarget(handle, allowHostTarget)
        {
            SelectorRecheck = () => WindowTargeting.Recheck(windows, window, resolved) is { } refusal
                ? new SelectorRefusal(refusal.ErrorCode, refusal.Error)
                : null
        };
    }
}
