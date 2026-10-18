using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The key names of send_keys' <c>{NAME}</c> (roadmap M4.1), shared with the tools that name keys. A name is matched
/// without regard to case, a leading <c>VK_</c> or underscores, so <c>{PGDN}</c>, <c>{VK_OEM_1}</c> and
/// <c>{volume_mute}</c> all work. Keys that would sleep, power off or log off the computer have no name.
/// </summary>
public static class KeyTable
{
    private static readonly Dictionary<string, ushort> ByName = BuildNames();

    /// <summary>The name each virtual key is written with, for messages: the first name the table gives it.</summary>
    private static readonly Dictionary<ushort, string> NameByKey = ByName
        .GroupBy(entry => entry.Value)
        .ToDictionary(group => group.Key, group => group.First().Key);

    /// <summary>The virtual key <paramref name="name"/> stands for, or null for an unknown name.</summary>
    public static ushort? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = name.Trim().ToUpperInvariant();
        if (key.StartsWith("VK_", StringComparison.Ordinal))
        {
            key = key[3..];
        }

        return ByName.TryGetValue(key.Replace("_", string.Empty), out var virtualKey) ? virtualKey : null;
    }

    /// <summary>The name of <paramref name="virtualKey"/> as <c>{NAME}</c> takes it, or null when it has none.</summary>
    public static string? NameOf(ushort virtualKey) => NameByKey.TryGetValue(virtualKey, out var name) ? name : null;

    /// <summary>
    /// True for the keys that need KEYEVENTF_EXTENDEDKEY even when the layout gives no 0xE0 scan code: the right Ctrl and
    /// Alt, the Windows and Apps keys, the navigation block and arrows, Num Lock, Print Screen, the numeric keypad's
    /// divide key, and the browser and media keys.
    /// </summary>
    public static bool IsExtended(ushort virtualKey) =>
        KeyNames.IsExtended(virtualKey) || virtualKey == NativeEnums.VK_DIVIDE || virtualKey is >= VkBrowserBack and <= VkLaunchApp2;

    /// <summary>True for Shift, Ctrl, Alt and the Windows keys, generic or left/right.</summary>
    public static bool IsModifier(ushort virtualKey) => KeyNames.IsModifier(virtualKey);

    private const ushort VkF13 = 0x7C;
    private const ushort VkBrowserBack = 0xA6;
    private const ushort VkLaunchApp2 = 0xB7;

    private static Dictionary<string, ushort> BuildNames()
    {
        var names = new Dictionary<string, ushort>(StringComparer.Ordinal);
        void Add(ushort virtualKey, params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                names.Add(alias, virtualKey);
            }
        }

        Add(NativeEnums.VK_RETURN, "ENTER", "RETURN");
        Add(NativeEnums.VK_TAB, "TAB");
        Add(NativeEnums.VK_ESCAPE, "ESC", "ESCAPE");
        Add(NativeEnums.VK_BACK, "BACKSPACE", "BS", "BKSP", "BACK");
        Add(NativeEnums.VK_DELETE, "DELETE", "DEL");
        Add(NativeEnums.VK_INSERT, "INSERT", "INS");
        Add(NativeEnums.VK_UP, "UP");
        Add(NativeEnums.VK_DOWN, "DOWN");
        Add(NativeEnums.VK_LEFT, "LEFT");
        Add(NativeEnums.VK_RIGHT, "RIGHT");
        Add(NativeEnums.VK_HOME, "HOME");
        Add(NativeEnums.VK_END, "END");
        Add(NativeEnums.VK_PRIOR, "PGUP", "PAGEUP", "PRIOR");
        Add(NativeEnums.VK_NEXT, "PGDN", "PAGEDOWN", "NEXT");
        Add(NativeEnums.VK_SPACE, "SPACE");
        Add(NativeEnums.VK_CLEAR, "CLEAR");
        Add(NativeEnums.VK_CAPITAL, "CAPSLOCK", "CAPS", "CAPITAL");
        Add(NativeEnums.VK_NUMLOCK, "NUMLOCK");
        Add(NativeEnums.VK_SCROLL, "SCROLLLOCK", "SCROLL");
        Add(NativeEnums.VK_SNAPSHOT, "PRTSC", "PRINTSCREEN", "PRTSCN", "SNAPSHOT");
        Add(NativeEnums.VK_PAUSE, "PAUSE", "BREAK");
        Add(NativeEnums.VK_APPS, "APPS", "CONTEXTMENU");

        Add(NativeEnums.VK_LWIN, "WIN", "LWIN");
        Add(NativeEnums.VK_RWIN, "RWIN");
        Add(NativeEnums.VK_CONTROL, "CTRL", "CONTROL");
        Add(NativeEnums.VK_LCONTROL, "LCTRL", "LCONTROL");
        Add(NativeEnums.VK_RCONTROL, "RCTRL", "RCONTROL");
        Add(NativeEnums.VK_MENU, "ALT", "MENU");
        Add(NativeEnums.VK_LMENU, "LALT", "LMENU");
        Add(NativeEnums.VK_RMENU, "RALT", "RMENU");
        Add(NativeEnums.VK_SHIFT, "SHIFT");
        Add(NativeEnums.VK_LSHIFT, "LSHIFT");
        Add(NativeEnums.VK_RSHIFT, "RSHIFT");

        for (var f = 1; f <= 24; f++)
        {
            Add((ushort)(f <= 12 ? NativeEnums.VK_F1 + f - 1 : VkF13 + f - 13), $"F{f}");
        }

        // The numeric keypad
        for (var n = 0; n <= 9; n++)
        {
            Add((ushort)(NativeEnums.VK_NUMPAD0 + n), $"NUMPAD{n}");
        }

        Add(NativeEnums.VK_MULTIPLY, "MULTIPLY");
        Add(NativeEnums.VK_ADD, "ADD");
        Add(NativeEnums.VK_SEPARATOR, "SEPARATOR");
        Add(NativeEnums.VK_SUBTRACT, "SUBTRACT");
        Add(NativeEnums.VK_DECIMAL, "DECIMAL");
        Add(NativeEnums.VK_DIVIDE, "DIVIDE");

        // The punctuation keys by virtual key: which physical key each is, and what it types, depends on the layout (VK_OEM_1
        // is ';' on a US keyboard and 'ü' on a German one; VK_OEM_PLUS is the = key on a US keyboard, the + key right of 0 on a
        // Norwegian one)
        Add(0xBA, "OEM1");
        Add(0xBB, "OEMPLUS");
        Add(0xBC, "OEMCOMMA");
        Add(0xBD, "OEMMINUS");
        Add(0xBE, "OEMPERIOD");
        Add(0xBF, "OEM2");
        Add(0xC0, "OEM3");
        Add(0xDB, "OEM4");
        Add(0xDC, "OEM5");
        Add(0xDD, "OEM6");
        Add(0xDE, "OEM7");
        Add(0xDF, "OEM8");
        Add(0xE2, "OEM102");

        // Browser, volume, media and launch keys (VK_SLEEP is left out on purpose)
        Add(0xA6, "BROWSERBACK");
        Add(0xA7, "BROWSERFORWARD");
        Add(0xA8, "BROWSERREFRESH");
        Add(0xA9, "BROWSERSTOP");
        Add(0xAA, "BROWSERSEARCH");
        Add(0xAB, "BROWSERFAVORITES");
        Add(0xAC, "BROWSERHOME");
        Add(0xAD, "VOLUMEMUTE");
        Add(0xAE, "VOLUMEDOWN");
        Add(0xAF, "VOLUMEUP");
        Add(0xB0, "MEDIANEXTTRACK", "MEDIANEXT");
        Add(0xB1, "MEDIAPREVTRACK", "MEDIAPREV");
        Add(0xB2, "MEDIASTOP");
        Add(0xB3, "MEDIAPLAYPAUSE");
        Add(0xB4, "LAUNCHMAIL");
        Add(0xB5, "LAUNCHMEDIASELECT");
        Add(0xB6, "LAUNCHAPP1");
        Add(0xB7, "LAUNCHAPP2");
        return names;
    }
}
