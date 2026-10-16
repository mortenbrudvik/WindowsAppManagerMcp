using System.Collections.Immutable;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class MonitorService : IMonitorService
{
    /// <summary>
    /// How long a monitor snapshot is reused before the monitors are enumerated again.
    /// Window listings look up the monitor of every window, so this avoids one
    /// EnumDisplayMonitors pass per window while keeping display changes visible quickly.
    /// </summary>
    public static TimeSpan SnapshotLifetime { get; } = TimeSpan.FromMilliseconds(250);

    private readonly INativeMonitorWrapper _nativeMonitors;
    private readonly TimeProvider _timeProvider;

    // Immutable once published. Tool calls can run concurrently, so a refresh builds a new
    // snapshot and swaps the reference; readers never see a list that is being filled.
    private MonitorSnapshot? _snapshot;

    public MonitorService()
        : this(new NativeMonitorWrapper())
    {
    }

    public MonitorService(INativeMonitorWrapper nativeMonitors, TimeProvider? timeProvider = null)
    {
        _nativeMonitors = nativeMonitors;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed record MonitorSnapshot(ImmutableArray<(nint Handle, MonitorInfo Info)> Monitors, long Timestamp)
    {
        public ImmutableArray<MonitorInfo> Infos { get; } = Monitors.Select(m => m.Info).ToImmutableArray();

        public MonitorInfo? FindByHandle(nint hMonitor)
        {
            foreach (var monitor in Monitors)
            {
                if (monitor.Handle == hMonitor)
                {
                    return monitor.Info;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns the current snapshot, enumerating the monitors again if it is missing, stale or empty.
    /// </summary>
    private MonitorSnapshot GetSnapshot() => GetSnapshot(out _);

    private MonitorSnapshot GetSnapshot(out bool refreshed)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot != null &&
            !snapshot.Monitors.IsEmpty &&
            _timeProvider.GetElapsedTime(snapshot.Timestamp) < SnapshotLifetime)
        {
            refreshed = false;
            return snapshot;
        }

        refreshed = true;
        return RefreshSnapshot();
    }

    private MonitorSnapshot RefreshSnapshot()
    {
        // Take the timestamp first so the snapshot never looks newer than the data in it
        var timestamp = _timeProvider.GetTimestamp();
        var snapshot = new MonitorSnapshot(Identify(_nativeMonitors.EnumerateMonitors()), timestamp);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Adds each monitor's identity (id, friendly name, internal or not) from the display configuration, read again at every
    /// snapshot refresh. The refreshes are at most one per <see cref="SnapshotLifetime"/> and a window listing's per-window
    /// lookups use the snapshot, so the read is not per window. It is not kept across refreshes: in clone mode a projector can
    /// be added, removed or swapped on a source whose monitor handle, device name and bounds all stay the same, and only the
    /// display configuration shows it. A failed or partial read leaves the identity of the monitors it misses unknown for
    /// that snapshot.
    /// </summary>
    private ImmutableArray<(nint Handle, MonitorInfo Info)> Identify(IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors)
    {
        if (monitors.Count == 0)
        {
            return [];
        }

        var targets = _nativeMonitors.GetDisplayTargets();
        return targets is null
            ? monitors.ToImmutableArray()
            : monitors.Select(m => (m.Handle, MonitorIdentity.Identify(m.Info, targets))).ToImmutableArray();
    }

    /// <summary>
    /// Finds the monitor with the handle the native lookup returns. If a cached snapshot does not
    /// contain that handle, the display configuration changed after the snapshot was taken, so
    /// enumerate the monitors again and look the handle up once more.
    /// </summary>
    private MonitorInfo? FindMonitor(Func<nint> getMonitorHandle)
    {
        var snapshot = GetSnapshot(out var refreshed);
        var hMonitor = getMonitorHandle();
        var monitor = snapshot.FindByHandle(hMonitor);
        if (monitor != null || refreshed || hMonitor == nint.Zero)
        {
            return monitor;
        }

        return RefreshSnapshot().FindByHandle(getMonitorHandle());
    }

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        return GetSnapshot().Infos;
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        return GetPrimaryMonitor(GetSnapshot());
    }

    private static MonitorInfo GetPrimaryMonitor(MonitorSnapshot snapshot)
    {
        return snapshot.Infos.FirstOrDefault(m => m.IsPrimary)
            ?? snapshot.Infos.FirstOrDefault()
            ?? throw new InvalidOperationException("No monitors found");
    }

    public MonitorInfo? GetMonitorAt(int x, int y)
    {
        return FindMonitor(() => _nativeMonitors.MonitorFromPoint(x, y));
    }

    public MonitorInfo? GetMonitorForWindow(nint windowHandle)
    {
        return FindMonitor(() => _nativeMonitors.MonitorFromWindow(windowHandle));
    }

    public int GetMonitorIndex(nint windowHandle)
    {
        var monitor = GetMonitorForWindow(windowHandle);
        return monitor?.Index ?? 0;
    }

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="monitorIndex"/> is not a monitor, or <paramref name="position"/> is not a snap position. Never
    /// falls back to the primary monitor (roadmap M2.2): the window would land on another monitor than the one asked for.
    /// </exception>
    public MonitorRect CalculateSnapBounds(int monitorIndex, SnapPosition position)
    {
        var snapshot = GetSnapshot();
        if (monitorIndex < 0 || monitorIndex >= snapshot.Infos.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorIndex), monitorIndex,
                $"Monitor index {monitorIndex} does not exist; there are {snapshot.Infos.Length} monitors.");
        }

        // The visible frame the window gets; the parts of a split tile the work area exactly (SnapMath)
        return SnapMath.Calculate(snapshot.Infos[monitorIndex].WorkArea, position);
    }
}
