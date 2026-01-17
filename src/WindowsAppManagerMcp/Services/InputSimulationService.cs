using System.Runtime.InteropServices;
using System.Text;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for simulating mouse input using Windows SendInput API.
/// </summary>
public class InputSimulationService : IInputSimulationService
{
    private readonly IMonitorService _monitorService;

    public InputSimulationService(IMonitorService monitorService)
    {
        _monitorService = monitorService;
    }

    public ClickResult Click(int x, int y)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "left",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        if (!SetCursorAndClick(x, y, leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "left",
                Error: "SendInput failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "left");
    }

    public ClickResult RightClick(int x, int y)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "right",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        if (!SetCursorAndClick(x, y, leftButton: false))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "right",
                Error: "SendInput failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "right");
    }

    public ClickResult DoubleClick(int x, int y, int delayMs = 50)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        // Move cursor to position first
        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Failed to set cursor position",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        // Small delay to ensure cursor position is updated
        Thread.Sleep(10);

        // First click
        if (!SendClickInputs(leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "First click in double-click failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        // Delay between clicks
        Thread.Sleep(Math.Clamp(delayMs, 10, 500));

        // Second click
        if (!SendClickInputs(leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Second click in double-click failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "double");
    }

    public MouseMoveResult MoveMouse(int x, int y)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new MouseMoveResult(
                Success: false,
                X: x,
                Y: y,
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return new MouseMoveResult(
                Success: false,
                X: x,
                Y: y,
                Error: "Failed to set cursor position",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new MouseMoveResult(
            Success: true,
            X: x,
            Y: y);
    }

    public (int X, int Y)? GetCursorPosition()
    {
        if (NativeMethods.Input.GetCursorPos(out var point))
        {
            return (point.X, point.Y);
        }
        return null;
    }

    public bool IsValidScreenCoordinate(int x, int y)
    {
        var monitors = _monitorService.GetAllMonitors();
        return monitors.Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);
    }

    private bool SetCursorAndClick(int x, int y, bool leftButton)
    {
        // Move cursor to position
        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return false;
        }

        // Small delay to ensure cursor position is updated
        Thread.Sleep(10);

        return SendClickInputs(leftButton);
    }

    private static bool SendClickInputs(bool leftButton)
    {
        var downFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTDOWN : NativeEnums.MOUSEEVENTF_RIGHTDOWN;
        var upFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTUP : NativeEnums.MOUSEEVENTF_RIGHTUP;

        var inputs = new INPUT[]
        {
            INPUT.MouseInput(0, 0, downFlag),
            INPUT.MouseInput(0, 0, upFlag)
        };

        var result = NativeMethods.Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return result == inputs.Length;
    }

    public TypeTextResult TypeText(string text, int delayBetweenKeysMs = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TypeTextResult(
                Success: false,
                Text: text ?? "",
                CharactersTyped: 0,
                Error: "Text cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        delayBetweenKeysMs = Math.Clamp(delayBetweenKeysMs, 0, 100);
        var charactersTyped = 0;

        foreach (var character in text)
        {
            var inputs = new INPUT[]
            {
                INPUT.UnicodeInput(character, NativeEnums.KEYEVENTF_KEYDOWN),
                INPUT.UnicodeInput(character, NativeEnums.KEYEVENTF_KEYUP)
            };

            var result = NativeMethods.Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (result != inputs.Length)
            {
                return new TypeTextResult(
                    Success: false,
                    Text: text,
                    CharactersTyped: charactersTyped,
                    Error: $"SendInput failed at character {charactersTyped}",
                    ErrorCode: InputErrorCode.SendInputFailed);
            }

            charactersTyped++;

            if (delayBetweenKeysMs > 0 && charactersTyped < text.Length)
            {
                Thread.Sleep(delayBetweenKeysMs);
            }
        }

        return new TypeTextResult(
            Success: true,
            Text: text,
            CharactersTyped: charactersTyped);
    }

    public SendKeysResult SendKeys(string keys)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys ?? "",
                Error: "Keys cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        try
        {
            var inputs = ParseSendKeysString(keys);
            if (inputs.Count == 0)
            {
                return new SendKeysResult(
                    Success: false,
                    Keys: keys,
                    Error: "No valid keys parsed from input",
                    ErrorCode: InputErrorCode.InvalidKeySpecification);
            }

            var inputArray = inputs.ToArray();
            var result = NativeMethods.Input.SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<INPUT>());

            if (result != inputArray.Length)
            {
                return new SendKeysResult(
                    Success: false,
                    Keys: keys,
                    Error: "SendInput failed to send all keystrokes",
                    ErrorCode: InputErrorCode.SendInputFailed);
            }

            return new SendKeysResult(
                Success: true,
                Keys: keys);
        }
        catch (ArgumentException ex)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: ex.Message,
                ErrorCode: InputErrorCode.UnknownKey);
        }
    }

    private static List<INPUT> ParseSendKeysString(string keys)
    {
        var inputs = new List<INPUT>();
        var modifiers = new List<ushort>();
        var i = 0;

        while (i < keys.Length)
        {
            var c = keys[i];

            // Handle modifier prefixes
            if (c == '^' || c == '%' || c == '+')
            {
                var modifierVk = c switch
                {
                    '^' => NativeEnums.VK_CONTROL,
                    '%' => NativeEnums.VK_MENU,
                    '+' => NativeEnums.VK_SHIFT,
                    _ => (ushort)0
                };

                if (modifierVk != 0 && !modifiers.Contains(modifierVk))
                {
                    modifiers.Add(modifierVk);
                    inputs.Add(INPUT.KeyboardInput(modifierVk, NativeEnums.KEYEVENTF_KEYDOWN));
                }
                i++;
                continue;
            }

            // Handle special keys in braces
            if (c == '{')
            {
                var endBrace = keys.IndexOf('}', i + 1);
                if (endBrace == -1)
                {
                    throw new ArgumentException($"Unclosed brace at position {i}");
                }

                var keyName = keys.Substring(i + 1, endBrace - i - 1).ToUpperInvariant();
                var vk = GetVirtualKeyCode(keyName);
                var flags = IsExtendedKey(vk) ? NativeEnums.KEYEVENTF_EXTENDEDKEY : NativeEnums.KEYEVENTF_KEYDOWN;

                inputs.Add(INPUT.KeyboardInput(vk, flags));
                inputs.Add(INPUT.KeyboardInput(vk, flags | NativeEnums.KEYEVENTF_KEYUP));

                // Release modifiers after the key
                ReleaseModifiers(inputs, modifiers);
                modifiers.Clear();

                i = endBrace + 1;
                continue;
            }

            // Handle regular characters
            var charVk = GetVirtualKeyForChar(c);
            if (charVk != 0)
            {
                inputs.Add(INPUT.KeyboardInput(charVk, NativeEnums.KEYEVENTF_KEYDOWN));
                inputs.Add(INPUT.KeyboardInput(charVk, NativeEnums.KEYEVENTF_KEYUP));
            }
            else
            {
                // Send as Unicode
                inputs.Add(INPUT.UnicodeInput(c, NativeEnums.KEYEVENTF_KEYDOWN));
                inputs.Add(INPUT.UnicodeInput(c, NativeEnums.KEYEVENTF_KEYUP));
            }

            // Release modifiers after the key
            ReleaseModifiers(inputs, modifiers);
            modifiers.Clear();

            i++;
        }

        // Release any remaining modifiers
        ReleaseModifiers(inputs, modifiers);

        return inputs;
    }

    private static void ReleaseModifiers(List<INPUT> inputs, List<ushort> modifiers)
    {
        // Release in reverse order
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            inputs.Add(INPUT.KeyboardInput(modifiers[i], NativeEnums.KEYEVENTF_KEYUP));
        }
    }

    private static ushort GetVirtualKeyCode(string keyName)
    {
        return keyName switch
        {
            "ENTER" or "RETURN" => NativeEnums.VK_RETURN,
            "TAB" => NativeEnums.VK_TAB,
            "ESC" or "ESCAPE" => NativeEnums.VK_ESCAPE,
            "BACKSPACE" or "BS" or "BKSP" => NativeEnums.VK_BACK,
            "DELETE" or "DEL" => NativeEnums.VK_DELETE,
            "INSERT" or "INS" => NativeEnums.VK_INSERT,
            "UP" => NativeEnums.VK_UP,
            "DOWN" => NativeEnums.VK_DOWN,
            "LEFT" => NativeEnums.VK_LEFT,
            "RIGHT" => NativeEnums.VK_RIGHT,
            "HOME" => NativeEnums.VK_HOME,
            "END" => NativeEnums.VK_END,
            "PGUP" or "PAGEUP" => NativeEnums.VK_PRIOR,
            "PGDN" or "PAGEDOWN" => NativeEnums.VK_NEXT,
            "SPACE" => NativeEnums.VK_SPACE,
            "F1" => NativeEnums.VK_F1,
            "F2" => NativeEnums.VK_F2,
            "F3" => NativeEnums.VK_F3,
            "F4" => NativeEnums.VK_F4,
            "F5" => NativeEnums.VK_F5,
            "F6" => NativeEnums.VK_F6,
            "F7" => NativeEnums.VK_F7,
            "F8" => NativeEnums.VK_F8,
            "F9" => NativeEnums.VK_F9,
            "F10" => NativeEnums.VK_F10,
            "F11" => NativeEnums.VK_F11,
            "F12" => NativeEnums.VK_F12,
            "CAPSLOCK" or "CAPS" => NativeEnums.VK_CAPITAL,
            "NUMLOCK" => NativeEnums.VK_NUMLOCK,
            "SCROLLLOCK" or "SCROLL" => NativeEnums.VK_SCROLL,
            "PRTSC" or "PRINTSCREEN" => NativeEnums.VK_SNAPSHOT,
            "PAUSE" or "BREAK" => NativeEnums.VK_PAUSE,
            "WIN" or "LWIN" => NativeEnums.VK_LWIN,
            "RWIN" => NativeEnums.VK_RWIN,
            "APPS" or "CONTEXTMENU" => NativeEnums.VK_APPS,
            "CTRL" or "CONTROL" => NativeEnums.VK_CONTROL,
            "ALT" or "MENU" => NativeEnums.VK_MENU,
            "SHIFT" => NativeEnums.VK_SHIFT,
            _ => throw new ArgumentException($"Unknown key: {keyName}")
        };
    }

    private static bool IsExtendedKey(ushort vk)
    {
        // Extended keys include arrow keys, Insert, Delete, Home, End, Page Up, Page Down,
        // Num Lock, Print Screen, and the right-hand Alt and Ctrl keys
        return vk == NativeEnums.VK_UP || vk == NativeEnums.VK_DOWN ||
               vk == NativeEnums.VK_LEFT || vk == NativeEnums.VK_RIGHT ||
               vk == NativeEnums.VK_INSERT || vk == NativeEnums.VK_DELETE ||
               vk == NativeEnums.VK_HOME || vk == NativeEnums.VK_END ||
               vk == NativeEnums.VK_PRIOR || vk == NativeEnums.VK_NEXT ||
               vk == NativeEnums.VK_NUMLOCK || vk == NativeEnums.VK_SNAPSHOT ||
               vk == NativeEnums.VK_RCONTROL || vk == NativeEnums.VK_RMENU ||
               vk == NativeEnums.VK_LWIN || vk == NativeEnums.VK_RWIN ||
               vk == NativeEnums.VK_APPS;
    }

    private static ushort GetVirtualKeyForChar(char c)
    {
        // For alphanumeric characters, return the VK code
        if (c >= 'a' && c <= 'z')
        {
            return (ushort)('A' + (c - 'a'));
        }
        if (c >= 'A' && c <= 'Z')
        {
            return (ushort)c;
        }
        if (c >= '0' && c <= '9')
        {
            return (ushort)c;
        }

        // For other characters, return 0 to indicate Unicode should be used
        return 0;
    }
}
