using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Takeover detection (roadmap M7.4, opt-in): whether the user used the keyboard or mouse while a long input operation
/// (type_text, drag, run_steps) ran. Only the user's own input counts, never injected input (this server's, or another
/// program's SendInput), and only its time is kept: never which key, button or where. It complements the pause hotkey
/// (roadmap M1.10), which stays the primary control.
/// </summary>
public interface ITakeoverMonitor
{
    /// <summary>The mode the user set: off, warn or stop.</summary>
    TakeoverMode Mode { get; }

    /// <summary>The mode and whether the hooks work, for get_control_status.</summary>
    TakeoverStatus Status { get; }

    /// <summary>
    /// Starts watching for the user's input for one operation, from now until the watch is disposed. With mode off the watch
    /// never fires.
    /// </summary>
    TakeoverWatch Watch();
}

/// <summary>
/// One operation's watch for the user's input (<see cref="ITakeoverMonitor.Watch"/>): <see cref="UserActed"/> once the user
/// pressed a key or button, turned the wheel or moved the mouse since it started, and with mode stop <see cref="Token"/> is
/// cancelled then, so the operation stops between two batches or in a wait. <see cref="Unwatched"/> once detection did not
/// work for some of that time.
/// </summary>
public sealed class TakeoverWatch : IDisposable
{
    private readonly CancellationTokenSource? _stop;
    private readonly Action<TakeoverWatch>? _ended;
    private string? _unwatched;
    private int _acted;
    private int _disposed;

    /// <param name="mode">The mode: with stop, <see cref="Token"/> is cancelled when the user acts.</param>
    /// <param name="ended">Called once when the watch is disposed (the monitor stops counting it).</param>
    public TakeoverWatch(TakeoverMode mode, Action<TakeoverWatch>? ended = null)
    {
        Mode = mode;
        _stop = mode == TakeoverMode.Stop ? new CancellationTokenSource() : null;
        _ended = ended;
    }

    /// <summary>A watch that never fires: mode off.</summary>
    public static TakeoverWatch None => new(TakeoverMode.Off);

    /// <summary>The mode the watch was started with.</summary>
    public TakeoverMode Mode { get; }

    /// <summary>Cancelled when the user acts and the mode is stop; never cancelled otherwise.</summary>
    public CancellationToken Token => _stop?.Token ?? CancellationToken.None;

    /// <summary>True once the user used the keyboard or mouse since the watch started (any mode but off).</summary>
    public bool UserActed => Volatile.Read(ref _acted) != 0;

    /// <summary>True when the user acted and the mode is stop: the operation must stop with UserInterrupted.</summary>
    public bool Interrupted => UserActed && Mode == TakeoverMode.Stop;

    /// <summary>True when the user acted and the mode is warn: the result must carry the warning.</summary>
    public bool Warns => UserActed && Mode == TakeoverMode.Warn;

    /// <summary>
    /// True once the monitor said detection did not work for some of this watch (<see cref="MarkUnwatched"/>): its hooks
    /// were not installed, a hook failed, or the detection thread stopped or could not be started, so the user's input may
    /// have gone unseen and the result must say so. Set on the watch itself, not read from the monitor's status, so an
    /// operation is never blamed for a failure that came before it, and one that came during it is never missed.
    /// </summary>
    public bool Unwatched => Volatile.Read(ref _unwatched) != null;

    /// <summary>Why <see cref="Unwatched"/>: the first reason the monitor gave; null while the watch works.</summary>
    public string? UnwatchedBecause => Volatile.Read(ref _unwatched);

    /// <summary>
    /// Records that detection did not work for some of this watch, and why (<paramref name="why"/>); the first reason is
    /// kept. Mode off ignores it: nothing was to be watched.
    /// </summary>
    public void MarkUnwatched(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (Mode != TakeoverMode.Off)
        {
            Interlocked.CompareExchange(ref _unwatched, why, null);
        }
    }

    /// <summary>Records that the user acted; with mode stop cancels <see cref="Token"/>. Mode off ignores it.</summary>
    public void Signal()
    {
        if (Mode == TakeoverMode.Off || Interlocked.Exchange(ref _acted, 1) != 0)
        {
            return;
        }

        try
        {
            _stop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation ended meanwhile
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ended?.Invoke(this);
        _stop?.Dispose();
    }
}
