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

    // The display configuration's targets for one topology (roadmap M6.7). QueryDisplayConfig is slower than
    // EnumDisplayMonitors, so it is read again only when the topology changes, not every snapshot. Immutable once
    // published, like the snapshot.
    private DisplayIdentity? _identity;

    public MonitorService()
        : this(new NativeMonitorWrapper())
    {
    }

    public MonitorService(INativeMonitorWrapper nativeMonitors, TimeProvider? timeProvider = null)
    {
        _nativeMonitors = nativeMonitors;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed record DisplayIdentity(string TopologyKey, IReadOnlyList<DisplayTarget> Targets);

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
    /// Adds each monitor's identity (id, friendly name, internal or not) from the display configuration. The targets are
    /// kept for the topology they were read for, so a window listing's per-window lookups and the snapshot refreshes in
    /// between never read the configuration again; a failed read is not kept, so the next snapshot tries again.
    /// </summary>
    private ImmutableArray<(nint Handle, MonitorInfo Info)> Identify(IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors)
    {
        if (monitors.Count == 0)
        {
            return [];
        }

        var targets = GetDisplayTargets(monitors);
        return targets is null
            ? monitors.ToImmutableArray()
            : monitors.Select(m => (m.Handle, MonitorIdentity.Identify(m.Info, targets))).ToImmutableArray();
    }

    private IReadOnlyList<DisplayTarget>? GetDisplayTargets(IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors)
    {
        var key = TopologyKey(monitors);
        var cached = Volatile.Read(ref _identity);
        if (cached?.TopologyKey == key)
        {
            return cached.Targets;
        }

        var targets = _nativeMonitors.GetDisplayTargets();
        // A failed read, one that misses a monitor, or one whose target name failed for a monitor is used for this
        // snapshot but not kept, so the next snapshot reads again
        if (targets is not null && MonitorIdentity.IsComplete(monitors.Select(m => m.Info), targets))
        {
            Volatile.Write(ref _identity, new DisplayIdentity(key, targets.ToImmutableArray()));
        }

        return targets;
    }

    /// <summary>
    /// What identifies a topology: each monitor's handle, GDI device name and bounds, in enumeration order. Windows hands
    /// out new monitor handles when the display configuration changes, and docking renumbers the device names or moves
    /// the bounds. The work area is left out: moving the taskbar changes it but not which monitor is which.
    /// </summary>
    private static string TopologyKey(IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors) =>
        string.Join(';', monitors.Select(m => FormattableString.Invariant(
            $"{m.Handle}|{m.Info.DeviceName}|{m.Info.Bounds.X},{m.Info.Bounds.Y},{m.Info.Bounds.Width},{m.Info.Bounds.Height}")));

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
