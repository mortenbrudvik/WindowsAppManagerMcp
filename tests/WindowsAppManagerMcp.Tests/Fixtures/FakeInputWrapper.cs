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
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const ushort VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4;

    private static readonly (uint Down, uint Up, ushort Vk)[] Buttons =
    [
        (0x0002, 0x0004, 0x01), // left
        (0x0008, 0x0010, 0x02), // right
        (0x0020, 0x0040, 0x04)  // middle
    ];

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

    public bool SetCursorPos(int x, int y)
    {
        lock (_lock)
        {
            _cursor = (x, y);
            CursorMoves++;
        }

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

    /// <summary>Updates the key state as Windows does: a generic modifier counts as its left key too.</summary>
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

                break;
        }
    }
}
