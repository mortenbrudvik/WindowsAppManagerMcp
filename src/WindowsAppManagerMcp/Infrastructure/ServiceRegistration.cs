using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The server's service and tool registrations. Program.cs and the DI tests both call these, so the
/// tests check exactly what the server registers instead of a copy that can drift.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers the native wrappers, the services and the tool classes, all as singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="layoutStorage">
    /// Creates the layout storage options when <see cref="ILayoutService"/> is first resolved. Defaults to
    /// <see cref="LayoutStorageOptions.FromEnvironment"/>: %LOCALAPPDATA%\WindowsAppManagerMcp\layouts or
    /// WAM_LAYOUTS_PATH, outside the versioned plugin folder. Tests pass a temporary folder.
    /// </param>
    /// <param name="auditLog">
    /// Creates the audit log's options when <see cref="IAuditLog"/> is first resolved (roadmap M2.17). Defaults to
    /// <see cref="AuditLogOptions.FromEnvironment"/>: %LOCALAPPDATA%\WindowsAppManagerMcp\audit or WAM_AUDIT_PATH.
    /// Nothing is written until an audited tool is called; tests that call one pass a temporary folder.
    /// </param>
    public static IServiceCollection AddWindowsAppManagerServices(
        this IServiceCollection services,
        Func<LayoutStorageOptions>? layoutStorage = null,
        Func<AuditLogOptions>? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var createLayoutStorage = layoutStorage ?? LayoutStorageOptions.FromEnvironment;
        var createAuditLog = auditLog ?? AuditLogOptions.FromEnvironment;

        // Services
        services.AddSingleton<INativeWindowWrapper, NativeWindowWrapper>();
        services.AddSingleton<INativeInputWrapper, NativeInputWrapper>();
        // The keyboard layout send_keys maps characters with (roadmap M4.1)
        services.AddSingleton<INativeKeyboardLayoutWrapper, NativeKeyboardLayoutWrapper>();
        services.AddSingleton<IUIAutomationWrapper, UIAutomationWrapper>();
        // CDP (roadmap M5.2): the transport is the only part that talks to a browser's debug port over the network, so the
        // E2E harness refuses it with ICdpService; the port owner check reads the TCP table through a Native wrapper
        services.AddSingleton<ITcpPortOwnerResolver, TcpPortOwnerResolver>();
        services.AddSingleton<ICdpTransport>(provider =>
            new CdpTransport(new ClientWebSocketFactory(), provider.GetRequiredService<ITcpPortOwnerResolver>()));
        services.AddSingleton<ICdpService, CdpService>();
        // launch_browser_with_debug's wait for the DevTools endpoint (roadmap M5.1): /json/version through ICdpService, so
        // the E2E harness's refusal of ICdpService covers it too
        services.AddSingleton<IDebugBrowserReadiness, DebugBrowserReadinessProbe>();
        // The monitor reads are created here unless a test host registered its own; a `monitor` of 'current' is the
        // foreground window's monitor (roadmap M6.7)
        services.AddSingleton<IMonitorService>(sp => new MonitorService(
            sp.GetService<INativeMonitorWrapper>() ?? new NativeMonitorWrapper(), sp.GetRequiredService<INativeWindowWrapper>()));
        // SetWindowPos and ShowWindow wait for the window's thread; this gives up on a hung window at a deadline,
        // so a window tool cannot hold the input gate until the application recovers
        services.AddSingleton<IWindowCallRunner, WindowCallRunner>();
        // Tells windows on other virtual desktops from suspended or prelaunched frames the shell cloaks the same way
        services.AddSingleton<INativeVirtualDesktopWrapper, NativeVirtualDesktopWrapper>();
        // The windows set_window_zorder made topmost (roadmap M6.7), which get_server_info lists
        services.AddSingleton<TopmostPins>();
        services.AddSingleton<IWindowService, WindowService>();
        // Verified close (roadmap M2.5): waits for closed windows and reads their prompts through UI Automation
        services.AddSingleton<IWindowCloseVerifier, WindowCloseVerifier>();
        services.AddSingleton<IInputValidationService, InputValidationService>();
        services.AddSingleton<IBrowserDetectionService, BrowserDetectionService>();
        // The reading tools find a debug browser's port themselves (roadmap M5.3): its command line and the TCP table, both read
        // through Native wrappers; it connects to nothing
        services.AddSingleton<IDebugPortLocator, DebugPortLocator>();
        services.AddSingleton<IBrowserReadingService, BrowserReadingService>();
        // The tab tools (roadmap M5.5): CDP only, over the same transport, port lookup and target policy as the reads
        services.AddSingleton<ICdpTabService, CdpTabService>();
        services.AddSingleton<IBrowserTabService, BrowserTabService>();
        // The DOM bridge (roadmap M5.8): CDP only, as the tab tools; its page anchor reads the window through UI Automation
        // and the window's render widgets (read-only)
        services.AddSingleton<ICdpPageService, CdpPageService>();
        services.AddSingleton<IBrowserViewportLocator, BrowserViewportLocator>();
        services.AddSingleton<IBrowserPageService, BrowserPageService>();

        // browser_handle_dialog (roadmap M5.8, dialogs): a Chrome or Edge window's JavaScript dialog, through UI Automation
        services.AddSingleton<IBrowserDialogService, BrowserDialogService>();
        services.AddSingleton<IProcessService, ProcessService>();
        // Packaged app activation and the installed apps (roadmap M6.5): launch_application's AppUserModelIDs and friendly
        // names, and list_installed_apps. The E2E harness refuses the activation (WrapperCatalog: ProcessStart)
        services.AddSingleton<INativeAppsWrapper, NativeAppsWrapper>();
        services.AddSingleton<IInstalledAppsService>(sp => new InstalledAppsService(sp.GetRequiredService<INativeAppsWrapper>()));
        // The debug browsers' lifecycle (roadmap M5.4): named profiles, reuseIfRunning and close_debug_browser. It reaches a
        // browser only through ICdpService (Browser.close, /json/version), which the E2E harness refuses
        services.AddSingleton(_ => DebugBrowserProfiles.FromEnvironment());
        services.AddSingleton<IDebugBrowserLifecycle, DebugBrowserLifecycle>();
        // Finds the window a launch opens, whichever process owns it (launch tools and apply_layout's launchMissing)
        services.AddSingleton<IWindowWaitService, WindowWaitService>();
        // Window events that wake wait_for_window's sleeps between polls (roadmap M3.F22): out-of-context WinEvent hooks on
        // a thread of their own, which the first wait that sleeps starts and the container stops with the server
        services.AddSingleton<INativeWinEventWrapper, NativeWinEventWrapper>();
        services.AddSingleton<IWindowEventSource, WindowEventSource>();

        // get_window_events' journal (roadmap M7.2): listens to the same window events while it is read, and stops after 10
        // minutes without a read
        services.AddSingleton<IWindowEventJournal, WindowEventJournal>();
        // Places the launch tools' windows (placement, roadmap M3.13) and apply_layout's launched ones (roadmap M3.F27)
        // through the window and foreground services, finding a named monitor again by its id when it places (M6.7, M6.8)
        services.AddSingleton<ILaunchPlacementService>(sp => new LaunchPlacementService(
            sp.GetRequiredService<IWindowService>(), sp.GetRequiredService<IForegroundService>(), sp.GetRequiredService<IInputSession>(),
            monitors: sp.GetRequiredService<IMonitorService>()));
        services.AddSingleton<IScreenCaptureWrapper, ScreenCaptureWrapper>();
        // Captures register under a captureId, and the mouse tools convert image, window and client points (roadmap M3.11).
        // They keep their visual signatures only while wait_for_visual is served (roadmap M6.4, owner decision D9), so the
        // captures cost nothing more otherwise; without a selection (the tests) every tool is served
        services.AddSingleton<ICaptureRegistry>(sp => new CaptureRegistry(
            keepSignatures: sp.GetService<ToolsetSelection>()?.Serves(VisualTools.WaitForVisualTool, readOnlyHint: true) ?? true));
        services.AddSingleton<ICoordinateSpaceResolver, CoordinateSpaceResolver>();
        // The target policy's check of a captured window: the capture tools and the raw pixel reads (roadmap M6) share it
        services.AddSingleton<ICaptureTargetPolicy, CaptureTargetPolicy>();
        // Windows.Graphics.Capture for window captures (roadmap M7.1): its Direct3D device is made with the first capture
        services.AddSingleton<IGraphicsCaptureWrapper, NativeGraphicsCaptureWrapper>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        // wait_for_visual (roadmap M6.4): frames compared by their signatures, with the capture checks, the policy's mask
        // of region and monitor reads, and the caret left out
        services.AddSingleton<IVisualChangeService>(sp => new VisualChangeService(
            sp.GetRequiredService<IScreenCaptureWrapper>(), sp.GetRequiredService<IScreenshotService>(),
            sp.GetRequiredService<INativeWindowWrapper>(), sp.GetRequiredService<IMonitorService>(),
            sp.GetRequiredService<ICaptureTargetPolicy>(), sp.GetRequiredService<IWindowCallRunner>(),
            sp.GetRequiredService<ICaptureRegistry>(), sp.GetRequiredService<INativeThreadInputWrapper>(),
            sp.GetRequiredService<ITargetPolicy>()));
        // Windows OCR (roadmap M6.3): read_screen_text and find_text_on_screen read the raw pixels above. The wrapper loads
        // WinRT only on its first call, so a server without the ocr toolset never does
        services.AddSingleton<IOcrEngineWrapper, OcrEngineWrapper>();
        services.AddSingleton<IOcrService>(sp => new OcrService(
            sp.GetRequiredService<IScreenshotService>(), sp.GetRequiredService<IOcrEngineWrapper>(),
            sp.GetRequiredService<INativeWindowWrapper>(), sp.GetRequiredService<ICaptureTargetPolicy>(),
            sp.GetRequiredService<ITargetPolicy>()));
        services.AddSingleton<IUIElementService, UIElementService>();
        // UI Automation tree seam (UIElementService) and single-element reads (get_focused_element,
        // get_element_at_point); UiaWorker runs every UI Automation read under a deadline. The engine (roadmap M3.2):
        // the COM client (Uia3Tree) unless WAM_UIA_ENGINE=uia2 chooses the managed one (UiaTree); neither touches UI
        // Automation before its first read
        services.AddSingleton(_ => UiaEngineOptions.FromEnvironment());
        services.AddSingleton<IUiaTree>(sp => sp.GetRequiredService<UiaEngineOptions>().Engine == UiaEngine.Uia2
            ? new UiaTree()
            : new Uia3Tree());
        services.AddSingleton<IUiaWorker, UiaWorker>();
        services.AddSingleton<INativeHitTestWrapper, NativeHitTestWrapper>();
        services.AddSingleton<IElementInspectionService, ElementInspectionService>();
        // Warms up the web content of Chromium, Electron and WebView2 windows before the two services above read them
        // (roadmap M3.3); it remembers the windows it warmed up
        services.AddSingleton<IAccessibilityWarmup>(sp => new AccessibilityWarmup(
            sp.GetRequiredService<IUiaTree>(), sp.GetRequiredService<INativeWindowWrapper>(),
            sp.GetRequiredService<INativeProcessWrapper>(), policy: sp.GetRequiredService<ITargetPolicy>()));
        // Element refs (roadmap M3.4): the read tools register the elements they return, find_ui_elements searches with
        // the provider evaluating its criteria, and the tools that act on a ref resolve it here. WAM_UIA_PROVIDER_CONTAINS=1
        // lets the UIA3 provider narrow a contains name search too (roadmap M3.F12; off until checked on a real desktop)
        services.AddSingleton(_ => ProviderContainsOptions.FromEnvironment());
        services.AddSingleton<IElementRegistry, ElementRegistry>();
        services.AddSingleton<IElementSearchService, ElementSearchService>();
        // The popups of a window (roadmap M3.5): find_ui_elements' windowAndPopups and desktop scopes, get_active_popups,
        // and the tools that act on or wait for a popup
        services.AddSingleton<IPopupService>(sp => new PopupService(
            sp.GetRequiredService<INativeWindowWrapper>(), sp.GetRequiredService<INativeProcessWrapper>(),
            sp.GetRequiredService<IHostGuard>(), sp.GetRequiredService<ITargetPolicy>(), sp.GetRequiredService<IUiaTree>(),
            sp.GetRequiredService<IUiaWorker>(), sp.GetRequiredService<IElementRegistry>()));
        // read_element_text and read_table (roadmap M3.9): text and tables through the patterns, by ref or find's criteria
        services.AddSingleton<IElementReadService, ElementReadService>();
        // wait_for_element (roadmap M3.8): waits for the window through IWindowWaitService, then checks with the search above;
        // UI Automation events wake its sleeps between checks (roadmap M3.F22), added and removed on each watch's own thread
        services.AddSingleton<IElementEventSource>(sp => new ElementEventSource(
            sp.GetRequiredService<IUiaTree>(), logger: sp.GetService<ILogger<ElementEventSource>>()));
        services.AddSingleton<IElementWaitService, ElementWaitService>();
        // element_action (roadmap M3.6): pattern calls on threads of their own (a blocked Invoke never stops the reads of the
        // dialog it opened), the application's new windows, and the action itself behind the input guard's target checks
        services.AddSingleton<IUiaActionRunner, UiaActionRunner>();
        services.AddSingleton<IAppWindowProbe, AppWindowProbe>();
        services.AddSingleton<IElementActionService, ElementActionService>();
        // get_desktop_state (roadmap M3.12) and the action tools' observe: one observation through the reads above, under one deadline
        services.AddSingleton<IDesktopStateService>(sp => new DesktopStateService(
            sp.GetRequiredService<IWindowService>(), sp.GetRequiredService<IInputSession>(), sp.GetRequiredService<INativeInputWrapper>(),
            sp.GetRequiredService<IElementInspectionService>(), sp.GetRequiredService<IUIElementService>(), sp.GetRequiredService<IPopupService>(),
            sp.GetRequiredService<IScreenshotService>(), sp.GetRequiredService<ITargetPolicy>(), sp.GetService<ToolsetSelection>()));
        // menu_path, respond_to_dialog and file_dialog (roadmap M3.5, M3.14): searches and popups to find the elements, and
        // every action through element_action's service above, so its guards apply to each step
        services.AddSingleton<IMenuPathService, MenuPathService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IInputSimulationService, InputSimulationService>();

        // Workspaces (roadmap M7.5): File Explorer's folders through the shell's window list, read on a thread of its own
        services.AddSingleton<INativeShellWindowsWrapper, NativeShellWindowsWrapper>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<ILayoutService>(sp =>
        {
            var windowService = sp.GetRequiredService<IWindowService>();
            var monitorService = sp.GetRequiredService<IMonitorService>();
            var processService = sp.GetRequiredService<IProcessService>();
            var foregroundService = sp.GetRequiredService<IForegroundService>();

            // launchMissing's windows are placed as the launch tools place theirs (roadmap M3.F27); save_layout records a
            // packaged app's window by its appId (roadmap M6.8)
            return new LayoutService(windowService, monitorService, processService, foregroundService, createLayoutStorage(),
                launchPlacement: sp.GetRequiredService<ILaunchPlacementService>(), apps: sp.GetRequiredService<INativeAppsWrapper>(),
                installedApps: sp.GetRequiredService<IInstalledAppsService>(), logger: sp.GetService<ILogger<LayoutService>>());
        });

        // Verified window activation (focus_window, apply_layout, and the input guard): all attach and
        // activation calls run on one dedicated thread, started on first use
        services.AddSingleton<INativeThreadInputWrapper, NativeThreadInputWrapper>();
        services.AddSingleton<IForegroundThread, ForegroundThread>();
        services.AddSingleton<IForegroundService, ForegroundService>();

        // Safety probes: integrity, elevation and desktop state, and the agent host recorded at start
        // (HostGuardStartup creates the guard when the server starts, see IHostGuard)
        services.AddSingleton<INativeProcessWrapper, NativeProcessWrapper>();
        services.AddSingleton<INativeSecurityWrapper, NativeSecurityWrapper>();
        services.AddSingleton<IProcessSecurityProbe, ProcessSecurityProbe>();
        services.AddSingleton<IHostGuard, HostGuard>();
        services.AddHostedService<HostGuardStartup>();

        // The effective DPI awareness (from app.manifest), the foreground lock timeout and the session, for
        // get_server_info and the log at start
        services.AddSingleton<INativeSystemInfoWrapper, NativeSystemInfoWrapper>();
        // Logs the versions, the DPI awareness, the toolsets, and the clipboard and URI scheme settings in force
        services.AddHostedService(sp => new ServerStartupLog(
            sp.GetRequiredService<INativeSystemInfoWrapper>(),
            sp.GetService<ToolsetSelection>(),
            sp.GetService<IOptions<McpServerOptions>>(),
            sp.GetService<ILogger<ServerStartupLog>>(),
            sp.GetService<ClipboardOptions>(),
            ProtocolAllowlist.FromEnvironment()));
        services.AddSingleton<IServerInfoService>(sp => new ServerInfoService(
            sp.GetRequiredService<INativeSystemInfoWrapper>(),
            sp.GetRequiredService<IProcessSecurityProbe>(),
            sp.GetRequiredService<IMonitorService>(),
            sp.GetRequiredService<IInputSafetyInfo>(),
            sp.GetRequiredService<IInputSession>(),
            sp.GetRequiredService<IPauseHotkey>(),
            sp.GetRequiredService<ILayoutService>(),
            sp.GetRequiredService<INativeWindowWrapper>(),
            sp.GetService<ToolsetSelection>(), // registered by AddWindowsAppManagerTools
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<ClipboardOptions>(),
            ProtocolAllowlist.FromEnvironment(),
            sp.GetService<UiaEngineOptions>(),
            sp.GetService<TopmostPins>()));

        // The audit log of input and destructive actions (roadmap M2.17), written by AuditLogToolFilter
        services.AddSingleton<IAuditLog>(sp => new AuditLog(
            createAuditLog(), sp.GetRequiredService<INativeWindowWrapper>(), TimeProvider.System, sp.GetService<ILogger<AuditLog>>()));

        // The input guard (every input call, inside the input gate) and the target policy (input, close,
        // kill and window captures), both configured by the user in the server's environment
        services.AddSingleton<ITargetPolicy>(sp =>
            new TargetPolicy(TargetPolicySettings.FromEnvironment(), sp.GetRequiredService<INativeWindowWrapper>()));
        services.AddSingleton(_ => InputGuardOptions.FromEnvironment());
        services.AddSingleton<IInputTargetGuard, InputTargetGuard>();
        services.AddSingleton<IInputSafetyInfo, InputSafetyInfo>();
        services.AddHostedService<InputSafetyStartup>(); // logs the settings in force at start

        // The user's control over the server's input (roadmap M1.10): every batch goes through the input session,
        // and the pause hotkey (WAM_PAUSE_HOTKEY) runs on its own thread, started when the server starts
        services.AddSingleton(_ => PauseHotkeyOptions.FromEnvironment());
        services.AddSingleton<INativeHotkeyWrapper, NativeHotkeyWrapper>();
        services.AddSingleton<IPauseHotkey, PauseHotkeyListener>();
        services.AddSingleton<IInputSession, InputSession>();
        services.AddHostedService<InputSessionStartup>(); // starts the hotkey; releases held input when the server stops

        // Takeover detection (roadmap M7.4, opt-in with WAM_TAKEOVER_DETECTION): low-level keyboard and mouse hooks on their own
        // thread, installed only while type_text, drag or run_steps runs; nothing is started with mode off
        services.AddSingleton(_ => TakeoverOptions.FromEnvironment());
        services.AddSingleton<INativeLowLevelInputWrapper, NativeLowLevelInputWrapper>();
        services.AddSingleton<ITakeoverMonitor, UserTakeoverMonitor>();

        // The clipboard (roadmap M2.16): get_clipboard returns only what the agent put there unless the user allows more
        // (WAM_ALLOW_USER_CLIPBOARD, or a yes when asked); the input service tells the origin about the agent's copies
        // (and the window they went to, which must be in front when the copy happens), and pastes only what
        // get_clipboard would return (the paste guard)
        services.AddSingleton<INativeClipboardWrapper, NativeClipboardWrapper>();
        services.AddSingleton<IClipboardOrigin>(sp => new ClipboardOrigin(
            sp.GetRequiredService<INativeClipboardWrapper>(), windows: sp.GetRequiredService<INativeWindowWrapper>()));
        services.AddSingleton(_ => ClipboardOptions.FromEnvironment());
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IClipboardPasteGuard, ClipboardPasteGuard>();
        services.AddSingleton<IPasteClipboard, PasteClipboard>();

        // MCP tool classes
        services.AddSingleton<WindowFinderTools>();
        services.AddSingleton<WindowManagerTools>();
        services.AddSingleton<MonitorInfoTools>();
        services.AddSingleton<AppLauncherTools>();
        services.AddSingleton<LayoutPresetTools>();
        services.AddSingleton<WorkspaceTools>();
        services.AddSingleton<BrowserReadingTools>();
        services.AddSingleton<BrowserTabTools>();
        services.AddSingleton<BrowserPageTools>();
        services.AddSingleton<ScreenshotTools>();
        services.AddSingleton<OcrTools>();
        services.AddSingleton<VisualTools>();
        services.AddSingleton<UIElementTools>();
        services.AddSingleton<PopupTools>();
        services.AddSingleton<ElementReadTools>();
        services.AddSingleton<ElementWaitTools>();
        services.AddSingleton<ElementActionTools>();
        services.AddSingleton<DesktopStateTools>();
        services.AddSingleton<DialogTools>();
        services.AddSingleton<InputSimulationTools>();
        services.AddSingleton<InputControlTools>();
        services.AddSingleton<ServerInfoTools>();
        services.AddSingleton<ClipboardTools>();
        services.AddSingleton<StepSequenceTools>();

        return services;
    }

    /// <summary>
    /// The server's log levels: Warning and above for everything, plus the per-call lines of
    /// <see cref="ToolErrorReporting.CallLoggerCategory"/> (tool, duration and outcome of every call)
    /// and the agent host that <see cref="HostGuard"/> records, the safety settings that
    /// <see cref="InputSafetyStartup"/> reports once at start, the pause hotkey that
    /// <see cref="InputSessionStartup"/> registers and the versions, DPI awareness and toolsets that
    /// <see cref="ServerStartupLog"/> reports, at Information.
    /// Program.cs writes them to stderr; stdout carries the MCP protocol.
    /// </summary>
    public static ILoggingBuilder AddWindowsAppManagerLogLevels(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        return logging
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(ToolErrorReporting.CallLoggerCategory, LogLevel.Information)
            .AddFilter(typeof(HostGuard).FullName, LogLevel.Information)
            .AddFilter(typeof(InputSafetyStartup).FullName, LogLevel.Information)
            .AddFilter(typeof(InputSessionStartup).FullName, LogLevel.Information)
            .AddFilter(typeof(ServerStartupLog).FullName, LogLevel.Information);
    }

    /// <summary>
    /// Registers the call filters and the server's tools, resources and prompts with the MCP server, and serves the ones
    /// <paramref name="toolsets"/> chooses (roadmap M2.14, M6.9).
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="toolsets">
    /// The toolsets, excluded tools and read-only mode; Program.cs reads them from the environment
    /// (<see cref="ToolsetSelection.FromEnvironment()"/>). Null serves every tool (<see cref="ToolsetSelection.All"/>),
    /// the whole surface the tests and the tools/list snapshot check.
    /// </param>
    public static IMcpServerBuilder AddWindowsAppManagerTools(this IMcpServerBuilder builder, ToolsetSelection? toolsets = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The SDK runs the first call-tool filter added as the outermost one (ServerFilterPipelineTests pins the
        // order; SDK 0.5's XML documentation said the opposite)
        return builder
            // One audit log line per call of an input, close, kill, launch, layout or clipboard-writing tool (roadmap
            // M2.17). Outermost, so it records the result the agent gets, including the error filter's error results
            // and the calls the gate filter refuses while input is paused
            .AddAuditLog()
            // Report tool exceptions to the agent (type + message, isError=true) and log them to stderr,
            // instead of the SDK's opaque "An error occurred invoking '<tool>'." Added before the gate filter, so
            // it is outside it: its call log includes the wait for the gate and the calls the gate filter
            // refuses while input is paused
            .AddToolErrorReporting()
            // A call to a tool 3.0.0 removed is refused with the tool to call instead (owner decision D1), reported as a tool error
            .AddRemovedToolRefusal()
            // Every tool refuses an argument it does not declare (roadmap M3.F42). Inside the two filters above, so the refusal
            // is audited and reported as a tool error; outside the gate filter, so it never waits for the gate
            .AddUndeclaredArgumentRefusal()
            // Tools that can change which window is in front run inside the input gate, so they cannot
            // change it between an input call's guard check and its input
            .AddInputGateSerialization()
            // WithTools<T>() with the server's argument binding and schemas: fixed-value parameters are enums whose
            // values the input schema lists and that are bound strictly (ToolJson, roadmap M2.2)
            .WithServerTools<WindowFinderTools>()
            .WithServerTools<WindowManagerTools>()
            .WithServerTools<MonitorInfoTools>()
            .WithServerTools<AppLauncherTools>()
            .WithServerTools<LayoutPresetTools>()
            .WithServerTools<WorkspaceTools>()
            .WithServerTools<BrowserReadingTools>()
            .WithServerTools<BrowserTabTools>()
            .WithServerTools<BrowserPageTools>()
            .WithServerTools<ScreenshotTools>()
            .WithServerTools<OcrTools>()
            .WithServerTools<VisualTools>()
            .WithServerTools<UIElementTools>()
            .WithServerTools<PopupTools>()
            .WithServerTools<ElementReadTools>()
            .WithServerTools<ElementWaitTools>()
            .WithServerTools<ElementActionTools>()
            .WithServerTools<DesktopStateTools>()
            .WithServerTools<DialogTools>()
            .WithServerTools<InputSimulationTools>()
            .WithServerTools<InputControlTools>()
            .WithServerTools<ServerInfoTools>()
            .WithServerTools<ClipboardTools>()
            // run_steps (roadmap M6.2): its steps call the tools above through these same filters
            .WithServerTools<StepSequenceTools>()
            // The wam:// resources and the prompts (roadmap M6.9), with their own request log
            .AddWindowsAppManagerResourcesAndPrompts()
            // Last: removes the tools the user's toolsets, exclusions or read-only mode leave out, and the resources and
            // prompts whose tools are then not served
            .WithToolsets(toolsets ?? ToolsetSelection.All());
    }
}
