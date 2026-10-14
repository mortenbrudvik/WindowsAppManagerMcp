namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake native input layer: records every SendInput batch and cursor move instead of sending them, and
/// keeps a key state the way Windows would (inserted key-downs and button-downs are down until their up
/// event), so key-state reads and stuck-input recovery can be tested without touching the desktop. Tests can
/// make SendInput insert fewer events, throw, or block.
/// </summary>
public sealed class FakeInputWrapper : INativeInputWrapper
{
    private const uint KeyUp = 0x0002;
    private const uint AbsoluteMove = 0x0001 | 0x4000 | 0x8000; // MOUSEEVENTF_MOVE | VIRTUALDESK | ABSOLUTE
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const ushort VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4;

    private static readonly (uint Down, uint Up, ushort Vk)[] Buttons =
    [
        (0x0002, 0x0004, 0x01), // left
        (0x0008, 0x0010, 0x02), // right
        (0x0020, 0x0040, 0x04)  // middle
    ];

    /// <summary>XDOWN and XUP, and the X buttons (their mouseData) with their key-state codes.</summary>
    private const uint XDown = 0x0080, XUp = 0x0100;

    private static readonly (uint XButton, ushort Vk)[] XButtons = [(1, 0x05), (2, 0x06)];

    private readonly object _lock = new();
    private readonly List<IReadOnlyList<InputEvent>> _batches = new();
    private readonly HashSet<ushort> _down = new();
    private (int X, int Y) _cursor;

    /// <summary>
    /// How many events of a batch SendInput inserts, given the batch and its 0-based call number; null
    /// inserts them all. Throw from it to make SendInput throw.
    /// </summary>
    public Func<IReadOnlyList<InputEvent>, int, uint>? Insert { get; set; }

    /// <summary>Runs before each batch is inserted, on the sending thread (to block it, or to pause meanwhile).</summary>
    public Action<IReadOnlyList<InputEvent>>? BeforeSend { get; set; }

    /// <summary>Every batch passed to SendInput, inserted or not, in order.</summary>
    public IReadOnlyList<IReadOnlyList<InputEvent>> Batches
    {
        get
        {
            lock (_lock)
            {
                return _batches.ToList();
            }
        }
    }

    /// <summary>The events of every batch, in order.</summary>
    public IReadOnlyList<InputEvent> Events => Batches.SelectMany(b => b).ToList();

    /// <summary>The number of SetCursorPos calls.</summary>
    public int CursorMoves { get; private set; }

    /// <summary>Holds <paramref name="virtualKey"/> down, as a user's finger on the key would.</summary>
    public void Press(ushort virtualKey)
    {
        lock (_lock)
        {
            _down.Add(virtualKey);
        }
    }

    /// <summary>Lets go of <paramref name="virtualKey"/>.</summary>
    public void Lift(ushort virtualKey)
    {
        lock (_lock)
        {
            _down.Remove(virtualKey);
        }
    }

    /// <summary>Runs after each SetCursorPos, on the calling thread (to move the mouse away, or to pause meanwhile).</summary>
    public Action<int, int>? AfterSetCursorPos { get; set; }

    /// <summary>
    /// The virtual screen an absolute move (MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK) spans: the fake puts the pointer
    /// at the pixel Windows maps the move to, left + n * width / 65536 rounded down. Default: one 1920x1080 monitor.
    /// </summary>
    public VirtualScreenBounds VirtualScreen { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>
    /// The rectangle the pointer is confined to (ClipCursor, as a game or remote session sets it; right and bottom
    /// exclusive), or null. SetCursorPos and absolute moves put the pointer at the nearest point inside it, as Windows does.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom)? Clip { get; set; }

    /// <summary>Puts the pointer at (x, y), as the user moving the mouse would; not counted in <see cref="CursorMoves"/>.</summary>
    public void MovePointer(int x, int y)
    {
        lock (_lock)
        {
            _cursor = (x, y);
        }
    }

    public bool SetCursorPos(int x, int y)
    {
        lock (_lock)
        {
            _cursor = Clipped(x, y);
            CursorMoves++;
        }

        AfterSetCursorPos?.Invoke(x, y);
        return true;
    }

    public (int X, int Y)? GetCursorPos()
    {
        lock (_lock)
        {
            return _cursor;
        }
    }

    public uint SendInput(IReadOnlyList<InputEvent> events)
    {
        BeforeSend?.Invoke(events);

        int call;
        lock (_lock)
        {
            call = _batches.Count;
            _batches.Add(events.ToList());
        }

        var inserted = (int)Math.Min(Insert?.Invoke(events, call) ?? (uint)events.Count, (uint)events.Count);
        lock (_lock)
        {
            foreach (var e in events.Take(inserted))
            {
                Apply(e);
            }
        }

        return (uint)inserted;
    }

    public bool IsKeyDown(ushort virtualKey)
    {
        lock (_lock)
        {
            return _down.Contains(virtualKey);
        }
    }

    /// <summary>The toggle keys that are on (Caps Lock is 0x14); nothing sent through the fake toggles them.</summary>
    public HashSet<ushort> Toggled { get; } = new();

    public bool? IsKeyToggled(ushort virtualKey)
    {
        lock (_lock)
        {
            return Toggled.Contains(virtualKey);
        }
    }

    private (int X, int Y) Clipped(int x, int y) => Clip is { } clip
        ? (Math.Clamp(x, clip.Left, clip.Right - 1), Math.Clamp(y, clip.Top, clip.Bottom - 1))
        : (x, y);

    /// <summary>
    /// Updates the key state as Windows does (a generic modifier counts as its left key too), and the pointer's position for
    /// an absolute move.
    /// </summary>
    private void Apply(InputEvent e)
    {
        switch (e.Kind)
        {
            case InputEventKind.VirtualKey:
                var keys = e.Code switch
                {
                    VkControl or VkLControl => new[] { VkControl, VkLControl },
                    VkMenu or VkLMenu => new[] { VkMenu, VkLMenu },
                    VkShift or VkLShift => new[] { VkShift, VkLShift },
                    _ => new[] { e.Code }
                };
                foreach (var key in keys)
                {
                    if ((e.Flags & KeyUp) == 0)
                    {
                        _down.Add(key);
                    }
                    else
                    {
                        _down.Remove(key);
                    }
                }

                break;

            case InputEventKind.Mouse:
                if ((e.Flags & AbsoluteMove) == AbsoluteMove)
                {
                    _cursor = Clipped(VirtualScreen.X + (int)((long)e.Dx * VirtualScreen.Width / 65536),
                        VirtualScreen.Y + (int)((long)e.Dy * VirtualScreen.Height / 65536));
                }

                foreach (var (down, up, vk) in Buttons)
                {
                    if ((e.Flags & down) != 0)
                    {
                        _down.Add(vk);
                    }

                    if ((e.Flags & up) != 0)
                    {
                        _down.Remove(vk);
                    }
                }

                foreach (var (xButton, vk) in XButtons.Where(x => (e.MouseData & x.XButton) != 0))
                {
                    if ((e.Flags & XDown) != 0)
                    {
                        _down.Add(vk);
                    }

                    if ((e.Flags & XUp) != 0)
                    {
                        _down.Remove(vk);
                    }
                }

                break;
        }
    }
}
