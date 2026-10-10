namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The thread side of window activation: thread IDs, attaching the calling thread's input queue to
/// another thread's, and a thread's GUI state (its active and focus windows, menu mode). Activating a
/// window itself (SetForegroundWindow, BringWindowToTop) stays on <see cref="INativeWindowWrapper"/>.
/// </summary>
/// <remarks>
/// Only <see cref="AttachThreadInput"/> changes anything. While two threads are attached they share
/// keyboard state, keyboard focus and the active window, which is what lets the calling thread pass
/// the foreground lock. It sends no input and changes no window by itself.
/// </remarks>
public interface INativeThreadInputWrapper
{
    /// <summary>The ID of the calling thread (GetCurrentThreadId).</summary>
    uint GetCurrentThreadId();

    /// <summary>
    /// The ID of the thread that created the window (the return value of GetWindowThreadProcessId),
    /// or 0 when the handle is not a window.
    /// </summary>
    uint GetWindowThreadId(nint hWnd);

    /// <summary>
    /// Attaches the input queue of thread <paramref name="attachThreadId"/> to that of thread
    /// <paramref name="attachToThreadId"/>, or detaches it when <paramref name="attach"/> is false
    /// (AttachThreadInput). Returns false when Windows refuses: a thread has no message queue, the
    /// threads are on different desktops, or UIPI blocks the attach (the other thread's process has a
    /// higher integrity level). An attach must be undone by a detach from the same thread.
    /// </summary>
    bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

    /// <summary>
    /// The GUI state of thread <paramref name="threadId"/> (GetGUIThreadInfo): its active, focus,
    /// capture and menu owner windows, and whether it is in menu mode. Null when it cannot be read,
    /// for example when the thread has exited or has no GUI.
    /// </summary>
    GuiThreadInfo? GetGuiThreadInfo(uint threadId);
}

/// <summary>
/// A thread's GUI state, as GetGUIThreadInfo reports it.
/// </summary>
/// <param name="Flags">The GUI_* flags, for example <see cref="InMenuModeFlag"/>.</param>
/// <param name="ActiveWindow">The thread's active window, or 0.</param>
/// <param name="FocusWindow">The window with keyboard focus, or 0.</param>
/// <param name="CaptureWindow">The window that has captured the mouse, or 0.</param>
/// <param name="MenuOwnerWindow">The window that owns an active menu, or 0.</param>
public readonly record struct GuiThreadInfo(
    uint Flags,
    nint ActiveWindow,
    nint FocusWindow,
    nint CaptureWindow,
    nint MenuOwnerWindow)
{
    /// <summary>GUI_INMENUMODE: the thread's active window is in menu mode (a menu bar or menu is active).</summary>
    public const uint InMenuModeFlag = 0x0004;

    /// <summary>GUI_POPUPMENUMODE: the thread has an active pop-up menu.</summary>
    public const uint PopupMenuModeFlag = 0x0010;

    /// <summary>
    /// True when a menu bar or menu of the thread is active. Keys then go to the menu (letters pick
    /// menu items) instead of the window's controls.
    /// </summary>
    public bool IsInMenuMode => (Flags & (InMenuModeFlag | PopupMenuModeFlag)) != 0;
}
