using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The events of one mouse operation, sent in one SendInput call (roadmap M4.2). <see cref="Events"/> name physical buttons,
/// as SendInput does; <see cref="AsUnswapped"/> is the same batch with the primary button as the left one and the secondary
/// as the right one, for the rules about what a click does (a left-button press can copy, a right click in a terminal
/// pastes; see <see cref="ClipboardOrigin"/>), which are about the buttons as the user knows them.
/// </summary>
public sealed record MouseBatch(IReadOnlyList<InputEvent> Events, IReadOnlyList<InputEvent> AsUnswapped);

/// <summary>
/// Builds the one SendInput batch of a mouse operation (roadmap M4.2): an absolute move to the point across the virtual
/// desktop, then the button or wheel events. SendInput does not interleave other input with the events of one call, so the
/// press lands on the pixel the guard checked wherever the pointer was before, and a user moving the mouse cannot put it
/// elsewhere. click, right_click, double_click, mouse_click, drag, mouse_move, scroll and element_action's mouse path build
/// their batches here. mouse_click and drag (roadmap M4.3) also put modifier keys around the mouse events.
/// </summary>
/// <remarks>
/// Buttons are the user's (<see cref="MouseButton"/>): with swapped buttons a primary click is sent as the right button's
/// events, which Windows swaps back. The clicks of a double or triple click follow each other in the batch with no move in
/// between, so every press is at the same pixel (within SM_CXDOUBLECLK x SM_CYDOUBLECLK of the first) and, since SendInput
/// stamps every event of a call with the time it inserts them, well within the double-click time (GetDoubleClickTime): no
/// gap is needed, and a batch cannot hold one. The X buttons share MOUSEEVENTF_XDOWN and XUP, and name the button in the
/// event's mouseData (XBUTTON1, XBUTTON2).
/// </remarks>
public sealed class InputBatchBuilder
{
    /// <summary>The most clicks one batch sends at a point (a triple click).</summary>
    public const int MaxClicks = 3;

    private readonly VirtualScreenBounds _screen;
    private readonly bool _buttonsSwapped;
    private readonly KeyboardLayout _layout;
    private readonly List<InputEvent> _events = new();
    private readonly List<InputEvent> _asUnswapped = new();

    /// <param name="screen">The virtual screen the move's normalized coordinates span (see <see cref="ToAbsolute"/>).</param>
    /// <param name="buttonsSwapped">Whether the user swapped the mouse buttons (GetSystemMetrics SM_SWAPBUTTON).</param>
    /// <param name="layout">
    /// The keyboard layout the modifier keys take their scan codes from, as send_keys' keys do (roadmap M4.1); without it,
    /// <see cref="KeyboardLayout.Invariant"/>, which has none.
    /// </param>
    public InputBatchBuilder(VirtualScreenBounds screen, bool buttonsSwapped, KeyboardLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (screen.Width <= 0 || screen.Height <= 0)
        {
            throw new ArgumentException("The virtual screen has no size.", nameof(screen));
        }

        _screen = screen;
        _buttonsSwapped = buttonsSwapped;
        _layout = layout ?? KeyboardLayout.Invariant;
    }

    /// <summary>
    /// Moves the pointer to the pixel (<paramref name="x"/>, <paramref name="y"/>) of the virtual desktop
    /// (MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK).
    /// </summary>
    public InputBatchBuilder MoveTo(int x, int y)
    {
        var (dx, dy) = ToAbsolute(x, y, _screen);
        return Add(InputEvent.MouseMoveAbsolute(dx, dy));
    }

    /// <summary>Presses <paramref name="button"/> where the pointer is.</summary>
    public InputBatchBuilder Press(MouseButton button) =>
        Add(InputEvent.Mouse(DownFlag(button, _buttonsSwapped), XButtonOf(button)),
            InputEvent.Mouse(DownFlag(button, buttonsSwapped: false), XButtonOf(button)));

    /// <summary>Releases <paramref name="button"/> where the pointer is.</summary>
    public InputBatchBuilder Release(MouseButton button) =>
        Add(InputEvent.Mouse(UpFlag(button, _buttonsSwapped), XButtonOf(button)),
            InputEvent.Mouse(UpFlag(button, buttonsSwapped: false), XButtonOf(button)));

    /// <summary>
    /// Presses <paramref name="modifiers"/> (each once, in the order given) as their left-hand keys, so the mouse events after
    /// them are Ctrl-, Shift-, Alt- or Windows-clicks (roadmap M4.3).
    /// </summary>
    public InputBatchBuilder ModifiersDown(IEnumerable<MouseModifier> modifiers)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        foreach (var key in modifiers.Distinct().Select(KeyOf))
        {
            Add(ModifierKey(key, NativeEnums.KEYEVENTF_KEYDOWN));
        }

        return this;
    }

    /// <summary>
    /// Releases <paramref name="modifiers"/>, last pressed first, after the masking key when Alt or Windows is among them: either
    /// going up with no key pressed since it went down would open the menu bar or the Start menu (as the session's releases
    /// do, <see cref="HeldInputTracker.ReleaseEvents"/>).
    /// </summary>
    public InputBatchBuilder ModifiersUp(IEnumerable<MouseModifier> modifiers)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        var keys = modifiers.Distinct().Select(KeyOf).ToList();
        if (keys.Any(key => key is NativeEnums.VK_LMENU or NativeEnums.VK_LWIN))
        {
            Add(InputEvent.Key(HeldInputTracker.MaskKey, NativeEnums.KEYEVENTF_KEYDOWN));
            Add(InputEvent.Key(HeldInputTracker.MaskKey, NativeEnums.KEYEVENTF_KEYUP));
        }

        foreach (var key in Enumerable.Reverse(keys))
        {
            Add(ModifierKey(key, NativeEnums.KEYEVENTF_KEYUP));
        }

        return this;
    }

    /// <summary>
    /// <paramref name="count"/> clicks (1 to <see cref="MaxClicks"/>) of <paramref name="button"/> where the pointer is: a press
    /// and a release each, with nothing in between, so Windows counts them as one double or triple click.
    /// </summary>
    public InputBatchBuilder Click(MouseButton button, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxClicks);
        for (var i = 0; i < count; i++)
        {
            Press(button).Release(button);
        }

        return this;
    }

    /// <summary>
    /// One wheel event per notch: <paramref name="deltaY"/> notches down (negative: up), then <paramref name="deltaX"/> notches
    /// right (negative: left). One event per notch (WHEEL_DELTA each) rather than one large delta, because some applications
    /// scroll one step for any delta. deltaY counts down as positive (as a web page's wheel event does), which is a negative
    /// wheel movement.
    /// </summary>
    public InputBatchBuilder Wheel(int deltaY, int deltaX)
    {
        for (var i = 0; i < Math.Abs(deltaY); i++)
        {
            Add(InputEvent.Wheel(-Math.Sign(deltaY) * InputEvent.WheelDelta));
        }

        for (var i = 0; i < Math.Abs(deltaX); i++)
        {
            Add(InputEvent.HorizontalWheel(Math.Sign(deltaX) * InputEvent.WheelDelta));
        }

        return this;
    }

    /// <summary>The batch built so far.</summary>
    public MouseBatch Build() => new(_events.ToList(), _asUnswapped.ToList());

    /// <summary>
    /// The MOUSEEVENTF_*DOWN flag of the physical button that is <paramref name="button"/>: XDOWN for either X button, which
    /// <see cref="XButtonOf"/> tells apart.
    /// </summary>
    public static uint DownFlag(MouseButton button, bool buttonsSwapped) => button switch
    {
        MouseButton.Primary => buttonsSwapped ? NativeEnums.MOUSEEVENTF_RIGHTDOWN : NativeEnums.MOUSEEVENTF_LEFTDOWN,
        MouseButton.Secondary => buttonsSwapped ? NativeEnums.MOUSEEVENTF_LEFTDOWN : NativeEnums.MOUSEEVENTF_RIGHTDOWN,
        MouseButton.Middle => NativeEnums.MOUSEEVENTF_MIDDLEDOWN,
        MouseButton.X1 or MouseButton.X2 => NativeEnums.MOUSEEVENTF_XDOWN,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button")
    };

    /// <summary>The mouseData of <paramref name="button"/>'s events: XBUTTON1 or XBUTTON2 for an X button, otherwise 0.</summary>
    public static uint XButtonOf(MouseButton button) => button switch
    {
        MouseButton.X1 => NativeEnums.XBUTTON1,
        MouseButton.X2 => NativeEnums.XBUTTON2,
        _ => 0
    };

    /// <summary>The left-hand key of <paramref name="modifier"/>.</summary>
    public static ushort KeyOf(MouseModifier modifier) => modifier switch
    {
        MouseModifier.Ctrl => NativeEnums.VK_LCONTROL,
        MouseModifier.Shift => NativeEnums.VK_LSHIFT,
        MouseModifier.Alt => NativeEnums.VK_LMENU,
        MouseModifier.Win => NativeEnums.VK_LWIN,
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "Unknown modifier")
    };

    /// <summary>A modifier key's event, with the layout's scan code and extended-key flag (the left Windows key is E0 5B).</summary>
    private InputEvent ModifierKey(ushort key, uint flags)
    {
        var (scan, extended) = _layout.ScanOf(key);
        return InputEvent.Key(key, flags | (extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0), scan);
    }

    /// <summary>The MOUSEEVENTF_*UP flag of the physical button that is <paramref name="button"/>.</summary>
    public static uint UpFlag(MouseButton button, bool buttonsSwapped) => DownFlag(button, buttonsSwapped) << 1;

    /// <summary>
    /// The normalized absolute coordinates (0..65535 each) of the pixel (<paramref name="x"/>, <paramref name="y"/>) for a move
    /// with MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK across <paramref name="screen"/> (see <see cref="Normalize"/>).
    /// </summary>
    public static (int X, int Y) ToAbsolute(int x, int y, VirtualScreenBounds screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return (Normalize((long)x - screen.X, screen.Width), Normalize((long)y - screen.Y, screen.Height));
    }

    /// <summary>
    /// The normalized coordinate (0..65535) of the pixel <paramref name="offset"/> pixels from the virtual screen's left (or
    /// top) edge, across <paramref name="size"/> pixels. Windows does not document how it maps n to a pixel. The mapping seen
    /// in practice, and the one ReactOS implements, is floor(n * size / 65536), so the pixel's values of n run from
    /// ceil(offset * 65536 / size) to ceil((offset + 1) * 65536 / size) - 1. Of those, this returns the middle one of those that
    /// also hit the pixel when n * size / 65536 is rounded to the nearest pixel, and when n * (size - 1) / 65535 is (the
    /// literal reading of "65535 maps onto the lower-right corner"), the two other mappings code written for Windows assumes:
    /// every pixel, the last column and row too, then comes back exactly under all three, for any size up to 32768
    /// (InputBatchBuilderTests checks them all). A larger size can leave a pixel without such an n; then the middle of its
    /// floor range is returned. An offset off the screen gets the nearest edge; the pointer then ends up away from the
    /// point, which the caller reports (CursorClamped).
    /// </summary>
    public static int Normalize(long offset, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (offset < 0)
        {
            return 0;
        }

        if (offset >= size)
        {
            return 65535;
        }

        var first = (int)Math.Min((offset * 65536 + size - 1) / size, 65535);
        var last = (int)Math.Min(((offset + 1) * 65536 + size - 1) / size - 1, 65535);
        int? from = null, to = null;
        for (var n = first; n <= last; n++)
        {
            if (RoundedToNearest(n, size) == offset && RoundedToLastPixel(n, size) == offset)
            {
                from ??= n;
                to = n;
            }
        }

        return from is { } low && to is { } high ? (low + high) / 2 : (first + last) / 2;

        static long RoundedToNearest(int n, int size) => ((long)n * size + 32768) / 65536;

        static long RoundedToLastPixel(int n, int size) => (2L * n * (size - 1) + 65535) / (2L * 65535);
    }

    /// <summary>
    /// The bounding rectangle of <paramref name="monitors"/>: the virtual screen, when Windows' own reading of it
    /// (<see cref="Interfaces.INativeSystemInfoWrapper.GetVirtualScreen"/>) is not available.
    /// </summary>
    public static VirtualScreenBounds VirtualScreenOf(IReadOnlyCollection<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is needed.", nameof(monitors));
        }

        var left = monitors.Min(m => m.Bounds.X);
        var top = monitors.Min(m => m.Bounds.Y);
        return new VirtualScreenBounds(left, top, monitors.Max(m => m.Bounds.X + m.Bounds.Width) - left,
            monitors.Max(m => m.Bounds.Y + m.Bounds.Height) - top);
    }

    private InputBatchBuilder Add(InputEvent e) => Add(e, e);

    private InputBatchBuilder Add(InputEvent e, InputEvent asUnswapped)
    {
        _events.Add(e);
        _asUnswapped.Add(asUnswapped);
        return this;
    }
}
