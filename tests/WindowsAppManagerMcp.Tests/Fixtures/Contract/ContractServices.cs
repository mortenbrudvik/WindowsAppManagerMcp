using System.Collections.Concurrent;
using System.Text.Json;
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
        ElementRegistry.Setup(r => r.Lookup(It.IsAny<string>()))
            .Returns((string elementRef) => new ElementLookup(elementRef, (nint)TypicalDesktop.NotepadHandle, null, 1));
        Session = new InputSession(InputWrapper);
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

    /// <summary>The tab tools (roadmap M5.5): three tabs of a debug Chrome on port 9222, the first shown by the browser window.</summary>
    public Mock<IBrowserTabService> BrowserTabs { get; } = new();
    public Mock<ILayoutService> Layouts { get; } = new();
    public Mock<IUIElementService> UiElements { get; } = new();
    public Mock<IElementInspectionService> Inspection { get; } = new();
    public Mock<IElementSearchService> ElementSearch { get; } = new();
    public Mock<IElementReadService> ElementReads { get; } = new();

    /// <summary>The popups of a window (roadmap M3.5): a context menu and a Save As dialog of Notepad.</summary>
    public Mock<IPopupService> Popups { get; } = new();

    /// <summary>element_action's service (roadmap M3.6).</summary>
    public Mock<IElementActionService> ElementActions { get; } = new();

    /// <summary>element_action's menu_path (roadmap M3.5).</summary>
    public Mock<IMenuPathService> MenuPaths { get; } = new();

    /// <summary>respond_to_dialog's service (roadmap M3.5): Notepad's save prompt.</summary>
    public Mock<IDialogService> Dialogs { get; } = new();

    /// <summary>file_dialog's service (roadmap M3.14): Notepad's Save As dialog.</summary>
    public Mock<IFileDialogService> FileDialogs { get; } = new();

    /// <summary>The element registry (roadmap M3.4): every ref belongs to Notepad's window unless a test arranges otherwise.</summary>
    public Mock<IElementRegistry> ElementRegistry { get; } = new();
    public Mock<IInputSimulationService> Input { get; } = new();
    public Mock<IClipboardService> Clipboard { get; } = new();
    public Mock<IPauseHotkey> Hotkey { get; } = new();
    public Mock<INativeSystemInfoWrapper> SystemInfo { get; } = new();
    public Mock<IProcessSecurityProbe> SecurityProbe { get; } = new();
    public Mock<IInputSafetyInfo> Safety { get; } = new();
    public Mock<INativeWindowWrapper> NativeWindows { get; } = new();
    public Mock<IWindowCloseVerifier> CloseVerifier { get; } = new();
    public Mock<IWindowWaitService> WindowWait { get; } = new();
    public Mock<IElementWaitService> ElementWait { get; } = new();
    public Mock<ILaunchPlacementService> Placement { get; } = new();
    public InputSession Session { get; }

    /// <summary>The fake input layer under <see cref="Session"/>: the cursor, the keys that are down and Caps Lock (get_desktop_state).</summary>
    public FakeInputWrapper InputWrapper { get; } = new();

    public IScreenshotService Screenshots { get; private set; } = null!;

    /// <summary>The capture registry the typical captures register in and the mouse tools' image points resolve through (roadmap M3.11).</summary>
    public CaptureRegistry Captures { get; } = new();

    /// <summary>The window the typical captures are of: 1600 x 900 at (0, 100), as the capture wrapper reports it.</summary>
    private readonly Mock<INativeWindowWrapper> _capturedWindows = new();

    /// <summary>The audit log's folder (roadmap M2.17), in the temporary folder; created by the first line written.</summary>
    public string AuditFolder { get; } = Path.Combine(Path.GetTempPath(), "WamContractAudit", Guid.NewGuid().ToString("N"), "audit");

    /// <summary>The audit log, writing to <see cref="AuditFolder"/>, reading windows from <see cref="NativeWindows"/>.</summary>
    public IAuditLog Audit => _audit ??= new AuditLog(AuditLogOptions.ForFolder(AuditFolder), NativeWindows.Object, TimeProvider.System);

    private IAuditLog? _audit;

    /// <summary>Every line the audit log wrote, in order, from all its day files.</summary>
    public IReadOnlyList<JsonElement> AuditLines() =>
        !Directory.Exists(AuditFolder)
            ? []
            : Directory.GetFiles(AuditFolder, "*.jsonl").Order(StringComparer.Ordinal)
                .SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();

    /// <summary>Puts a file where <see cref="AuditFolder"/> should be, so no line can be written.</summary>
    public void BlockAuditFolder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditFolder)!);
        File.WriteAllText(AuditFolder, "in the way");
    }

    /// <summary>Registers the services, as configureServices of <see cref="InProcessMcpServer.StartAsync"/>.</summary>
    public void Register(IServiceCollection services)
    {
        // The SDK creates the tool classes for every call from these registrations
        services.AddSingleton(Windows.Object);
        services.AddSingleton(CloseVerifier.Object);
        services.AddSingleton(WindowWait.Object);
        services.AddSingleton(ElementWait.Object);
        services.AddSingleton(Placement.Object);
        services.AddSingleton(Foreground.Object);
        services.AddSingleton(Monitors.Object);
        services.AddSingleton(Processes.Object);
        services.AddSingleton(Browser.Object);
        services.AddSingleton(BrowserTabs.Object);
        services.AddSingleton(Layouts.Object);
        services.AddSingleton(UiElements.Object);
        services.AddSingleton(Inspection.Object);
        services.AddSingleton(ElementSearch.Object);
        services.AddSingleton(Popups.Object);
        services.AddSingleton(ElementReads.Object);
        services.AddSingleton(ElementActions.Object);
        services.AddSingleton(MenuPaths.Object);
        services.AddSingleton(Dialogs.Object);
        services.AddSingleton(FileDialogs.Object);
        services.AddSingleton(ElementRegistry.Object);
        services.AddSingleton(Input.Object);
        services.AddSingleton(Clipboard.Object);
        services.AddSingleton(Hotkey.Object);
        services.AddSingleton<IInputSession>(Session);
        services.AddSingleton(Screenshots);
        services.AddSingleton<ICaptureRegistry>(Captures);
        services.AddSingleton<ICoordinateSpaceResolver>(new CoordinateSpaceResolver(
            Captures, _capturedWindows.Object, Mock.Of<INativeHitTestWrapper>(),
            TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 1600, captureHeight: 900, windowOriginX: 0, windowOriginY: 100).Object));
        services.AddSingleton<ITargetPolicy>(new TargetPolicy(TargetPolicySettings.Default));
        services.AddSingleton(Audit);
        // get_server_info: the real report over mocked sources, with the toolsets the server was registered with
        services.AddSingleton<IServerInfoService>(sp => new ServerInfoService(
            SystemInfo.Object, SecurityProbe.Object, Monitors.Object, Safety.Object, Session, Hotkey.Object, Layouts.Object,
            NativeWindows.Object, sp.GetService<ToolsetSelection>(), Audit));
        // get_desktop_state and the action tools' observe (roadmap M3.12, M3.F32): the real observation over the mocks above
        services.AddSingleton<IDesktopStateService>(sp => new DesktopStateService(
            Windows.Object, Session, InputWrapper, Inspection.Object, UiElements.Object, Popups.Object, Screenshots,
            sp.GetRequiredService<ITargetPolicy>(), sp.GetService<ToolsetSelection>()));
    }

    public void Dispose()
    {
        Session.Dispose();
        var root = Path.GetDirectoryName(AuditFolder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            _ when typical => TypicalDesktop.Windows.FirstOrDefault(w => w.ProcessId == id)?.ProcessName,
            _ => null
        });

        // The windows the audit log reads before a call: the typical desktop's, or none
        NativeWindows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => typical && TypicalDesktop.Windows.Any(w => w.Handle == h));
        NativeWindows.Setup(w => w.GetWindowText(It.IsAny<nint>()))
            .Returns((nint h) => TypicalDesktop.Windows.FirstOrDefault(w => w.Handle == h)?.Title ?? string.Empty);
        NativeWindows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns((nint h) => (uint)(TypicalDesktop.Windows.FirstOrDefault(w => w.Handle == h)?.ProcessId ?? 0));
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
        // A window selector names Notepad (roadmap M2.4); get_window_info reads it in full
        Windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>())).Returns(WindowResolution.Found(notepad));
        Windows.Setup(w => w.GetWindowDetails(It.IsAny<nint>())).Returns((nint handle) => new WindowDetails(
            windows.FirstOrDefault(w => w.Handle == handle) ?? notepad, IsForeground: true, ZOrder: 0, IsTopmost: false, IsEnabled: true,
            IsResponding: true, Dpi: 120));
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
        // wait_for_window (roadmap M3.7): Notepad's Save As dialog opened and stayed
        WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowConditionResult
            {
                Satisfied = true,
                ElapsedMs = 640,
                MatchCount = 1,
                Window = notepad with
                {
                    Handle = 0x1A0C00, Title = "Save As", ClassName = "#32770", OwnerHandle = (nint)TypicalDesktop.NotepadHandle,
                    Bounds = new WindowRect(400, 200, 960, 640), VisibleBounds = new WindowRect(407, 200, 946, 633)
                }
            });
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
        Processes.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, bool _, bool _, int _, string? _, KillSafeguards? _, CancellationToken _) =>
                new KillResult(true, id, ProcessName: "notepad", CloseRequested: true, Exited: true));
        Processes.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LaunchRequest> apps, bool wait, int _, IProgress<OperationProgress>? _, CancellationToken _) =>
                apps.Select((_, i) => TypicalLaunch(wait, TypicalDesktop.Windows[i], 20_000 + i)).ToList());
        Processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 31_337, (nint)TypicalDesktop.BrowserHandle, 9222,
                @"C:\Users\user\AppData\Local\Temp\WindowsAppManagerMcp\browser-9222", null, null,
                LaunchStatus: LaunchStatus.WindowObserved, CdpReady: true, BrowserVersion: "Chrome/141.0.7390.55",
                CdpEndpoint: "http://127.0.0.1:9222",
                BrowserWsEndpoint: "ws://127.0.0.1:9222/devtools/browser/6f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f"));

        var url = new BrowserUrlResult(true, "https://github.com/mortenbrudvik/WindowsAppManagerMcp", "chrome",
            "Windows App Manager - GitHub", null, BrowserErrorCode.None, Source: BrowserReadSource.Cdp, TargetId: "8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F");
        Browser.Setup(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(url);
        Browser.Setup(b => b.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(url);
        Browser.Setup(b => b.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(true, TypicalDesktop.Article, url.Url, "chrome", url.PageTitle, TypicalDesktop.ArticleLength,
                null, BrowserErrorCode.None, Source: BrowserReadSource.Cdp, TargetId: url.TargetId,
                Offset: 0, TotalLength: TypicalDesktop.ArticleLength, Truncated: false));

        BrowserTabs.Setup(b => b.ListTabsAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserTabsResult(true,
            [
                new BrowserTabInfo(url.TargetId!, url.PageTitle!, url.Url!, 1, true),
                new BrowserTabInfo("2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B", "Pull requests - GitHub", "https://github.com/pulls", 1, true),
                new BrowserTabInfo("5C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F", "Example Domain", "https://example.com/", 2, false)
            ], TabCount: 3, ShownTargetId: url.TargetId, WindowBound: true, CdpPort: 9222));
        BrowserTabs.Setup(b => b.ActivateTabAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserTabResult(true, "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B", "Pull requests - GitHub", "https://github.com/pulls", 9222));
        BrowserTabs.Setup(b => b.NavigateAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserNavigateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserNavigateResult(true, url.TargetId, "https://example.com/docs/", "Docs - Example", 200, "load", 412,
                CdpPort: 9222));
        BrowserTabs.Setup(b => b.WaitAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserWaitCondition>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserWaitResult(true, true, false, 630, url.TargetId, "https://example.com/docs/", "Docs - Example", 9222));

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

        // get_desktop_state (roadmap M3.12): the pointer over Notepad's editor
        InputWrapper.MovePointer(640, 480);

        var tree = TypicalDesktop.UiTree();
        UiElements.Setup(u => u.IsAvailable()).Returns(true);
        UiElements.Setup(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),It.IsAny<IProgress<OperationProgress>?>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync((nint handle, int depth, UIElementFilter? filter, IProgress<OperationProgress>? _, CancellationToken _) =>
                filter?.MaxElements is { } max && max < TypicalDesktop.Count(tree)
                    ? new UIElementResult(true, handle, notepad.Title, TypicalDesktop.Take(tree, max), max, depth, 184.5, DepthReached: 3,
                        Truncated: true, TruncationReason: UIElementTruncationReason.MaxElements)
                    : new UIElementResult(true, handle, notepad.Title, tree, TypicalDesktop.Count(tree), depth, 184.5, DepthReached: 3));

        Inspection.Setup(i => i.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(true, TypicalDesktop.FocusedEdit, TypicalDesktop.NotepadHandle, notepad.Title,
                notepad.ProcessId, notepad.ProcessName, 12.5));
        ElementSearch.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementQuery query, CancellationToken _) => new FindElementsResult(
                true, query.WindowHandle, notepad.Title, MatchCount: 2, Truncated: false, TypicalDesktop.FoundButtons, 21.5,
                Scope: ElementSearchScope.Window));
        Popups.Setup(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((nint window, TimeSpan _, CancellationToken _, TimeSpan? _) => new ActivePopupsResult(
                true, window == 0 ? TypicalDesktop.NotepadHandle : (long)window, notepad.Title, IsForeground: true,
                TypicalDesktop.NotepadPopups, 4.25));
        ElementReads.Setup(r => r.ReadTextAsync(It.IsAny<ElementTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TypicalDesktop.MessageText);
        ElementReads.Setup(r => r.ReadTableAsync(It.IsAny<ElementTableRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TypicalDesktop.ProcessTable);
        // wait_for_element (roadmap M3.8): the Save button enabled after 3 checks
        ElementWait.Setup(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitForElementResult(true, true, false, ElapsedMs: 480, Polls: 3, WindowHandle: TypicalDesktop.NotepadHandle,
                Element: TypicalDesktop.FoundButtons[0], MatchCount: 1));
        ElementActions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => TypicalDesktop.ElementActionDone(request));
        // menu_path, respond_to_dialog and file_dialog (roadmap M3.5, M3.14): File>Save As, the save prompt, the Save As dialog
        MenuPaths.Setup(m => m.RunAsync(It.IsAny<MenuPathRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TypicalDesktop.MenuPathDone);
        Dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TypicalDesktop.SavePromptAnswered);
        FileDialogs.Setup(d => d.CompleteAsync(It.IsAny<FileDialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileDialogRequest request, CancellationToken _) => TypicalDesktop.FileSaved(request));
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
        Input.Setup(i => i.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => Clicked(x, y, "double", receiving));
        Input.Setup(i => i.MouseClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<MouseModifier>>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, MouseButton button, int count, IReadOnlyList<MouseModifier> modifiers, InputTarget _, bool _, CancellationToken _) =>
                Clicked(x, y, ToolEnum<MouseButton>.Name(button), receiving) with
                {
                    ClickCount = count,
                    Modifiers = modifiers.Count == 0 ? null : modifiers.Select(ToolEnum<MouseModifier>.Name).ToList()
                });
        Input.Setup(i => i.DragAsync(It.IsAny<DragRequest>(), It.IsAny<InputTarget>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DragRequest r, InputTarget _, InputTarget _, bool _, CancellationToken _) => new DragResult(true, r.FromX, r.FromY, r.ToX, r.ToY)
            {
                Button = ToolEnum<MouseButton>.Name(r.Button),
                Pressed = true,
                ReceivingWindow = receiving, DropWindow = receiving, ForegroundAfter = receiving, ActualX = r.ToX, ActualY = r.ToY
            });
        Input.Setup(i => i.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new MouseMoveResult(true, x, y)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving, ActualX = x, ActualY = y
            });
        Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int _, InputTarget _, bool _, TextControlChars? _, TextInputMethod method, CancellationToken _) => new TypeTextResult(true, text.Length, text.Length)
            {
                // A paste reports whether the clipboard went back (roadmap M4.4); auto types this short text
                Method = method == TextInputMethod.Paste ? "paste" : method == TextInputMethod.Keys ? "keys" : "unicode",
                ClipboardRestored = method == TextInputMethod.Paste ? true : null,
                ReceivingWindow = receiving, ForegroundAfter = receiving
            });
        Input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, int _, CancellationToken _) => new SendKeysResult(true, keys)
            {
                ReceivingWindow = receiving, ForegroundAfter = receiving
            });
        Input.Setup(i => i.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KeyList keys, int repeat, int _, int _, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(true, keys.Text)
            {
                KeysSent = repeat, ReceivingWindow = receiving, ForegroundAfter = receiving
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
        _capturedWindows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _capturedWindows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _capturedWindows.Setup(w => w.GetWindowRect(It.IsAny<nint>())).Returns((0, 100, 1600, 1000));
        _capturedWindows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>())).Returns(4242u);
        Screenshots = new ScreenshotService(
            TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 1600, captureHeight: 900, windowOriginX: 0, windowOriginY: 100).Object,
            monitors.Object,
            _capturedWindows.Object,
            captureRegistry: Captures);
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
        Windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>())).Returns(
            WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound), "No window matches processName 'notepad'. Nothing was done."));
        Windows.Setup(w => w.GetWindowDetails(It.IsAny<nint>())).Returns((WindowDetails?)null);
        Windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(new TargetPolicyVerdict(true, null, null));
        Windows.Setup(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns((nint handle, bool _) => new WindowCloseResult(handle, false, WindowErrorCode.InvalidHandle, invalid));
        Windows.Setup(w => w.GetApplicationWindows()).Returns([]);
        Windows.Setup(w => w.GetOwnedWindows(It.IsAny<nint>(), It.IsAny<bool>())).Returns([]);
        WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowConditionResult
            {
                TimedOut = true,
                ElapsedMs = 10_000,
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                Error = "No window matching titleEquals 'Save As' appeared within 10000 ms."
            });
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
        Processes.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, bool _, bool _, int _, string? _, KillSafeguards? _, CancellationToken _) =>
                new KillResult(false, id, ErrorMessage: "No process with that ID", ErrorCode: nameof(ProcessErrorCode.ProcessNotFound)));
        Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(false, ErrorMessage: "The executable was not found", ErrorCode: nameof(LaunchErrorCode.ExecutableNotFound)));
        Processes.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) =>
                apps.Select(_ => new LaunchResult(false, ErrorMessage: "The executable was not found", ErrorCode: nameof(LaunchErrorCode.ExecutableNotFound))).ToList());
        Processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(false, null, null, null, null, "Chrome is not installed", nameof(BrowserErrorCode.BrowserNotFound)));

        Browser.Setup(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "Not a browser window", BrowserErrorCode.NotABrowser));
        Browser.Setup(b => b.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "No browser in the foreground", BrowserErrorCode.NoBrowserInForeground));
        Browser.Setup(b => b.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(false, null, null, null, null, null, "Not a browser window", BrowserErrorCode.NotABrowser));
        BrowserTabs.Setup(b => b.ListTabsAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrowserTabsResult.Failed(nameof(BrowserErrorCode.NotABrowser), "Window process 'notepad' is not a recognized browser. Nothing was done."));
        BrowserTabs.Setup(b => b.ActivateTabAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrowserTabResult.Failed(nameof(BrowserErrorCode.CdpTargetNotFound), "No tab with that targetId; it may have closed.", 9222));
        BrowserTabs.Setup(b => b.NavigateAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserNavigateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserNavigateResult(false, "8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F", LoadState: "failed", ElapsedMs: 40, CdpPort: 9222,
                ErrorText: "net::ERR_NAME_NOT_RESOLVED", Error: "The browser could not load the page: net::ERR_NAME_NOT_RESOLVED.",
                ErrorCode: nameof(BrowserErrorCode.NavigationFailed)));
        BrowserTabs.Setup(b => b.WaitAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserWaitCondition>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserWaitResult(false, false, true, 10_000, "8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F", "https://example.com/", "Example Domain", 9222,
                "The page did not meet the condition within 10000 ms: no element matches '#results'.", "WaitTimedOut"));

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
        ElementSearch.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementQuery query, CancellationToken _) => new FindElementsResult(
                false, query.WindowHandle, null, 0, false, null, 1.5, Error: invalid, ErrorCode: UIElementErrorCode.InvalidHandle));
        Popups.Setup(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((nint window, TimeSpan _, CancellationToken _, TimeSpan? _) => new ActivePopupsResult(
                false, (long)window, null, false, null, 0.5, Error: invalid, ErrorCode: UIElementErrorCode.InvalidHandle));
        const string stale = "Element ref 'w4120035e57' is stale: its window closed. Find the element again with find_ui_elements.";
        ElementReads.Setup(r => r.ReadTextAsync(It.IsAny<ElementTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementTextRequest request, CancellationToken _) => new ElementTextResult(
                false, request.Ref, 0, null, null, null, 0, null, false, null, 0.5, Error: stale, ErrorCode: UIElementErrorCode.ElementStale));
        ElementReads.Setup(r => r.ReadTableAsync(It.IsAny<ElementTableRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementTableRequest request, CancellationToken _) => new ElementTableResult(
                false, request.Ref, 0, null, null, null, request.StartRow, null, null, false, 0.5, Error: stale,
                ErrorCode: UIElementErrorCode.ElementStale));
        ElementWait.Setup(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WaitForElementResult(false, false, true, ElapsedMs: 10_000, Polls: 24, WindowHandle: TypicalDesktop.NotepadHandle,
                Element: TypicalDesktop.FoundButtons[0] with { IsEnabled = false }, MatchCount: 1,
                LastObservedState: "Button 'Save' (w4120035e9) is disabled", ErrorCode: "WaitTimedOut",
                Error: "No element matching name 'Save' became enabled within 10000 ms (24 checks): Button 'Save' (w4120035e9) is disabled."));
        ElementActions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => new ElementActionResult(
                false, "set_value", request.Ref, TypicalDesktop.NotepadHandle,
                Error: "Element ref is stale: its window closed. Find the element again with find_ui_elements.",
                ErrorCode: nameof(UIElementErrorCode.ElementStale), ElapsedMilliseconds: 1.5));
        MenuPaths.Setup(m => m.RunAsync(It.IsAny<MenuPathRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementActionResult(false, "menu_path", WindowHandle: TypicalDesktop.NotepadHandle,
                Error: "No visible menu item 'File' (item 1 of 2) was found in the window (or its open popups).",
                ErrorCode: nameof(UIElementErrorCode.ElementNotFound), ElapsedMilliseconds: 21.5));
        const string noDialog = "Window 1706796 ('Untitled - Notepad') is no dialog box and has no open dialog. Nothing was done.";
        Dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DialogResponseResult(false, Error: noDialog, ErrorCode: nameof(UIElementErrorCode.DialogNotFound), ElapsedMilliseconds: 0.5));
        FileDialogs.Setup(d => d.CompleteAsync(It.IsAny<FileDialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileDialogResult(false, "save", Error: noDialog, ErrorCode: nameof(UIElementErrorCode.DialogNotFound),
                ElapsedMilliseconds: 0.5));
        Inspection.Setup(i => i.GetElementAtPointAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int x, int y, int _, TimeSpan _, CancellationToken _) => new ElementAtPointResult(
                false, x, y, null, null, null, null, null, null, 0.5, "The point is outside every monitor", UIElementErrorCode.CoordinatesOutOfBounds));

        const string host = "The input would go to the agent's own terminal";
        Input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "left", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "right", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(false, x, y, "double", host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.MouseClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<MouseModifier>>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, MouseButton button, int _, IReadOnlyList<MouseModifier> _, InputTarget _, bool _, CancellationToken _) =>
                new ClickResult(false, x, y, ToolEnum<MouseButton>.Name(button), host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.DragAsync(It.IsAny<DragRequest>(), It.IsAny<InputTarget>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DragRequest r, InputTarget _, InputTarget _, bool _, CancellationToken _) =>
                new DragResult(false, r.FromX, r.FromY, r.ToX, r.ToY, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new MouseMoveResult(false, x, y, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int _, InputTarget _, bool _, TextControlChars? _, TextInputMethod _, CancellationToken _) => new TypeTextResult(false, text.Length, 0, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, int _, CancellationToken _) => new SendKeysResult(false, keys, host, InputErrorCode.TargetIsAgentHost));
        Input.Setup(i => i.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KeyList keys, int _, int _, int _, InputTarget _, bool _, CancellationToken _) =>
                new SendKeysResult(false, keys.Text, host, InputErrorCode.TargetIsAgentHost));
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
