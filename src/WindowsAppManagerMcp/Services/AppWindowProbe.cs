using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// <see cref="IAppWindowProbe"/> through Win32 reads (EnumWindows, IsWindowVisible, GetWindowThreadProcessId, the title,
/// class and process name), which Windows answers without the windows' threads, so a hung application cannot block it.
/// </summary>
public sealed class AppWindowProbe : IAppWindowProbe
{
    /// <summary>At most this many windows are described as opened.</summary>
    public const int MaxOpenedWindows = 8;

    private readonly INativeWindowWrapper _windows;

    public AppWindowProbe(INativeWindowWrapper windows)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    /// <inheritdoc />
    public AppWindowSnapshot Snapshot(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        var processes = processIds.Where(id => id > 0).ToHashSet();
        return new AppWindowSnapshot(processes, VisibleWindows(processes).Select(w => w.Handle).ToHashSet());
    }

    /// <inheritdoc />
    public IReadOnlyList<OpenedWindow> OpenedSince(AppWindowSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var opened = new List<OpenedWindow>();
        foreach (var (handle, processId) in VisibleWindows(before.ProcessIds))
        {
            if (before.Windows.Contains(handle))
            {
                continue;
            }

            opened.Add(new OpenedWindow(
                handle,
                Safe(() => _windows.GetWindowText(handle)) ?? string.Empty,
                Safe(() => _windows.GetClassName(handle)) is { Length: > 0 } className ? className : null,
                processId,
                Safe(() => _windows.GetProcessName((uint)processId))));
            if (opened.Count == MaxOpenedWindows)
            {
                break;
            }
        }

        return opened;
    }

    /// <summary>The visible top-level windows of the processes, in z-order (EnumWindows' order: topmost first).</summary>
    private List<(nint Handle, int ProcessId)> VisibleWindows(IReadOnlySet<int> processIds)
    {
        var found = new List<(nint, int)>();
        if (processIds.Count == 0)
        {
            return found;
        }

        try
        {
            _windows.EnumWindows(handle =>
            {
                var processId = (int)_windows.GetWindowThreadProcessId(handle);
                if (processIds.Contains(processId) && _windows.IsWindowVisible(handle))
                {
                    found.Add((handle, processId));
                }

                return true;
            });
        }
        catch (Exception)
        {
            // Best effort: openedPopup is a hint, never a reason to fail the action
        }

        return found;
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
