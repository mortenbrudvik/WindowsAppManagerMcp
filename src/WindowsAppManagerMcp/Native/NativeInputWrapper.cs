using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeInputWrapper that calls real Windows API functions.
/// </summary>
public class NativeInputWrapper : INativeInputWrapper
{
    public bool SetCursorPos(int x, int y)
    {
        return NativeMethods.Input.SetCursorPos(x, y);
    }

    public (int X, int Y)? GetCursorPos()
    {
        if (!NativeMethods.Input.GetCursorPos(out var point))
            return null;

        return (point.X, point.Y);
    }

    public uint SendInput(IReadOnlyList<InputEvent> events)
    {
        var inputs = new INPUT[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            inputs[i] = ToNativeInput(events[i]);
        }

        return NativeMethods.Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT ToNativeInput(InputEvent inputEvent) => inputEvent.Kind switch
    {
        InputEventKind.Mouse => INPUT.MouseInput(0, 0, inputEvent.Flags),
        InputEventKind.VirtualKey => INPUT.KeyboardInput(inputEvent.Code, inputEvent.Flags),
        InputEventKind.Unicode => INPUT.UnicodeInput((char)inputEvent.Code, inputEvent.Flags),
        _ => throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent.Kind, "Unknown input event kind")
    };
}
