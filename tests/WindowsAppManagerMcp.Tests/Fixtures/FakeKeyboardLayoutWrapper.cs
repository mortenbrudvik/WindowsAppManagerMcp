namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the keyboard layout queries (roadmap M4.1) that models four layouts the way Windows reports them: US
/// English, Norwegian (AltGr for @ { [ ] } $ €, dead keys for ¨ ^ ~ ` ´), German (QWERTZ, AltGr for @ on the Q key,
/// { [ ] } \ ~ | €, dead keys for ^ ´ `) and French AZERTY (digits with Shift, the digit row flipped by Caps Lock, A and
/// Q, Z and W swapped). Each key has its scan code, its virtual key and what it types
/// unshifted, with Shift and with AltGr; VkKeyScan, ScanCode and KeyText answer from that table, so the server's mapping is
/// tested against the layout's own answers rather than against a copy of its logic.
/// </summary>
public sealed class FakeKeyboardLayoutWrapper : INativeKeyboardLayoutWrapper
{
    public static readonly nint Us = 0x04090409;
    public static readonly nint Norwegian = 0x04140414;
    public static readonly nint French = 0x040C040C;
    public static readonly nint German = 0x04070407;

    /// <summary>A layout handle the fake does not know: no key types anything on it.</summary>
    public static readonly nint Empty = 0x7777;

    private const byte Shift = 1, Ctrl = 2, Alt = 4, AltGr = Ctrl | Alt;

    /// <summary>A dead key's character in the tables: the key waits for the next key and types nothing itself.</summary>
    private const char Dead = '\0';

    private static readonly Dictionary<nint, Key[]> Layouts = new()
    {
        [Us] = UsKeys(),
        [Norwegian] = NorwegianKeys(),
        [German] = GermanKeys(),
        [French] = FrenchKeys()
    };

    /// <summary>The scan codes (0xE0 prefix included) of the keys that type nothing, the same on every layout.</summary>
    private static readonly Dictionary<ushort, uint> OtherScanCodes = new()
    {
        [0x08] = 0x0E, [0x09] = 0x0F, [0x0D] = 0x1C, [0x10] = 0x2A, [0x11] = 0x1D, [0x12] = 0x38, [0x13] = 0xE11D,
        [0x1B] = 0x01, [0x20] = 0x39, [0x21] = 0xE049, [0x22] = 0xE051, [0x23] = 0xE04F, [0x24] = 0xE047,
        [0x25] = 0xE04B, [0x26] = 0xE048, [0x27] = 0xE04D, [0x28] = 0xE050, [0x2D] = 0xE052, [0x2E] = 0xE053,
        [0x5B] = 0xE05B, [0x5C] = 0xE05C, [0x5D] = 0xE05D, [0x60] = 0x52, [0x65] = 0x4C, [0x6B] = 0x4E, [0x6F] = 0xE035,
        [0x70] = 0x3B, [0x73] = 0x3E, [0x7C] = 0x64, [0x90] = 0x45, [0xA0] = 0x2A, [0xA1] = 0x36, [0xA2] = 0x1D,
        [0xA3] = 0xE01D, [0xA4] = 0x38, [0xA5] = 0xE038, [0xAD] = 0xE020
    };

    private readonly List<nint> _layoutQueries = new();

    /// <summary>The layout of every window the test does not name in <see cref="WindowLayouts"/>.</summary>
    public nint Layout { get; set; } = Us;

    /// <summary>The layouts of particular windows (their threads').</summary>
    public Dictionary<nint, nint> WindowLayouts { get; } = new();

    /// <summary>The windows <see cref="GetKeyboardLayout"/> was asked about, in order (0 is the foreground window).</summary>
    public IReadOnlyList<nint> LayoutQueries => _layoutQueries.ToList();

    public nint GetKeyboardLayout(nint window)
    {
        _layoutQueries.Add(window);
        return WindowLayouts.TryGetValue(window, out var layout) ? layout : Layout;
    }

    public short VkKeyScan(char character, nint layout)
    {
        // As Windows does: the first key, in the lowest shift state, that types the character (dead keys included)
        foreach (var state in new byte[] { 0, Shift, AltGr })
        {
            foreach (var key in KeysOf(layout))
            {
                if (key.Typed(state) == character || key.DeadChar(state) == character)
                {
                    return (short)(key.VirtualKey | (state << 8));
                }
            }
        }

        return -1;
    }

    public uint ScanCode(ushort virtualKey, nint layout) =>
        KeysOf(layout).FirstOrDefault(k => k.VirtualKey == virtualKey) is { } key ? key.Scan
        : OtherScanCodes.TryGetValue(virtualKey, out var scan) ? scan
        : 0;

    public string? KeyText(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout)
    {
        if (KeysOf(layout).FirstOrDefault(k => k.VirtualKey == virtualKey) is not { } key || (shiftState & ~(Shift | AltGr)) != 0)
        {
            return null;
        }

        // Caps Lock flips Shift on the keys the layout lets it (not with AltGr)
        if (capsLock && key.CapsLock && (shiftState & AltGr) == 0)
        {
            shiftState ^= Shift;
        }

        var typed = key.Typed(shiftState);
        return typed is null or Dead ? null : typed.Value.ToString();
    }

    private static Key[] KeysOf(nint layout) => Layouts.TryGetValue(layout, out var keys) ? keys : [];

    /// <summary>
    /// One key of a layout: what it types unshifted, with Shift and with AltGr (null: nothing; <see cref="Dead"/> marks a
    /// dead key, whose character is in <see cref="DeadChars"/>), and whether Caps Lock flips its Shift.
    /// </summary>
    private sealed record Key(uint Scan, ushort VirtualKey, char? Normal, char? Shifted, char? AltGraph = null, bool CapsLock = false,
        string? DeadChars = null)
    {
        public char? Typed(byte state) => state switch
        {
            0 => Normal,
            Shift => Shifted,
            AltGr => AltGraph,
            _ => null
        };

        /// <summary>The character of a dead key in <paramref name="state"/>, which VkKeyScan finds although the key types nothing.</summary>
        public char? DeadChar(byte state) =>
            Typed(state) == Dead && DeadChars != null ? DeadChars[state == 0 ? 0 : state == Shift ? 1 : 2] : null;
    }

    private static IEnumerable<Key> Letters(string qwertyRows, Func<char, uint> scanOf, bool caps = true) =>
        qwertyRows.Select(c => new Key(scanOf(c), char.ToUpperInvariant(c), c, char.ToUpperInvariant(c), CapsLock: caps));

    /// <summary>The scan codes of the letters at their US positions.</summary>
    private static uint UsScan(char c) => "qwertyuiop".IndexOf(c) is var top and >= 0 ? (uint)(0x10 + top)
        : "asdfghjkl".IndexOf(c) is var home and >= 0 ? (uint)(0x1E + home)
        : (uint)(0x2C + "zxcvbnm".IndexOf(c));

    private static Key[] UsKeys() =>
    [
        .. Letters("abcdefghijklmnopqrstuvwxyz", UsScan),
        .. "1234567890".Select((d, i) => new Key((uint)(0x02 + i), d, d, "!@#$%^&*()"[i])),
        new(0x0C, 0xBD, '-', '_'), new(0x0D, 0xBB, '=', '+'), new(0x1A, 0xDB, '[', '{'), new(0x1B, 0xDD, ']', '}'),
        new(0x2B, 0xDC, '\\', '|'), new(0x27, 0xBA, ';', ':'), new(0x28, 0xDE, '\'', '"'), new(0x29, 0xC0, '`', '~'),
        new(0x33, 0xBC, ',', '<'), new(0x34, 0xBE, '.', '>'), new(0x35, 0xBF, '/', '?'), new(0x39, 0x20, ' ', ' ')
    ];

    private static Key[] NorwegianKeys() =>
    [
        .. Letters("abcdefghijklmnopqrstuvwxyz", UsScan),
        new(0x1A, 0xDD, 'å', 'Å', CapsLock: true), new(0x27, 0xC0, 'ø', 'Ø', CapsLock: true), new(0x28, 0xDE, 'æ', 'Æ', CapsLock: true),
        new(0x02, '1', '1', '!'), new(0x03, '2', '2', '"', '@'), new(0x04, '3', '3', '#', '£'), new(0x05, '4', '4', '¤', '$'),
        new(0x06, '5', '5', '%', '€'), new(0x07, '6', '6', '&'), new(0x08, '7', '7', '/', '{'), new(0x09, '8', '8', '(', '['),
        new(0x0A, '9', '9', ')', ']'), new(0x0B, '0', '0', '=', '}'),
        new(0x0C, 0xBB, '+', '?'), new(0x0D, 0xDB, '\\', Dead, Dead, DeadChars: "\\`´"),
        new(0x1B, 0xBA, Dead, Dead, Dead, DeadChars: "¨^~"),
        new(0x29, 0xDC, '|', '§'), new(0x2B, 0xBF, '\'', '*'), new(0x56, 0xE2, '<', '>'),
        new(0x33, 0xBC, ',', ';'), new(0x34, 0xBE, '.', ':'), new(0x35, 0xBD, '-', '_'), new(0x39, 0x20, ' ', ' ')
    ];

    /// <summary>The scan codes of the QWERTZ letters: Y and Z swapped.</summary>
    private static uint GermanScan(char c) => c switch
    {
        'y' => 0x2C, 'z' => 0x15,
        _ => UsScan(c)
    };

    private static Key[] GermanKeys() =>
    [
        .. Letters("abcdfghijklmnoprstuvwxyz", GermanScan),
        new(0x10, 'Q', 'q', 'Q', '@', CapsLock: true), new(0x12, 'E', 'e', 'E', '€', CapsLock: true),
        new(0x1A, 0xBA, 'ü', 'Ü', CapsLock: true), new(0x27, 0xC0, 'ö', 'Ö', CapsLock: true), new(0x28, 0xDE, 'ä', 'Ä', CapsLock: true),
        new(0x02, '1', '1', '!'), new(0x03, '2', '2', '"', '²'), new(0x04, '3', '3', '§', '³'), new(0x05, '4', '4', '$'),
        new(0x06, '5', '5', '%'), new(0x07, '6', '6', '&'), new(0x08, '7', '7', '/', '{'), new(0x09, '8', '8', '(', '['),
        new(0x0A, '9', '9', ')', ']'), new(0x0B, '0', '0', '=', '}'),
        new(0x0C, 0xDB, 'ß', '?', '\\'), new(0x0D, 0xDD, Dead, Dead, DeadChars: "´` "),
        new(0x1B, 0xBB, '+', '*', '~'), new(0x29, 0xDC, Dead, '°', DeadChars: "^  "), new(0x2B, 0xBF, '#', '\''),
        new(0x56, 0xE2, '<', '>', '|'), new(0x33, 0xBC, ',', ';'), new(0x34, 0xBE, '.', ':'), new(0x35, 0xBD, '-', '_'),
        new(0x39, 0x20, ' ', ' ')
    ];

    /// <summary>The scan codes of the AZERTY letters: A and Q, Z and W swapped, M where US has ';'.</summary>
    private static uint FrenchScan(char c) => c switch
    {
        'a' => 0x10, 'z' => 0x11, 'q' => 0x1E, 'w' => 0x2C, 'm' => 0x27,
        _ => UsScan(c)
    };

    private static Key[] FrenchKeys() =>
    [
        .. Letters("abcdefghijklmnopqrstuvwxyz", FrenchScan),
        // The digit row types symbols unshifted and the digits with Shift; Caps Lock flips it
        .. "1234567890".Select((d, i) => new Key((uint)(0x02 + i), d, "&é\"'(-è_çà"[i], d, AltGraph: i == 0 ? null : "~#{[|`\\^@"[i - 1],
            CapsLock: true)),
        new(0x0C, 0xDB, ')', '°', ']'), new(0x0D, 0xBB, '=', '+', '}'),
        new(0x1A, 0xDD, Dead, Dead, DeadChars: "^¨ "), new(0x1B, 0xBA, '$', '£', '¤'),
        new(0x28, 0xC0, 'ù', '%'), new(0x2B, 0xDC, '*', 'µ'), new(0x56, 0xE2, '<', '>'),
        new(0x32, 0xBC, ',', '?'), new(0x33, 0xBE, ';', '.'), new(0x34, 0xBF, ':', '/'), new(0x35, 0xDF, '!', '§'),
        new(0x39, 0x20, ' ', ' ')
    ];
}
