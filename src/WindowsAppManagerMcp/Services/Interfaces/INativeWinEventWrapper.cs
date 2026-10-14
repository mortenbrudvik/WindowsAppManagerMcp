namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The native side of the window events behind wait_for_window's wake-ups (roadmap M3.F22): out-of-context WinEvent hooks
/// (SetWinEventHook with WINEVENT_OUTOFCONTEXT) installed by the calling thread, and that thread's message loop, in which
/// Windows calls their callbacks. Only the server's window event thread calls it (<c>WindowEventSource</c>); tests replace
/// it with a fake, so no test installs a real hook.
/// </summary>
/// <remarks>
/// An out-of-context hook loads nothing into another process and changes no window: Windows queues each event to the
/// thread that installed the hook and calls the callback there while that thread waits for messages. The thread that
/// installed a hook must remove it.
/// </remarks>
public interface INativeWinEventWrapper
{
    /// <summary>The ID of the calling thread (GetCurrentThreadId).</summary>
    uint GetCurrentThreadId();

    /// <summary>
    /// Gives the calling thread a message queue (PeekMessage), so another thread can post it a message before it first waits
    /// for one.
    /// </summary>
    void CreateMessageQueue();

    /// <summary>
    /// Installs an out-of-context hook for the events <paramref name="eventMin"/> to <paramref name="eventMax"/> of every
    /// process but the server's (WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS). <paramref name="callback"/> is called on
    /// the calling thread while it waits for messages (<see cref="WaitForMessage"/>); the wrapper keeps it alive until
    /// <see cref="UnhookWinEvent"/>. Zero when the hook could not be installed.
    /// </summary>
    nint SetWinEventHook(uint eventMin, uint eventMax, WinEventCallback callback);

    /// <summary>Removes a hook the calling thread installed (UnhookWinEvent).</summary>
    bool UnhookWinEvent(nint hook);

    /// <summary>Waits for the calling thread's next message (GetMessage), calling the hooks' callbacks meanwhile.</summary>
    WinEventLoopMessage WaitForMessage();

    /// <summary>Posts the update message to thread <paramref name="threadId"/> (PostThreadMessage): its subscribers changed.</summary>
    bool PostUpdateMessage(uint threadId);

    /// <summary>Posts the stop message to thread <paramref name="threadId"/> (PostThreadMessage).</summary>
    bool PostStopMessage(uint threadId);
}

/// <summary>A WinEvent: its event constant, the window, and the object and child it is about (OBJID_WINDOW and CHILDID_SELF: the window itself).</summary>
public delegate void WinEventCallback(uint eventType, nint window, int objectId, int childId);

/// <summary>What <see cref="INativeWinEventWrapper.WaitForMessage"/> got.</summary>
public enum WinEventLoopMessage
{
    /// <summary>The update message: the subscribers changed, so the hooks may be wanted or no longer wanted.</summary>
    Update,

    /// <summary>The stop message (or WM_QUIT): the loop ends.</summary>
    Stop,

    /// <summary>Any other message; the loop waits for the next one.</summary>
    Other,

    /// <summary>GetMessage failed: the loop ends.</summary>
    Failed
}
