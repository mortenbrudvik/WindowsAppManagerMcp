using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>What the services behind the tools report in a contract run.</summary>
public enum ContractMode
{
    /// <summary>Every call succeeds with the data of a <see cref="TypicalDesktop"/>.</summary>
    Typical,

    /// <summary>Every call fails the way the services report failures: invalid handles, errors, empty lists.</summary>
    Failing
}

/// <summary>
/// The services the tool classes take, as mocks and fakes for one <see cref="ContractMode"/>, registered in the
/// DI container of an in-process MCP server. Nothing native is called: input goes to a fake input wrapper, the
/// pause hotkey is a stub, and the capture tools encode blank pixels from a mocked capture wrapper (the real
/// ScreenshotService, so the image size of a typical capture is real).
/// </summary>
public sealed class ContractServices : IDisposable
{
    public ContractServices(ContractMode mode)
    {
        Mode = mode;
        Session = new InputSession(new FakeInputWrapper());
        Hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, PauseHotkeyOptions.DefaultSource));
        SetUpServerInfoSources();

        if (mode == ContractMode.Typical)
        {
            SetUpTypical();
        }
        else
        {
            SetUpFailing();
        }
    }

    public ContractMode Mode { get; }

    public Mock<IWindowService> Windows { get; } = new();
    public Mock<IForegroundService> Foreground { get; } = new();
    public Mock<IMonitorService> Monitors { get; } = new();
    public Mock<IProcessService> Processes { get; } = new();
    public Mock<IBrowserReadingService> Browser { get; } = new();
    public Mock<ILayoutService> Layouts { get; } = new();
    public Mock<IUIElementService> UiElements { get; } = new();
    public Mock<IElementInspectionService> Inspection { get; } = new();
    public Mock<IInputSimulationService> Input { get; } = new();
    public Mock<IClipboardService> Clipboard { get; } = new();
    public Mock<IPauseHotkey> Hotkey { get; } = new();
    public Mock<INativeSystemInfoWrapper> SystemInfo { get; } = new();
    public Mock<IProcessSecurityProbe> SecurityProbe { get; } = new();
    public Mock<IInputSafetyInfo> Safety { get; } = new();
    public Mock<INativeWindowWrapper> NativeWindows { get; } = new();
    public Mock<IWindowCloseVerifier> CloseVerifier { get; } = new();
    public InputSession Session { get; }
    public IScreenshotService Screenshots { get; private set; } = null!;

    /// <summary>Registers the services, as configureServices of <see cref="InProcessMcpServer.StartAsync"/>.</summary>
    public void Register(IServiceCollection services)
    {
        // The SDK creates the tool classes for every call from these registrations
        services.AddSingleton(Windows.Object);
        services.AddSingleton(CloseVerifier.Object);
        services.AddSingleton(Foreground.Object);
        services.AddSingleton(Monitors.Object);
        services.AddSingleton(Processes.Object);
        services.AddSingleton(Browser.Object);
        services.AddSingleton(Layouts.Object);
        services.AddSingleton(UiElements.Object);
        services.AddSingleton(Inspection.Object);
        services.AddSingleton(Input.Object);
        services.AddSingleton(Clipboard.Object);
        services.AddSingleton(Hotkey.Object);
        services.AddSingleton<IInputSession>(Session);
        services.AddSingleton(Screenshots);
        services.AddSingleton<ITargetPolicy>(new TargetPolicy(TargetPolicySettings.Default));
        // get_server_info: the real report over mocked sources, with the toolsets the server was registered with
        services.AddSingleton<IServerInfoService>(sp => new ServerInfoService(
            SystemInfo.Object, SecurityProbe.Object, Monitors.Object, Safety.Object, Session, Hotkey.Object, Layouts.Object,
            NativeWindows.Object, sp.GetService<ToolsetSelection>()));
    }

    public void Dispose() => Session.Dispose();

    /// <summary>
    /// The sources of get_server_info: a medium-integrity, per-monitor v2 server started from Windows Terminal (Typical),
    /// or one whose DPI awareness cannot be read on a locked session (Failing). The report itself never fails.
    /// </summary>
    private void SetUpServerInfoSources()
    {
        var typical = Mode == ContractMode.Typical;
        SystemInfo.Setup(s => s.GetDpiAwareness()).Returns(typical ? DpiAwareness.PerMonitorV2 : DpiAwareness.Unknown);
        SystemInfo.Setup(s => s.GetForegroundLockTimeout()).Returns(typical ? 200_000 : null);
        SystemInfo.Setup(s => s.GetSessionId()).Returns(1);
        SecurityProbe.Setup(p => p.ServerIntegrity).Returns(new ProcessIntegrity(31_000, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
        SecurityProbe.Setup(p => p.IsInputDesktopAvailable()).Returns(typical);
        SecurityProbe.Setup(p => p.IsSessionLocked()).Returns(!typical);

        // claude.exe in pwsh in Windows Terminal, as the host guard records it
        var terminal = new HostWindow(0x30_0D1E, new HostProcess(30_100, null));
        var host = new HostSnapshot(31_000, [new HostProcess(30_900, null), new HostProcess(30_500, null), new HostProcess(30_100, null)],
            ConsoleWindow: 0x30_0A10, ConsoleHostWindow: terminal, StartupForegroundWindow: terminal, ShellProcessId: 5_120)
        {
            ConsoleServerProcesses = [new HostProcess(30_700, null)]
        };
        Safety.Setup(s => s.Read()).Returns(new InputSafetySnapshot(
            host, TargetPolicySettings.Default, InputGuardOptions.Default.DestructiveChords.Select(c => c.Spec).ToList(),
            InputGuardOptions.Default.DestructiveChordSource));
        NativeWindows.Setup(w => w.GetProcessName(It.IsAny<uint>())).Returns((uint id) => id switch
        {
            31_000 => "WindowsAppManagerMcp",
            30_900 => "claude",
            30_500 => "pwsh",
            30_100 => "WindowsTerminal",
            30_700 => "OpenConsole",
            _ => null
        });
        Layouts.Setup(l => l.LayoutsPath).Returns(@"C:\Users\user\AppData\Local\WindowsAppManagerMcp\layouts");
    }

    private void SetUpTypical()
    {
        var windows = TypicalDesktop.Windows;
        var notepad = TypicalDesktop.Notepad;

        Windows.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns(windows);
        Windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(windows);
        Windows.Setup(w => w.GetForegroundWindow()).Returns(notepad);
        Windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>()))
            .Returns((nint handle) => windows.FirstOrDefault(w => w.Handle == handle) ?? notepad);
        Windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        Windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(new TargetPolicyVerdict(true, notepad.ProcessName, null));
        Windows.Setup(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, int _, bool _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.ResizeWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((nint handle, int _, int _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, int _, int _, int _, bool _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, int _, int _, int _, bool _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()))
            .Returns((nint handle, WindowStateRequest _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns((nint handle, SnapPosition _, int? _, bool _) => WindowChangeResult.Done(handle));
        Windows.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, MonitorPositioning _, bool _) => WindowChangeResult.Done(handle));
        // A window that was sent WM_CLOSE leaves the Alt-Tab set, and the verifier reports it closed (roadmap M2.5)
        var closed = new System.Collections.Concurrent.ConcurrentDictionary<nint, bool>();
        Windows.Setup(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>())).Returns((nint handle, bool _) =>
        {
            closed[handle] = true;
            return new WindowCloseResult(handle, true);
        });
        Windows.Setup(w => w.GetApplicationWindows()).Returns(() => windows.Where(w => !closed.ContainsKey(w.Handle)).ToList());
        Windows.Setup(w => w.GetOwnedWindows((nint)TypicalDesktop.NotepadHandle, It.IsAny<bool>())).Returns(
        [
            new OwnedWindowInfo(notepad with
            {
                Handle = 0x1A0C00, Title = "Save As", ClassName = "#32770", OwnerHandle = (nint)TypicalDesktop.NotepadHandle,
                Bounds = new WindowRect(400, 200, 960, 640)
            }, IsDialog: true, IsBlocking: true)
        ]);
        CloseVerifier.Setup(v => v.ShownWindows(It.IsAny<IReadOnlyList<nint>>())).Returns((IReadOnlyList<nint> handles) => handles.ToHashSet());
        CloseVerifier.Setup(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<IReadOnlySet<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, IReadOnlySet<nint> _, TimeSpan _, CancellationToken _) =>
                handles.Select(h => new CloseObservation(h, CloseStatus.Closed)).ToList());
        CloseVerifier.Setup(v => v.WaitForProcessesToExitAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Windows.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) => handles.Select(h => new WindowCloseResult(h, true)).ToList());
        Windows.Setup(w => w.SetWindowBoundsBatchAsync(It.IsAny<IReadOnlyList<(nint, int, int, int, int, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, int, int, int, int, bool)> placements, int _, CancellationToken _) =>
                placements.Select(p => WindowChangeResult.Done(p.Handle)).ToList());
        Windows.Setup(w => w.SnapWindowsBatchAsync(It.IsAny<IReadOnlyList<(nint, SnapPosition, int?, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, SnapPosition, int?, bool)> placements, int _, CancellationToken _) =>
                placements.Select(p => WindowChangeResult.Done(p.Handle)).ToList());
        Windows.Setup(w => w.SetWindowStateBatchAsync(It.IsAny<IReadOnlyList<(nint, WindowStateRequest)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, WindowStateRequest)> changes, int _, CancellationToken _) =>
                changes.Select(c => WindowChangeResult.Done(c.Handle)).ToList());

        Foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) => new FocusOutcome
            {
                Target = handle,
                IsForeground = true,
                Method = FocusMethod.SetForeground,
                ForegroundHandle = handle,
                ForegroundTitle = notepad.Title,
                ForegroundProcess = notepad.ProcessName,
                ForegroundProcessId = notepad.ProcessId
            });

        Monitors.Setup(m => m.GetAllMonitors()).Returns(TypicalDesktop.Monitors);
        Monitors.Setup(m => m.GetPrimaryMonitor()).Returns(TypicalDesktop.Monitors[0]);
        Monitors.Setup(m => m.GetMonitorIndex(It.IsAny<nint>())).Returns(0);

        Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: TypicalDesktop.NotepadProcessId, WindowHandle: (nint)TypicalDesktop.NotepadHandle));
        // A launch that waited for its window found it: the launch tools return the whole window
        Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LaunchRequest _, bool wait, int _, CancellationToken _) => TypicalLaunch(wait, notepad, TypicalDesktop.NotepadProcessId));
        Processes.Setup(p => p.GetRunningProcesses(It.IsAny<string?>(), It.IsAny<bool>())).Returns(TypicalDesktop.Processes);
        Processes.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, bool _, bool _, int _, string? _, CancellationToken _) =>
                new KillResult(true, id, ProcessName: "notepad", CloseRequested: true, Exited: true));
        Processes.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LaunchRequest> apps, bool wait, int _, IProgress<OperationProgress>? _, CancellationToken _) =>
                apps.Select((_, i) => TypicalLaunch(wait, TypicalDesktop.Windows[i], 20_000 + i)).ToList());
        Processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 31_337, (nint)TypicalDesktop.BrowserHandle, 9222,
                @"C:\Users\user\AppData\Local\Temp\WindowsAppManagerMcp\browser-9222", null, null));

        var url = new BrowserUrlResult(true, "https://github.com/mortenbrudvik/WindowsAppManagerMcp", "chrome",
            "Windows App Manager - GitHub", null, BrowserErrorCode.None, Source: BrowserReadSource.Cdp, TargetId: "8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F");
        Browser.Setup(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(url);
        Browser.Setup(b => b.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(url);
        Browser.Setup(b => b.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(true, TypicalDesktop.Article, url.Url, "chrome", url.PageTitle, TypicalDesktop.ArticleLength,
                null, BrowserErrorCode.None, Source: BrowserReadSource.Cdp, TargetId: url.TargetId));

        var preset = TypicalDesktop.Layouts[0];
        Layouts.Setup(l => l.GetStorageError()).Returns((string?)null);
        Layouts.Setup(l => l.GetAllPresets()).Returns(TypicalDesktop.Layouts);
        Layouts.Setup(l => l.GetPreset(It.IsAny<string>())).Returns(preset);
        Layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, preset));
        Layouts.Setup(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(true, preset.Name, WindowsArranged: preset.Placements.Count, Focus: [TypicalDesktop.NotepadFocused],
                Placements: preset.Placements
                    .Select((placement, i) => new LayoutPlacementResult(placement.Order, LayoutPlacementStatus.Applied,
                        LayoutWindowRef.From(TypicalDesktop.Windows[i]), Focused: placement.Focus ? true : null))
                    .ToList()));
        Layouts.Setup(l => l.DeletePresetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => new LayoutDeleteResult(true, name));

        var tree = TypicalDesktop.UiTree();
        UiElements.Setup(u => u.IsAvailable()).Returns(true);
        UiElements.Setup(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),It.IsAny<IProgress<OperationProgress>?>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync((nint handle, int depth, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken _) => new UIElementResult(
                true, handle, notepad.Title, tree, TypicalDesktop.Count(tree), depth, 184.5, DepthReached: 3));

        Inspection.Setup(i => i.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(true, TypicalDesktop.FocusedEdit, TypicalDesktop.NotepadHandle, notepad.Title,
                notepad.ProcessId, notepad.ProcessName, 12.5));
        Inspection.Setup(i => i.GetElementAtPointAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int x, int y, int ancestors, TimeSpan _, CancellationToken _) => new ElementAtPointResult(
                true, x, y, TypicalDesktop.FocusedEdit,
                Enumerable.Range(1, ancestors).Select(i => new UIElementAncestor($"Pane {i}", "Pane", $"pane{i}", "Grid", new BoundsDto(0, 0, 1280, 720))).ToList(),
                TypicalDesktop.NotepadHandle, notepad.Title, notepad.ProcessId, notepad.ProcessName, 9.75));

        var receiving = TypicalDesktop.NotepadSummary;
        Input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => Clicked(x, y, "left", receiving));
        Input.Setup(i => i.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => Clicked(x, y, "right", receiving));
        Input.Setup(i => i.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, int _, InputTarget _, bool _, CancellationToken _) => Clicked(x, y, "double", receiving));
        Input.Setup(i => i.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new MouseMoveResult(true, x, y)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving, ActualX = x, ActualY = y
            });
        Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int _, InputTarget _, bool _, CancellationToken _) => new TypeTextResult(true, text, text.Length)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving
            });
        Input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(true, keys)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving
            });
        Input.Setup(i => i.Scroll(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int? x, int? y, int deltaY, int deltaX, InputTarget _, bool _, CancellationToken _) => new ScrollResult(true, x ?? 1280, y ?? 760, deltaY, deltaX)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving, ActualX = x ?? 1280, ActualY = y ?? 760
            });

        // The agent copied a paragraph from Notepad with send_keys '^c'
        Clipboard.Setup(c => c.ReadAsync(It.IsAny<ClipboardFormat>(), It.IsAny<int>(), It.IsAny<ClipboardConsent?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetClipboardResult(true, ClipboardFormat.Text, Text: TypicalDesktop.CopiedParagraph,
                Length: TypicalDesktop.CopiedParagraph.Length, Truncated: false, Origin: ClipboardContentOrigin.AgentInput));
        Clipboard.Setup(c => c.SetText(It.IsAny<string>()))
            .Returns((string text) => new SetClipboardResult(true, text.Length, ExcludedFromHistory: true));

        // The real capture pipeline (scaling, encoding, metadata) over blank pixels: a 2560 x 1440 monitor, a 1600 x 900 window
        var monitors = new Mock<IMonitorService>();
        monitors.Setup(m => m.GetAllMonitors()).Returns(TypicalDesktop.Monitors);
        Screenshots = new ScreenshotService(
            TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 1600, captureHeight: 900, windowOriginX: 0, windowOriginY: 100).Object,
            monitors.Object,
            Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true));
    }

    /// <summary>A launch that started a process and, when it waited, found the window.</summary>
    private static LaunchResult TypicalLaunch(bool waited, WindowInfo window, int processId) => waited
        ? new LaunchResult(true, ProcessId: processId, WindowHandle: window.Handle, Status: LaunchStatus.WindowObserved, Window: window, LauncherExited: false)
        : new LaunchResult(true, ProcessId: processId, Status: LaunchStatus.Started);

    private static ClickResult Clicked(int x, int y, string type, WindowSummary receiving) =>
        new(true, x, y, type) { ReceivingWindow = receiving, ForegroundAfter = receiving, ActualX = x, ActualY = y };

    private void SetUpFailing()
    {
        const string invalid = "Invalid window handle";

        Windows.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([]);
        Windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([]);
        Windows.Setup(w => w.GetForegroundWindow()).Returns((WindowInfo?)null);
        Windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((WindowInfo?)null);
        Windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(false);
        Windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(new TargetPolicyVerdict(true, null, null));
        Windows.Setup(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns((nint handle, bool _) => new WindowCloseResult(handle, false, WindowErrorCode.InvalidHandle, invalid));
        Windows.Setup(w => w.GetApplicationWindows()).Returns([]);
        Windows.Setup(w => w.GetOwnedWindows(It.IsAny<nint>(), It.IsAny<bool>())).Returns([]);
        Windows.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) =>
                handles.Select(h => new WindowCloseResult(h, false, WindowErrorCode.InvalidHandle, invalid)).ToList());
        Windows.Setup(w => w.SetWindowBoundsBatchAsync(It.IsAny<IReadOnlyList<(nint, int, int, int, int, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, int, int, int, int, bool)> placements, int _, CancellationToken _) =>
                placements.Select(p => WindowChangeResult.Failed(p.Handle, WindowErrorCode.InvalidHandle, invalid)).ToList());
        Windows.Setup(w => w.SnapWindowsBatchAsync(It.IsAny<IReadOnlyList<(nint, SnapPosition, int?, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, SnapPosition, int?, bool)> placements, int _, CancellationToken _) =>
                placements.Select(p => WindowChangeResult.Failed(p.Handle, WindowErrorCode.InvalidHandle, invalid)).ToList());
        Windows.Setup(w => w.SetWindowStateBatchAsync(It.IsAny<IReadOnlyList<(nint, WindowStateRequest)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, WindowStateRequest)> changes, int _, CancellationToken _) =>
                changes.Select(c => WindowChangeResult.Failed(c.Handle, WindowErrorCode.InvalidHandle, invalid)).ToList());

        Foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) => new FocusOutcome { Target = handle, ErrorCode = WindowErrorCode.InvalidHandle, Error = invalid });

        Monitors.Setup(m => m.GetAllMonitors()).Returns([]);
        Monitors.Setup(m => m.GetPrimaryMonitor()).Throws(new InvalidOperationException("No monitors found"));

        Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(false, ErrorMessage: "The executable was not found", ErrorCode: "ExecutableNotFound"));
        Processes.Setup(p => p.GetRunningProcesses(It.IsAny<string?>(), It.IsAny<bool>())).Returns([]);
        Processes.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, bool _, bool _, int _, string? _, CancellationToken _) =>
                new KillResult(false, id, ErrorMessage: "No process with that ID", ErrorCode: nameof(ProcessErrorCode.ProcessNotFound)));
        Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(false, ErrorMessage: "The executable was not found", ErrorCode: nameof(LaunchErrorCode.ExecutableNotFound)));
        Processes.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) =>
                apps.Select(_ => new LaunchResult(false, ErrorMessage: "The executable was not found", ErrorCode: nameof(LaunchErrorCode.ExecutableNotFound))).ToList());
        Processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(false, null, null, null, null, "Chrome is not installed", nameof(BrowserErrorCode.BrowserNotFound)));

        Browser.Setup(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "Not a browser window", BrowserErrorCode.NotABrowser));
        Browser.Setup(b => b.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "No browser in the foreground", BrowserErrorCode.NoBrowserInForeground));
        Browser.Setup(b => b.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(false, null, null, null, null, null, "Not a browser window", BrowserErrorCode.NotABrowser));

        Layouts.Setup(l => l.GetStorageError()).Returns("The layouts folder cannot be written");
        Layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(false, null, ErrorMessage: "The layouts folder cannot be written", ErrorCode: nameof(LayoutErrorCode.SaveFailed)));
        Layouts.Setup(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, LayoutMatchBy _, bool _, IProgress<OperationProgress>? _, CancellationToken _) =>
                new LayoutApplyResult(false, name, Errors: [$"Layout preset '{name}' was not found"], ErrorCode: nameof(LayoutErrorCode.PresetNotFound)));
        Layouts.Setup(l => l.DeletePresetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) =>
                new LayoutDeleteResult(false, name, $"Layout preset '{name}' was not found", nameof(LayoutErrorCode.PresetNotFound)));

        UiElements.Setup(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),It.IsAny<IProgress<OperationProgress>?>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync((nint handle, int _, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken _) =>
                new UIElementResult(false, handle, null, null, 0, 0, 1.5, invalid, UIElementErrorCode.InvalidHandle));
        Inspection.Setup(i => i.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(false, null, null, null, null, null, 5000, "UI Automation did not answer", UIElementErrorCode.OperationTimedOut));
        Inspection.Setup(i => i.GetElementAtPointAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int x, int y, int _, TimeSpan _, CancellationToken _) => new ElementAtPointResult(
                false, x, y, null, null, null, null, null, null, 0.5, "The point is outside every monitor", UIElementErrorCode.CoordinatesOutOfBounds));

        const string host = "The input would go to the agent's own terminal";
        Input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "left", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "right", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, int _, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "double", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new MouseMoveResult(false, x, y, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int _, InputTarget _, bool _, CancellationToken _) => new TypeTextResult(false, text, 0, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(false, keys, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.Scroll(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int? x, int? y, int deltaY, int deltaX, InputTarget _, bool _, CancellationToken _) =>
                new ScrollResult(false, x ?? 0, y ?? 0, deltaY, deltaX, host, InputErrorCode.TargetIsAgentHost));

        // The user copied something, and the test client cannot ask the user
        Clipboard.Setup(c => c.ReadAsync(It.IsAny<ClipboardFormat>(), It.IsAny<int>(), It.IsAny<ClipboardConsent?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClipboardFormat format, int _, ClipboardConsent? _, CancellationToken _) => new GetClipboardResult(false, format,
                Error: "The clipboard holds content the agent did not put there", ErrorCode: ClipboardErrorCode.ClipboardNotFromAgent));
        Clipboard.Setup(c => c.SetText(It.IsAny<string>()))
            .Returns(new SetClipboardResult(false, 0, Error: "Another application kept the clipboard open", ErrorCode: ClipboardErrorCode.ClipboardBusy));

        var screenshots = new Mock<IScreenshotService>();
        var failed = TestDataFactory.CreateScreenCapture(TestDataFactory.CreateScreenshotResult(
            success: false, error: "The capture failed", errorCode: nameof(ScreenshotErrorCode.CaptureFailed)));
        screenshots.Setup(s => s.ListScreens()).Returns(new ScreenListResult([], 0, new VirtualScreenBounds(0, 0, 0, 0)));
        screenshots.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>())).Returns(failed);
        screenshots.Setup(s => s.CaptureRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CaptureOptions>())).Returns(failed);
        screenshots.Setup(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>())).Returns(failed);
        Screenshots = screenshots.Object;
    }
}

/// <summary>
/// Calls tools through an in-process MCP server with the production tool and filter registrations
/// (<see cref="ServiceRegistration.AddWindowsAppManagerTools"/>) and the <see cref="ContractServices"/> of a mode.
/// Each call gets its own server and services; a call's result is kept for the rest of the test run, so the
/// contract and budget tests share one call per tool and mode. The cache is keyed by mode and tool only: every
/// caller passes the tool's one set of arguments (EveryToolContractTests.Arguments) and the same arrangement.
/// </summary>
public static class ContractRun
{
    private static readonly ConcurrentDictionary<(ContractMode Mode, string Tool), Lazy<Task<CallToolResult>>> Results = new();

    /// <summary>
    /// The result of calling <paramref name="tool"/> with <paramref name="arguments"/>. <paramref name="arrange"/>
    /// changes the services first, for a call that needs more than the mode's defaults.
    /// </summary>
    public static Task<CallToolResult> CallAsync(
        ContractMode mode,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        Action<ContractServices>? arrange = null) =>
        Results.GetOrAdd((mode, tool), _ => new Lazy<Task<CallToolResult>>(() => RunAsync(mode, tool, arguments, arrange))).Value;

    private static async Task<CallToolResult> RunAsync(
        ContractMode mode,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        Action<ContractServices>? arrange)
    {
        using var services = new ContractServices(mode);
        arrange?.Invoke(services);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services.Register);
        return await server.CallToolAsync(tool, arguments);
    }
}
