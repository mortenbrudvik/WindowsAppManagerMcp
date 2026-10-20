using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// get_server_info's report (roadmap M2.11): the build, the process's integrity and DPI awareness, the desktop state,
/// the monitors with their DPI, the host the server recorded, the pause, the policy, the paths and the toolsets, and a
/// warning for each thing that will make tools fail or coordinates wrong. Every source is a mock or a fake.
/// </summary>
public sealed class ServerInfoServiceTests : IDisposable
{
    private static readonly ServerConnectionInfo Connection = new("2025-11-25", "claude-code", "2.1.0");

    private readonly Mock<INativeSystemInfoWrapper> _system = new();
    private readonly Mock<IProcessSecurityProbe> _probe = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private readonly Mock<IInputSafetyInfo> _safety = new();
    private readonly Mock<IPauseHotkey> _hotkey = new();
    private readonly Mock<ILayoutService> _layouts = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IAuditLog> _audit = new();
    private readonly InputSession _session = new(new FakeInputWrapper());

    public ServerInfoServiceTests()
    {
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitorV2);
        _system.Setup(s => s.GetForegroundLockTimeout()).Returns(200_000);
        _system.Setup(s => s.GetSessionId()).Returns(1);
        _probe.Setup(p => p.ServerIntegrity).Returns(new ProcessIntegrity(4242, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
        _probe.Setup(p => p.IsInputDesktopAvailable()).Returns(true);
        _probe.Setup(p => p.IsSessionLocked()).Returns(false);
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.25),
            new MonitorInfo(1, @"\\.\DISPLAY2", false, new MonitorRect(-1920, 0, 1920, 1080), new MonitorRect(-1920, 0, 1920, 1040), 1.0)
        ]);
        var host = new HostSnapshot(
            4242,
            [new HostProcess(3100, null), new HostProcess(2900, null)],
            ConsoleWindow: 0x1001,
            ConsoleHostWindow: new HostWindow(0x2002, new HostProcess(2800, null)),
            StartupForegroundWindow: new HostWindow(0x2002, new HostProcess(2800, null)),
            ShellProcessId: 1500)
        {
            ConsoleServerProcesses = [new HostProcess(3300, null)]
        };
        _safety.Setup(s => s.Read()).Returns(new InputSafetySnapshot(host, TargetPolicySettings.Default, ["%{F4}", "^w"], "default"));
        _hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, "default"));
        _layouts.Setup(l => l.LayoutsPath).Returns(@"C:\Users\user\AppData\Local\WindowsAppManagerMcp\layouts");
        _audit.Setup(a => a.Folder).Returns(@"C:\Users\user\AppData\Local\WindowsAppManagerMcp\audit");
        _windows.Setup(w => w.GetProcessName(It.IsAny<uint>())).Returns((uint id) => id switch
        {
            4242 => "WindowsAppManagerMcp",
            3100 => "node",
            2900 => "pwsh",
            2800 => "WindowsTerminal",
            3300 => "OpenConsole",
            _ => null
        });
    }

    public void Dispose() => _session.Dispose();

    private ServerInfoService CreateSut(ToolsetSelection? toolsets = null) => new(
        _system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object, _layouts.Object, _windows.Object,
        toolsets, _audit.Object);

    [Fact]
    public void Read_ListsTheWindowsTheServerPinned_ThatAreStillTopmost()
    {
        // Arrange (roadmap M6.7, owner decision D14): one pin still topmost, one unpinned by its app, one closed, and one whose
        // handle another thread's window got
        const long Topmost = 0x8;
        var pins = new TopmostPins();
        pins.Pin(0x100, 11);
        pins.Pin(0x200, 22);
        pins.Pin(0x300, 33);
        pins.Pin(0x400, 44);
        foreach (var (window, thread) in new (nint, uint)[] { (0x100, 11), (0x200, 22), (0x400, 45) })
        {
            _windows.Setup(w => w.IsWindow(window)).Returns(true);
            _windows.Setup(w => w.GetWindowThreadId(window)).Returns(thread);
            _windows.Setup(w => w.GetWindowLongPtr(window, -20)).Returns(new nint(window == 0x200 ? 0 : Topmost));
        }

        _windows.Setup(w => w.GetWindowText(0x100)).Returns("Reference - Notepad");
        _windows.Setup(w => w.GetWindowThreadProcessId(0x100)).Returns(600u);
        _windows.Setup(w => w.GetProcessName(600u)).Returns("notepad");
        var sut = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, topmostPins: pins);

        // Act
        var info = sut.Read(Connection, 48);

        // Assert - the others are forgotten
        info.PinnedWindows.Should().Equal(new PinnedWindowInfo(0x100, "Reference - Notepad", "notepad"));
        pins.Windows.Select(p => p.Window).Should().Equal((nint)0x100);
    }

    [Fact]
    public void Read_LeavesPinnedWindowsOut_WhenThereAreNone()
    {
        CreateSut().Read(Connection, 48).PinnedWindows.Should().BeNull();
    }

    [Fact]
    public void Read_ReportsTheBuildAndTheConnection()
    {
        var info = CreateSut().Read(Connection, servedTools: 48);

        info.Server.Name.Should().Be("windows-app-manager");
        info.Server.Version.Should().Be(ServerVersion.Current);
        info.Server.GitSha.Should().Be(ServerVersion.GitSha);
        info.Server.SdkVersion.Should().Be(ServerVersion.SdkVersion);
        info.Server.Runtime.Should().StartWith(".NET");
        info.Server.Os.Should().Contain("Windows");
        info.Connection.Should().Be(Connection);
    }

    [Fact]
    public void Read_ReportsTheProcessAndTheDesktop()
    {
        var info = CreateSut().Read(Connection, 48);

        info.Process.Should().Be(new ServerProcessInfo(Environment.ProcessId, 1, IntegrityLevel.Medium, false, DpiAwareness.PerMonitorV2));
        info.Desktop.Should().Be(new DesktopStateInfo(InputDesktopAvailable: true, SessionLocked: false, ForegroundLockTimeoutMs: 200_000));
    }

    [Fact]
    public void Read_ReportsTheMonitorsWithTheirDpi()
    {
        var info = CreateSut().Read(Connection, 48);

        info.Monitors.Should().Equal(
            new ServerMonitorInfo(0, @"\\.\DISPLAY1", true, new BoundsDto(0, 0, 2560, 1440), 120, 1.25),
            new ServerMonitorInfo(1, @"\\.\DISPLAY2", false, new BoundsDto(-1920, 0, 1920, 1080), 96, 1.0));
    }

    [Fact]
    public void Read_AMonitorWhoseDpiWindowsCouldNotReport_HasNoDpiOrScale_AsInGetMonitors()
    {
        // Arrange - the native wrapper's scale of 1 for the arithmetic, which was never read, and a DPI Windows reported as is
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.0)
            {
                Dpi = 96, DpiUnknown = true
            },
            new MonitorInfo(1, @"\\.\DISPLAY2", false, new MonitorRect(-1920, 0, 1920, 1080), new MonitorRect(-1920, 0, 1920, 1040), 1.5)
            {
                Dpi = 144
            }
        ]);

        // Act
        var monitors = CreateSut().Read(Connection, 48).Monitors;

        // Assert
        monitors.Should().Equal(
            new ServerMonitorInfo(0, @"\\.\DISPLAY1", true, new BoundsDto(0, 0, 2560, 1440), Dpi: null, ScaleFactor: null),
            new ServerMonitorInfo(1, @"\\.\DISPLAY2", false, new BoundsDto(-1920, 0, 1920, 1080), 144, 1.5));
        JsonSerializer.Serialize(monitors[0], ToolJson.SerializerOptions).Should().NotContain("dpi").And.NotContain("scaleFactor");
    }

    [Fact]
    public void Read_ReportsTheHostTheServerRecorded()
    {
        var info = CreateSut().Read(Connection, 48);

        info.Host.Windows.Should().Equal(
            new AgentHostWindow(0x2002, "console", 2800, "WindowsTerminal"),
            new AgentHostWindow(0x2002, "startupForeground", 2800, "WindowsTerminal"));
        info.Host.Processes.Should().Equal(
            new AgentHostProcess(4242, "server", "WindowsAppManagerMcp"),
            new AgentHostProcess(3100, "ancestor", "node"),
            new AgentHostProcess(2900, "ancestor", "pwsh"),
            new AgentHostProcess(3300, "consoleServer", "OpenConsole"));
    }

    [Fact]
    public void Read_AHostWhoseAncestorsCouldNotBeRead_IsAWarning()
    {
        // Arrange - review F3: the host guard could not read the server's ancestors at start
        var host = new HostSnapshot(4242, [], 0x1001, new HostWindow(0x2002, new HostProcess(2800, null)), null, 1500)
        {
            AncestorsUnknown = true
        };
        _safety.Setup(s => s.Read()).Returns(new InputSafetySnapshot(host, TargetPolicySettings.Default, ["%{F4}", "^w"], "default"));

        // Act
        var info = CreateSut().Read(Connection, 48);

        // Assert
        info.Warnings.Should().ContainSingle().Which.Should().Contain("ancestor processes").And.Contain("started before the server").And.Contain("Tell the user")
            // Review A4: the effects that are no refusal, so an agent that sees many windows marked or left out knows why
            .And.Contain("isAgentHost").And.Contain("popups").And.Contain("desktop search");
        info.Host.Processes.Should().Equal(new AgentHostProcess(4242, "server", "WindowsAppManagerMcp"));
    }

    [Fact]
    public void Read_ReportsThePauseThePolicyAndThePaths()
    {
        var info = CreateSut().Read(Connection, 48);

        info.Input.Paused.Should().BeFalse();
        info.Input.Hotkey.State.Should().Be(PauseHotkeyState.Registered);
        info.Safety.DeniedProcesses.Should().Equal(TargetPolicySettings.Default.DeniedProcesses);
        info.Safety.DenyListSource.Should().Be("default");
        info.Safety.AllowedProcesses.Should().BeNull();
        info.Safety.DestructiveChords.Should().Equal("%{F4}", "^w");
        info.Paths.Layouts.Should().Be(@"C:\Users\user\AppData\Local\WindowsAppManagerMcp\layouts");
        info.Paths.AuditLog.Should().Be(@"C:\Users\user\AppData\Local\WindowsAppManagerMcp\audit");
    }

    [Fact]
    public void Read_ReportsTheClipboardAndProtocolSettings_AndWarnsAboutWhatWasIgnored()
    {
        // Arrange - a mistyped WAM_ALLOW_USER_CLIPBOARD, and a WAM_ALLOWED_PROTOCOLS with one usable and two ignored entries
        var clipboard = ClipboardOptions.FromEnvironment(_ => "ture");
        var protocols = ProtocolAllowlist.Parse("vscode, javascript, 1bad");

        // Act
        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, clipboard: clipboard, protocols: protocols).Read(Connection, 48);

        // Assert
        info.Safety.UserClipboardAllowed.Should().BeFalse();
        info.Safety.AllowedProtocols.Should().Equal("http", "https", "mailto", "tel", "ms-settings", "vscode");
        info.Warnings.Should().Contain(w => w.Contains("WAM_ALLOW_USER_CLIPBOARD") && w.Contains("'ture'"));
        info.Warnings.Should().Contain(w => w.Contains("WAM_ALLOWED_PROTOCOLS") && w.Contains("'javascript'") && w.Contains("'1bad'"));
    }

    [Fact]
    public void Read_WithTheUsersClipboardAllowed_SaysSo_WithoutAWarning()
    {
        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, clipboard: new ClipboardOptions(true), protocols: ProtocolAllowlist.BuiltIn).Read(Connection, 48);

        info.Safety.UserClipboardAllowed.Should().BeTrue();
        info.Safety.AllowedProtocols.Should().Equal(ProtocolAllowlist.BuiltInSchemes);
        info.Warnings.Should().BeNull();
    }

    [Fact]
    public void Read_ReportsTheUiAutomationEngine_WithItsTimeouts()
    {
        // Act - the default engine (roadmap M3.2)
        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, uiaEngine: UiaEngineOptions.Default).Read(Connection, 48);

        // Assert
        info.UiAutomation.Should().Be(new UiAutomationInfo(UiaEngine.Uia3, "default", 1000, 5000));
        info.Warnings.Should().BeNull();
    }

    [Fact]
    public void Read_ReportsTheManagedEngine_WithoutTimeouts()
    {
        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, uiaEngine: new UiaEngineOptions(UiaEngine.Uia2, UiaEngineOptions.EnvironmentVariable))
            .Read(Connection, 48);

        info.UiAutomation.Should().Be(new UiAutomationInfo(UiaEngine.Uia2, "WAM_UIA_ENGINE", null, null));
    }

    [Fact]
    public void Read_WarnsAboutAnEngineSettingItIgnored()
    {
        var options = UiaEngineOptions.FromEnvironment(_ => "managed");

        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object, uiaEngine: options).Read(Connection, 48);

        info.UiAutomation!.Engine.Should().Be(UiaEngine.Uia3);
        info.Warnings.Should().ContainSingle().Which.Should().Contain("WAM_UIA_ENGINE").And.Contain("'managed'");
    }

    [Fact]
    public void Read_WithoutAnEngineSetting_ReportsTheDefault()
    {
        CreateSut().Read(Connection, 48).UiAutomation!.Engine.Should().Be(UiaEngine.Uia3);
    }

    [Fact]
    public void Read_WithoutAnAuditLog_LeavesItsPathOut()
    {
        var info = new ServerInfoService(_system.Object, _probe.Object, _monitors.Object, _safety.Object, _session, _hotkey.Object,
            _layouts.Object, _windows.Object).Read(Connection, 48);

        info.Paths.AuditLog.Should().BeNull();
    }

    [Fact]
    public void Read_WarnsWhenTheAuditLogCannotBeWritten()
    {
        _audit.Setup(a => a.LastError).Returns(@"Access to the path 'C:\Users\user\AppData\Local\WindowsAppManagerMcp\audit' is denied.");

        var warning = CreateSut().Read(Connection, 48).Warnings.Should().ContainSingle().Subject;

        warning.Should().Contain("audit log").And.Contain("is denied").And.Contain("not recorded").And.Contain("Tell the user");
    }

    [Fact]
    public void Read_ReportsTheToolsets()
    {
        var selection = ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => "-browser",
                ToolsetSelection.ExcludeToolsVariable => "kill_process",
                _ => null
            },
            Toolsets.Server);

        var info = CreateSut(selection).Read(Connection, servedTools: 43);

        info.Toolsets.Enabled.Should().NotContain("browser").And.Contain(Toolsets.ServerToolset);
        // browser-actions and browser-script are off by default (roadmap M5.5, M5.8), so "-browser" leaves every browser toolset
        // off; windows-batch is off by default too (roadmap M6.1), and so are visual (roadmap M6.4), ocr (roadmap M6.3) and
        // workspaces (roadmap M7.5)
        info.Toolsets.Disabled.Should().Equal("workspaces", "browser", "browser-actions", "browser-script", "visual", "windows-batch", "ocr");
        info.Toolsets.Source.Should().Be(ToolsetSelection.ToolsetsVariable);
        info.Toolsets.ExcludedTools.Should().Equal("kill_process");
        info.Toolsets.ReadOnly.Should().BeFalse();
        info.Toolsets.ServedTools.Should().Be(43);
        info.Toolsets.Moved.Should().BeEquivalentTo(selection.Moved);
    }

    [Fact]
    public void Read_SaysWhereTheToolsThatMovedOutOfTheDefaultWent()
    {
        // Roadmap M6.1: an agent that looks for snap_windows_batch finds it in the windows-batch toolset
        var info = CreateSut(ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server)).Read(Connection, 66);

        info.Toolsets.Moved.Should().ContainSingle()
            .Which.Should().Contain("windows-batch toolset").And.Contain("snap_windows_batch").And.NotContain("deprecated");
        info.Warnings.Should().BeNull("an opt-in toolset that is off is nothing to warn about");
        CreateSut().Read(Connection, 78).Toolsets.Moved.Should().BeNull("every tool is served");
    }

    [Fact]
    public void Read_WithoutASelection_ReportsEveryToolset()
    {
        CreateSut().Read(Connection, 48).Toolsets.Source.Should().Be(ToolsetSelection.AllKeyword);
    }

    [Fact]
    public void Read_AHealthyServer_HasNoWarnings()
    {
        CreateSut().Read(Connection, 48).Warnings.Should().BeNull();
    }

    [Fact]
    public void Read_PerMonitorV1Awareness_IsNoWarning()
    {
        // The manifest's fallback before Windows 10 1703 gives physical pixels too: no false alarm for the user
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitor);

        CreateSut().Read(Connection, 48).Warnings.Should().BeNull();
    }

    [Fact]
    public void Read_APauseTheServerSetItself_IsWarnedAsTheServers_NotTheUsers()
    {
        // Arrange - the server could not read the pause it shares with the other servers, so it paused itself (review F4)
        _session.Pause(InputControlSources.SharedPauseUnreadable);

        // Act
        var warnings = CreateSut().Read(Connection, 48).Warnings;

        // Assert - the same PausedByUser and the same advice, but the user did not pause, so it does not say they did
        warnings.Should().ContainSingle(w => w.Contains("PausedByUser")).Which.Should()
            .Contain("could not read the pause it shares").And.Contain("Stop and ask the user").And.NotContain("by the user");
    }

    [Fact]
    public void Read_WarnsAboutWhatWillMakeToolsFail()
    {
        // Arrange - everything at once
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.System);
        _probe.Setup(p => p.IsSessionLocked()).Returns(true);
        _probe.Setup(p => p.IsInputDesktopAvailable()).Returns(false);
        _probe.Setup(p => p.ServerIntegrity).Returns(new ProcessIntegrity(4242, IntegrityLevel.Low, ProcessIntegrity.LowRid, false, null));
        _hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Failed, "default", "the chord is in use"));
        _session.Pause(InputControlSources.Hotkey);
        var toolsets = ToolsetSelection.FromEnvironment(name => name == ToolsetSelection.ToolsetsVariable ? "widnows" : null, Toolsets.Server);

        // Act
        var warnings = CreateSut(toolsets).Read(Connection, 48).Warnings;

        // Assert
        warnings.Should().NotBeNull();
        warnings.Should().Contain(w => w.Contains("System") && w.Contains("PerMonitorV2") && w.Contains("scaled"));
        warnings.Should().Contain(w => w.Contains("locked"));
        warnings.Should().NotContain(w => w.Contains("secure desktop"), "a locked session explains the unavailable input desktop");
        warnings.Should().Contain(w => w.Contains("Low") && w.Contains("integrity"));
        warnings.Should().Contain(w => w.Contains("PausedByUser"));
        warnings.Should().Contain(w => w.Contains("pause hotkey") && w.Contains("the chord is in use"));
        warnings.Should().Contain(w => w.Contains("'widnows'"));
    }

    [Fact]
    public void Read_ALockedSession_WarnsThatInputIsRefused_NotThatCapturesFail()
    {
        // Issue #18 review: no capture path checks the lock, so a capture while locked is made and may show the lock screen
        _probe.Setup(p => p.IsSessionLocked()).Returns(true);

        var warning = CreateSut().Read(Connection, 48).Warnings.Should().ContainSingle().Which;

        warning.Should().Contain("SessionLocked").And.Contain("Captures are not refused").And.NotContain("captures fail");
    }

    [Fact]
    public void Read_AnUnreachableInputDesktop_IsAWarning()
    {
        _probe.Setup(p => p.IsInputDesktopAvailable()).Returns(false);

        CreateSut().Read(Connection, 48).Warnings.Should().ContainSingle().Which.Should().Contain("secure desktop");
    }

    [Fact]
    public void Read_AHotkeyOfAnotherServerThatCannotShareItsPause_IsAWarning()
    {
        _hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default"));

        CreateSut().Read(Connection, 48).Warnings.Should().ContainSingle().Which.Should().Contain("does not pause this server");
    }
}
