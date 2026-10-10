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

        return new GuiThreadInfo(info.flags, info.hwndActive, info.hwndFocus, info.hwndCapture, info.hwndMenuOwner);
    }
}
