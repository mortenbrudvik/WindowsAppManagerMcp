using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Reads get_server_info's report from the services that own each part: the security probe (integrity, elevation,
/// desktop state), the system info wrapper (DPI awareness, foreground lock timeout, session), the monitors, the safety
/// settings and the host the server recorded at start, the input session and the pause hotkey, the layout storage, and
/// the toolset selection, the clipboard and URI scheme settings, and the audit log. Warnings name what will make tools
/// fail or coordinates wrong, a setting that was not understood, or (the audit log) what goes unrecorded.
/// </summary>
public sealed class ServerInfoService(
    INativeSystemInfoWrapper system,
    IProcessSecurityProbe probe,
    IMonitorService monitors,
    IInputSafetyInfo safety,
    IInputSession session,
    IPauseHotkey hotkey,
    ILayoutService layouts,
    INativeWindowWrapper windows,
    ToolsetSelection? toolsets = null,
    IAuditLog? auditLog = null,
    ClipboardOptions? clipboard = null,
    ProtocolAllowlist? protocols = null,
    UiaEngineOptions? uiaEngine = null,
    TopmostPins? topmostPins = null) : IServerInfoService
{
    public ServerInfoResult Read(ServerConnectionInfo connection, int servedTools, IReadOnlyList<string>? resources = null,
        IReadOnlyList<string>? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var warnings = new List<string>();
        var process = ReadProcess(warnings);
        var desktop = ReadDesktop(warnings);
        var input = ReadInput(warnings);
        var settings = safety.Read();
        var selection = toolsets ?? ToolsetSelection.All();
        warnings.AddRange(selection.Warnings);
        var clipboardSetting = clipboard ?? ClipboardOptions.Default;
        var protocolSetting = protocols ?? ProtocolAllowlist.BuiltIn;
        if (clipboardSetting.Warning is { } clipboardWarning)
            warnings.Add(clipboardWarning);
        var engine = uiaEngine ?? UiaEngineOptions.Default;
        if (engine.Warning is { } engineWarning)
            warnings.Add(engineWarning);
        if (protocolSetting.IgnoredWarning is { } protocolWarning)
            warnings.Add(protocolWarning);

        if (auditLog?.LastError is { } auditError)
        {
            warnings.Add($"The audit log in '{auditLog.Folder}' cannot be written ({auditError}): input and destructive actions are " +
                         "not recorded. Tell the user.");
        }

        return new ServerInfoResult(
            new ServerBuildInfo(
                ServerIdentity.Name,
                ServerVersion.Current,
                ServerVersion.GitSha,
                ServerVersion.SdkVersion,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription),
            connection,
            process,
            desktop,
            // As get_monitors reports them: the DPI Windows reported, and neither it nor the scale when it could not report one
            monitors.GetAllMonitors()
                .Select(m => new ServerMonitorInfo(
                    m.Index,
                    m.DeviceName,
                    m.IsPrimary,
                    new BoundsDto(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height),
                    m.DpiUnknown ? null : m.Dpi,
                    m.DpiUnknown ? null : m.ScaleFactor))
                .ToList(),
            ReadHost(settings.Host, warnings),
            input,
            new SafetySettingsInfo(
                settings.Policy.DeniedProcesses,
                settings.Policy.DenyListSource,
                settings.Policy.AllowedProcesses,
                settings.DestructiveChords,
                settings.DestructiveChordSource,
                clipboardSetting.AllowUserClipboard,
                protocolSetting.AllowedSchemes.ToList()),
            new ServerPathsInfo(layouts.LayoutsPath, auditLog?.Folder),
            new ToolsetInfo(selection.Enabled, selection.Disabled, selection.Source, selection.ReadOnly, selection.ExcludedTools, servedTools,
                selection.Moved, resources is { Count: > 0 } ? resources : null, prompts is { Count: > 0 } ? prompts : null),
            warnings.Count == 0 ? null : warnings,
            new UiAutomationInfo(
                engine.Engine,
                engine.Source,
                engine.Engine == UiaEngine.Uia3 ? (int)Native.Uia3Tree.ConnectionTimeoutMs : null,
                engine.Engine == UiaEngine.Uia3 ? (int)Native.Uia3Tree.TransactionTimeoutMs : null),
            ReadPinnedWindows());
    }

    /// <summary>
    /// The windows set_window_zorder made topmost that still are (roadmap M6.7): a pin whose window closed, whose handle a
    /// window of another thread got, or that is no longer topmost (unpinned by its app or the user) is forgotten. Null when
    /// none is left.
    /// </summary>
    private List<PinnedWindowInfo>? ReadPinnedWindows()
    {
        if (topmostPins == null)
            return null;

        var pinned = new List<PinnedWindowInfo>();
        foreach (var pin in topmostPins.Windows.OrderBy(p => p.Window))
        {
            var window = pin.Window;
            var stillPinned = windows.IsWindow(window) && windows.GetWindowThreadId(window) == pin.ThreadId &&
                              ((long)windows.GetWindowLongPtr(window, Native.NativeEnums.GWL_EXSTYLE) & Native.NativeEnums.WS_EX_TOPMOST) != 0;
            if (!stillPinned)
            {
                topmostPins.Unpin(window);
                continue;
            }

            pinned.Add(new PinnedWindowInfo(window.ToInt64(), AppText.Cut(windows.GetWindowText(window)),
                windows.GetProcessName(windows.GetWindowThreadProcessId(window))));
        }

        return pinned.Count == 0 ? null : pinned;
    }

    private ServerProcessInfo ReadProcess(List<string> warnings)
    {
        var integrity = probe.ServerIntegrity;
        var awareness = system.GetDpiAwareness();
        if (!DpiAwarenessNames.GivesPhysicalPixels(awareness))
        {
            warnings.Add($"The DPI awareness in force is {awareness}, not the PerMonitorV2 the server's manifest declares: window, " +
                         "element and click coordinates can be scaled on monitors above 100%. Tell the user.");
        }

        if (integrity.Level is IntegrityLevel.Low or IntegrityLevel.Untrusted)
        {
            warnings.Add($"The server runs at {integrity.Level} integrity: Windows drops its input to ordinary windows (TargetElevated). " +
                         "Ask the user to start it normally.");
        }

        return new ServerProcessInfo(Environment.ProcessId, system.GetSessionId(), integrity.Level, integrity.IsElevated, awareness);
    }

    private DesktopStateInfo ReadDesktop(List<string> warnings)
    {
        var locked = probe.IsSessionLocked();
        var available = probe.IsInputDesktopAvailable();
        if (locked)
        {
            // No capture path checks the lock: a capture is made, and may show the lock screen
            warnings.Add("The session is locked: input tools return SessionLocked until the user unlocks it. Captures are not " +
                         "refused, and may show the lock screen.");
        }
        else if (!available)
        {
            warnings.Add("The input desktop cannot be reached (a UAC prompt, the secure desktop of Ctrl+Alt+Del, or a disconnected " +
                         "session): input fails (DesktopUnavailable) until it is back. Wait for the user.");
        }

        return new DesktopStateInfo(available, locked, system.GetForegroundLockTimeout());
    }

    private InputControlInfo ReadInput(List<string> warnings)
    {
        var state = session.PauseState;
        var status = hotkey.Status;
        var shared = session.SharesDesktopPause;
        if (state.Paused)
        {
            // The server's own pause, when the shared one could not be read, is not the user's: the agent must not say it is
            var pausedBy = state.PausedBy == InputControlSources.SharedPauseUnreadable
                ? $"because {InputControlSources.PauseCause(state.PausedBy, null)}"
                : "by the user";
            warnings.Add($"Input is paused {pausedBy}: input tools and every tool that changes windows or starts or stops processes " +
                         "return PausedByUser. Stop and ask the user; never try to resume input yourself.");
        }

        if (status.State == PauseHotkeyState.Failed)
        {
            warnings.Add($"The pause hotkey {status.Chord} is not registered ({status.Error ?? "Windows refused it"}): the user cannot " +
                         "pause the server's input with it. Tell the user.");
        }
        else if (status.State == PauseHotkeyState.OtherServer && !shared)
        {
            warnings.Add($"Another Windows App Manager server has registered the pause hotkey {status.Chord}, and this server could not " +
                         "share its pause, so the hotkey does not pause this server. Tell the user.");
        }

        return new InputControlInfo(state.Paused, state.PausedBy, state.PausedAt, status, shared);
    }

    private AgentHostInfo ReadHost(HostSnapshot host, List<string> warnings)
    {
        if (host.AncestorsUnknown)
        {
            // Review F3: the list below then has no ancestors because they are unknown, not because there are none. The host
            // guard reads them again (reading the host above did), so this warns only while they still cannot be read
            warnings.Add("The server cannot read its ancestor processes (the process list cannot be read), so it does not know the " +
                         "terminal or editor that started it by its process. Until it can (it tries again at most once a second), " +
                         "every window of a program that started before the server, except the desktop shell's, counts as the " +
                         "agent's host: input to it and run_steps on it are refused (TargetIsAgentHost), and so are closing it " +
                         "without force and placing it after a launch (ProtectedWindow); kill_process and close_debug_browser " +
                         "refuse (ProcessTreeUnreadable). The window lists mark such windows isAgentHost, and the popup lists " +
                         "leave out their popups and a desktop search their windows, as for the host. Tell the user.");
        }

        var hostWindows = new List<AgentHostWindow>();
        AddWindow(host.ConsoleHostWindow, "console");
        AddWindow(host.StartupForegroundWindow, "startupForeground");

        var processes = new List<AgentHostProcess> { new(host.ServerProcessId, "server", NameOf(host.ServerProcessId)) };
        processes.AddRange(host.Ancestors.Select(p => new AgentHostProcess(p.ProcessId, "ancestor", NameOf(p.ProcessId))));
        processes.AddRange(host.ConsoleServerProcesses.Select(p => new AgentHostProcess(p.ProcessId, "consoleServer", NameOf(p.ProcessId))));
        return new AgentHostInfo(hostWindows, processes);

        void AddWindow(HostWindow? window, string role)
        {
            if (window != null)
            {
                hostWindows.Add(new AgentHostWindow(window.Handle, role, window.Process.ProcessId, NameOf(window.Process.ProcessId)));
            }
        }
    }

    private string? NameOf(int processId) => processId > 0 ? windows.GetProcessName((uint)processId) : null;
}
