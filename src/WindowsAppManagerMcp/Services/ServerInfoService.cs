using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Reads get_server_info's report from the services that own each part: the security probe (integrity, elevation,
/// desktop state), the system info wrapper (DPI awareness, foreground lock timeout, session), the monitors, the safety
/// settings and the host the server recorded at start, the input session and the pause hotkey, the layout storage, and
/// the toolset selection. Warnings name what will make tools fail or coordinates wrong.
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
    ToolsetSelection? toolsets = null) : IServerInfoService
{
    /// <summary>The MCP server name (serverInfo.name).</summary>
    public const string ServerName = "windows-app-manager";

    public ServerInfoResult Read(ServerConnectionInfo connection, int servedTools)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var warnings = new List<string>();
        var process = ReadProcess(warnings);
        var desktop = ReadDesktop(warnings);
        var input = ReadInput(warnings);
        var settings = safety.Read();
        var selection = toolsets ?? ToolsetSelection.All();
        warnings.AddRange(selection.Warnings);

        return new ServerInfoResult(
            new ServerBuildInfo(
                ServerName,
                ServerVersion.Current,
                ServerVersion.GitSha,
                ServerVersion.SdkVersion,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription),
            connection,
            process,
            desktop,
            monitors.GetAllMonitors()
                .Select(m => new ServerMonitorInfo(
                    m.Index,
                    m.DeviceName,
                    m.IsPrimary,
                    new BoundsDto(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height),
                    (int)Math.Round(m.ScaleFactor * 96),
                    m.ScaleFactor))
                .ToList(),
            ReadHost(settings.Host),
            input,
            new SafetySettingsInfo(
                settings.Policy.DeniedProcesses,
                settings.Policy.DenyListSource,
                settings.Policy.AllowedProcesses,
                settings.DestructiveChords,
                settings.DestructiveChordSource),
            new ServerPathsInfo(layouts.LayoutsPath),
            new ToolsetInfo(selection.Enabled, selection.Disabled, selection.Source, selection.ReadOnly, selection.ExcludedTools, servedTools),
            warnings.Count == 0 ? null : warnings);
    }

    private ServerProcessInfo ReadProcess(List<string> warnings)
    {
        var integrity = probe.ServerIntegrity;
        var awareness = system.GetDpiAwareness();
        if (!DpiAwarenessNames.IsExpected(awareness))
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
            warnings.Add("The session is locked: input and captures fail (SessionLocked) until the user unlocks it.");
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
            warnings.Add("Input is paused by the user: input tools and every tool that changes windows or starts or stops processes " +
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

    private AgentHostInfo ReadHost(HostSnapshot host)
    {
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
