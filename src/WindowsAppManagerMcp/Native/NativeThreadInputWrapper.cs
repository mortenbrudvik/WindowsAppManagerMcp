using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeThreadInputWrapper"/>. Everything but
/// <see cref="AttachThreadInput"/> only reads.
/// </summary>
public class NativeThreadInputWrapper : INativeThreadInputWrapper
{
    /// <inheritdoc />
    public uint GetCurrentThreadId() => NativeMethods.Kernel32.GetCurrentThreadId();

    /// <inheritdoc />
    public uint GetWindowThreadId(nint hWnd) =>
        hWnd == nint.Zero ? 0 : NativeMethods.User32.GetWindowThreadProcessId(hWnd, out _);

    /// <inheritdoc />
    public bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach) =>
        NativeMethods.User32.AttachThreadInput(attachThreadId, attachToThreadId, attach);

    /// <inheritdoc />
    public GuiThreadInfo? GetGuiThreadInfo(uint threadId)
    {
        if (threadId == 0)
            return null;

        var info = GUITHREADINFO.Create();
        if (!NativeMethods.User32.GetGUIThreadInfo(threadId, ref info))
            return null;

        return new GuiThreadInfo(info.flags, info.hwndActive, info.hwndFocus, info.hwndCapture, info.hwndMenuOwner, info.hwndCaret,
            CaretOnScreen(info.hwndCaret, info.rcCaret));
    }

    /// <summary>
    /// The caret's client rectangle on the screen: MapWindowPoints with a RECT keeps left &lt; right in a mirrored
    /// right-to-left window too. Null without a caret window or rectangle, or when it cannot be mapped.
    /// </summary>
    private static WindowRect? CaretOnScreen(nint caretWindow, RECT caret)
    {
        if (caretWindow == nint.Zero || caret.Right <= caret.Left || caret.Bottom <= caret.Top)
            return null;

        Marshal.SetLastPInvokeError(0);
        if (NativeMethods.User32.MapWindowPoints(caretWindow, nint.Zero, ref caret, 2) == 0 && Marshal.GetLastPInvokeError() != 0)
            return null;

        return WindowRect.FromLTRB(caret.Left, caret.Top, caret.Right, caret.Bottom);
    }
}
