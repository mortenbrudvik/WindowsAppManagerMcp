using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// <see cref="IAppWindowProbe"/> through Win32 reads (EnumWindows, IsWindowVisible, GetWindowThreadProcessId, the title,
/// class, styles, rectangle, owner and process name), which Windows answers without the windows' threads, so a hung
/// application cannot block it.
/// </summary>
public sealed class AppWindowProbe : IAppWindowProbe
{
    /// <summary>At most this many windows are described as opened.</summary>
    public const int MaxOpenedWindows = 8;

    /// <summary>WS_CAPTION (WS_BORDER | WS_DLGFRAME): a title bar.</summary>
    private const long WsCaption = 0x00C00000;

    /// <summary>WS_DISABLED: the window takes no input (a modal dialog disables the window that owns it).</summary>
    private const long WsDisabled = 0x08000000;

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
    public AppWindowsOpened OpenedSince(AppWindowSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var dialogs = new List<OpenedWindow>();
        var others = new List<OpenedWindow>();
        foreach (var (handle, processId) in VisibleWindows(before.ProcessIds))
        {
            if (before.Windows.Contains(handle))
            {
                continue;
            }

            var className = Safe(() => _windows.GetClassName(handle)) is { Length: > 0 } name ? name : null;
            if (IsTransient(handle, className))
            {
                continue;
            }

            var window = new OpenedWindow(
                handle,
                Safe(() => _windows.GetWindowText(handle)) ?? string.Empty,
                className,
                processId,
                Safe(() => _windows.GetProcessName((uint)processId)));
            (CanBeDialog(handle, className) ? dialogs : others).Add(window);
            if (dialogs.Count + others.Count == MaxOpenedWindows)
            {
                break;
            }
        }

        return new AppWindowsOpened(dialogs, others);
    }

    /// <summary>
    /// A window no action opens for the agent: a tooltip (shown by any thread of the application, also while the thread the
    /// action waits for is busy), a window without a size, or one that is disabled (it takes no input) or cloaked (not drawn).
    /// A read that fails leaves the window in.
    /// </summary>
    private bool IsTransient(nint handle, string? className)
    {
        if (PopupService.IsTooltipClass(className))
        {
            return true;
        }

        try
        {
            return _windows.IsCloaked(handle) ||
                   ((long)_windows.GetWindowLongPtr(handle, NativeEnums.GWL_STYLE) & WsDisabled) != 0 ||
                   _windows.GetWindowRect(handle) is { } rect && (rect.Right <= rect.Left || rect.Bottom <= rect.Top);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the window can be the dialog an action waits for (a Win32 Invoke returns only when the modal dialog it opened
    /// closes): a dialog box (#32770), a window with a title bar that is no tool window, or a window that an owner it
    /// disabled holds, as a modal dialog does. Not a menu, a dropdown or a hover card.
    /// </summary>
    private bool CanBeDialog(nint handle, string? className)
    {
        if (WindowClassification.IsDialogClass(className))
        {
            return true;
        }

        try
        {
            var style = (long)_windows.GetWindowLongPtr(handle, NativeEnums.GWL_STYLE);
            if ((style & WsCaption) == WsCaption && !WindowClassification.IsToolWindow(_windows.GetWindowLongPtr(handle, NativeEnums.GWL_EXSTYLE)))
            {
                return true;
            }

            return WindowOwnerChain.SelfAndOwners(_windows, handle).Skip(1)
                .Any(owner => ((long)_windows.GetWindowLongPtr(owner, NativeEnums.GWL_STYLE) & WsDisabled) != 0);
        }
        catch (Exception)
        {
            return false;
        }
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
