using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The windows set_window_zorder made topmost (roadmap M6.7, owner decision D14), so get_server_info can list what the
/// server left pinned: a topmost window stays in front of the user's work until it is unpinned or closes. The server does
/// not unpin them itself, not even when it stops (that cannot be promised after a crash); it only makes them visible.
/// A window that closed, whose handle another window got, or that is no longer topmost is dropped when it is read.
/// </summary>
public sealed class TopmostPins
{
    private readonly ConcurrentDictionary<nint, uint> _pins = new();

    /// <summary>The pinned windows with the thread each had when it was pinned, in no particular order.</summary>
    public IReadOnlyList<TopmostPin> Windows => _pins.Select(p => new TopmostPin(p.Key, p.Value)).ToList();

    /// <summary>Records that the server made <paramref name="window"/> (of thread <paramref name="threadId"/>) topmost.</summary>
    public void Pin(nint window, uint threadId) => _pins[window] = threadId;

    /// <summary>Forgets <paramref name="window"/>: the server unpinned it, or it is gone.</summary>
    public void Unpin(nint window) => _pins.TryRemove(window, out _);
}

/// <summary>A window the server made topmost, and the thread that owned it then (a reused handle has another).</summary>
public sealed record TopmostPin(nint Window, uint ThreadId);
