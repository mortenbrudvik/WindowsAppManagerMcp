using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The keys, characters and mouse buttons the server has sent down and not up yet, worked out from the
/// events SendInput inserted (roadmap M1.10). SendInput inserts a prefix of the batch: it returns how many
/// events went in, and the rest did not, so the held set is exact. Not thread-safe; <see cref="InputSession"/>
/// uses it under its lock.
/// </summary>
public sealed class HeldInputTracker
{
    /// <summary>
    /// VK 0xE8, which Windows leaves unassigned. Pressed and released right before Alt or Windows is released,
    /// it stops that release from opening the menu bar or the Start menu (Windows opens them when the key
    /// goes up with no other key pressed since it went down).
    /// </summary>
    public const ushort MaskKey = 0xE8;

    /// <summary>VK_LBUTTON, VK_RBUTTON and VK_MBUTTON: the key-state codes of the mouse buttons.</summary>
    private const ushort VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04;

    private static readonly (uint Down, uint Up)[] Buttons =
    [
        (NativeEnums.MOUSEEVENTF_LEFTDOWN, NativeEnums.MOUSEEVENTF_LEFTUP),
        (NativeEnums.MOUSEEVENTF_RIGHTDOWN, NativeEnums.MOUSEEVENTF_RIGHTUP),
        (NativeEnums.MOUSEEVENTF_MIDDLEDOWN, NativeEnums.MOUSEEVENTF_MIDDLEUP)
    ];

    private readonly List<HeldInput> _held = new();

    /// <summary>What is held, in the order it went down.</summary>
    public IReadOnlyList<HeldInput> Held => _held.ToList();

    /// <summary>True when nothing is held.</summary>
    public bool IsEmpty => _held.Count == 0;

    /// <summary>True when <paramref name="input"/> is held (the extended-key flag is ignored).</summary>
    public bool Contains(HeldInput input) => _held.Exists(h => SameKey(h, input));

    /// <summary>Applies the first <paramref name="count"/> events of a batch, the ones SendInput inserted.</summary>
    public void Apply(IReadOnlyList<InputEvent> events, int count)
    {
        ArgumentNullException.ThrowIfNull(events);
        for (var i = 0; i < Math.Min(count, events.Count); i++)
        {
            foreach (var (input, down) in Transitions(events[i]))
            {
                if (down)
                {
                    if (!Contains(input))
                    {
                        _held.Add(input);
                    }
                }
                else
                {
                    _held.RemoveAll(h => SameKey(h, input));
                }
            }
        }
    }

    /// <summary>
    /// Counts <paramref name="inputs"/> as held without an event that pressed them: what a batch pressed when
    /// it is unknown how much of it SendInput inserted.
    /// </summary>
    public void MarkHeld(IEnumerable<HeldInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var input in inputs.Where(input => !Contains(input)))
        {
            _held.Add(input);
        }
    }

    /// <summary>Every key, character and button <paramref name="events"/> press, whether or not they release it again.</summary>
    public static IReadOnlyList<HeldInput> Pressed(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var pressed = new List<HeldInput>();
        foreach (var e in events)
        {
            foreach (var (input, down) in Transitions(e))
            {
                if (down && !pressed.Exists(p => SameKey(p, input)))
                {
                    pressed.Add(input);
                }
            }
        }

        return pressed;
    }

    /// <summary>
    /// The virtual-key code Windows reports the state of <paramref name="input"/> under (GetAsyncKeyState):
    /// the key itself, or VK_LBUTTON, VK_RBUTTON or VK_MBUTTON; null for a character sent as Unicode, which
    /// has no state of its own.
    /// </summary>
    public static ushort? StateKey(HeldInput input) => input.Kind switch
    {
        InputEventKind.VirtualKey => input.Code,
        InputEventKind.Mouse when input.Code == NativeEnums.MOUSEEVENTF_LEFTDOWN => VkLButton,
        InputEventKind.Mouse when input.Code == NativeEnums.MOUSEEVENTF_RIGHTDOWN => VkRButton,
        InputEventKind.Mouse when input.Code == NativeEnums.MOUSEEVENTF_MIDDLEDOWN => VkMButton,
        _ => null
    };

    /// <summary>
    /// The events that release <paramref name="held"/>: the masking key (<see cref="MaskKey"/> down and up)
    /// first when Alt or Windows is among them, then the key-ups of ordinary keys and characters, then the
    /// modifier key-ups (last pressed first), then the mouse button-ups.
    /// </summary>
    public static List<InputEvent> ReleaseEvents(IReadOnlyCollection<HeldInput> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        var events = new List<InputEvent>();
        if (held.Any(h => h.OpensAMenuWhenReleasedAlone))
        {
            events.Add(InputEvent.Key(MaskKey, NativeEnums.KEYEVENTF_KEYDOWN));
            events.Add(InputEvent.Key(MaskKey, NativeEnums.KEYEVENTF_KEYUP));
        }

        foreach (var input in held.Where(h => h.Kind != InputEventKind.Mouse && !h.IsModifier))
        {
            events.Add(KeyUp(input));
        }

        foreach (var input in held.Where(h => h.IsModifier).Reverse())
        {
            events.Add(KeyUp(input));
        }

        foreach (var input in held.Where(h => h.Kind == InputEventKind.Mouse))
        {
            events.Add(InputEvent.Mouse((uint)input.Code << 1));
        }

        return events;
    }

    private static InputEvent KeyUp(HeldInput input) => input.Kind == InputEventKind.Unicode
        ? InputEvent.Unicode((char)input.Code, NativeEnums.KEYEVENTF_KEYUP)
        : InputEvent.Key(input.Code, input.Flags | NativeEnums.KEYEVENTF_KEYUP);

    /// <summary>What one event presses or releases. A mouse event can carry several button flags.</summary>
    private static IEnumerable<(HeldInput Input, bool Down)> Transitions(InputEvent e)
    {
        switch (e.Kind)
        {
            case InputEventKind.VirtualKey:
            case InputEventKind.Unicode:
                yield return (new HeldInput(e.Kind, e.Code, e.Kind == InputEventKind.VirtualKey ? e.Flags & NativeEnums.KEYEVENTF_EXTENDEDKEY : 0),
                    (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0);
                break;

            case InputEventKind.Mouse:
                foreach (var (down, up) in Buttons)
                {
                    if ((e.Flags & down) != 0)
                    {
                        yield return (HeldInput.Button(down), true);
                    }

                    if ((e.Flags & up) != 0)
                    {
                        yield return (HeldInput.Button(down), false);
                    }
                }

                break;
        }
    }

    private static bool SameKey(HeldInput a, HeldInput b) => a.Kind == b.Kind && a.Code == b.Code;
}
