using System.Collections.Concurrent;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeWinEventWrapper"/> (roadmap M3.F22): out-of-context WinEvent hooks for
/// the calling thread, and that thread's GetMessage loop, in which Windows calls them.
/// </summary>
public sealed class NativeWinEventWrapper : INativeWinEventWrapper
{
    private const uint WmUser = 0x0400;
    private const uint PmNoRemove = 0x0000;

    /// <summary>WINEVENT_OUTOFCONTEXT: the callback runs in the server, on the thread that installed the hook.</summary>
    private const uint WinEventOutOfContext = 0x0000;

    /// <summary>WINEVENT_SKIPOWNPROCESS: no events of the server's own windows.</summary>
    private const uint WinEventSkipOwnProcess = 0x0002;

    /// <summary>WM_APP + 0x58: the subscribers of the event thread changed.</summary>
    internal const uint UpdateMessage = 0x8000 + 0x58;

    /// <summary>WM_APP + 0x59: the message that ends the event thread's loop.</summary>
    internal const uint StopMessage = 0x8000 + 0x59;

    // The callbacks of the installed hooks: Windows holds only a pointer to each, so they must stay alive until removed
    private readonly ConcurrentDictionary<nint, NativeMethods.WinEvents.WinEventProc> _callbacks = new();

    /// <inheritdoc />
    public uint GetCurrentThreadId() => NativeMethods.Kernel32.GetCurrentThreadId();

    /// <inheritdoc />
    public void CreateMessageQueue() => NativeMethods.WinEvents.PeekMessageW(out _, nint.Zero, WmUser, WmUser, PmNoRemove);

    /// <inheritdoc />
    public nint SetWinEventHook(uint eventMin, uint eventMax, WinEventCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        NativeMethods.WinEvents.WinEventProc proc = (_, eventType, window, objectId, childId, _, _) =>
        {
            try
            {
                callback(eventType, window, objectId, childId);
            }
            catch (Exception)
            {
                // An exception must not cross back into Windows, which called the callback: it would end the process
            }
        };

        var hook = NativeMethods.WinEvents.SetWinEventHook(
            eventMin, eventMax, nint.Zero, proc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook != nint.Zero)
        {
            _callbacks[hook] = proc;
        }

        return hook;
    }

    /// <inheritdoc />
    public bool UnhookWinEvent(nint hook)
    {
        var removed = NativeMethods.WinEvents.UnhookWinEvent(hook);
        _callbacks.TryRemove(hook, out _);
        return removed;
    }

    /// <inheritdoc />
    public WinEventLoopMessage WaitForMessage()
    {
        var result = NativeMethods.WinEvents.GetMessageW(out var message, nint.Zero, 0, 0);
        if (result == -1)
        {
            return WinEventLoopMessage.Failed;
        }

        if (result == 0)
        {
            return WinEventLoopMessage.Stop;
        }

        return message.message switch
        {
            UpdateMessage => WinEventLoopMessage.Update,
            StopMessage => WinEventLoopMessage.Stop,
            _ => WinEventLoopMessage.Other
        };
    }

    /// <inheritdoc />
    public bool PostUpdateMessage(uint threadId) =>
        NativeMethods.WinEvents.PostThreadMessageW(threadId, UpdateMessage, 0, nint.Zero);

    /// <inheritdoc />
    public bool PostStopMessage(uint threadId) =>
        NativeMethods.WinEvents.PostThreadMessageW(threadId, StopMessage, 0, nint.Zero);
}
