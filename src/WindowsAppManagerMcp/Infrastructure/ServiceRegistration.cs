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
        services.AddSingleton<IUIAutomationWrapper, UIAutomationWrapper>();
        services.AddSingleton<ICdpService, CdpService>();
        services.AddSingleton<IMonitorService, MonitorService>();
        // SetWindowPos and ShowWindow wait for the window's thread; this gives up on a hung window at a deadline,
        // so a window tool cannot hold the input gate until the application recovers
        services.AddSingleton<IWindowCallRunner, WindowCallRunner>();
        // Tells windows on other virtual desktops from suspended or prelaunched frames the shell cloaks the same way
        services.AddSingleton<INativeVirtualDesktopWrapper, NativeVirtualDesktopWrapper>();
        services.AddSingleton<IWindowService, WindowService>();
        // Verified close (roadmap M2.5): waits for closed windows and reads their prompts through UI Automation
        services.AddSingleton<IWindowCloseVerifier, WindowCloseVerifier>();
        services.AddSingleton<IInputValidationService, InputValidationService>();
        services.AddSingleton<IBrowserDetectionService, BrowserDetectionService>();
        services.AddSingleton<IBrowserReadingService, BrowserReadingService>();
        services.AddSingleton<IProcessService, ProcessService>();
        // Finds the window a launch opens, whichever process owns it (launch tools and apply_layout's launchMissing)
        services.AddSingleton<IWindowWaitService, WindowWaitService>();
        // Places the launch tools' windows (placement, roadmap M3.13) through the window and foreground services
        services.AddSingleton<ILaunchPlacementService>(sp => new LaunchPlacementService(
            sp.GetRequiredService<IWindowService>(), sp.GetRequiredService<IForegroundService>(), sp.GetRequiredService<IInputSession>()));
        services.AddSingleton<IScreenCaptureWrapper, ScreenCaptureWrapper>();
        // Captures register under a captureId, and the mouse tools convert image, window and client points (roadmap M3.11)
        services.AddSingleton<ICaptureRegistry, CaptureRegistry>();
        services.AddSingleton<ICoordinateSpaceResolver, CoordinateSpaceResolver>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
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
            sp.GetRequiredService<IUiaTree>(), sp.GetRequiredService<INativeWindowWrapper>()));
        // Element refs (roadmap M3.4): the read tools register the elements they return, find_ui_elements searches with
        // the provider evaluating its criteria, and the tools that act on a ref resolve it here
        services.AddSingleton<IElementRegistry, ElementRegistry>();
        services.AddSingleton<IElementSearchService, ElementSearchService>();
        // The popups of a window (roadmap M3.5): find_ui_elements' windowAndPopups and desktop scopes, get_active_popups,
        // and the tools that act on or wait for a popup
        services.AddSingleton<IPopupService>(sp => new PopupService(
            sp.GetRequiredService<INativeWindowWrapper>(), sp.GetRequiredService<INativeProcessWrapper>(),
            sp.GetRequiredService<IHostGuard>(), sp.GetRequiredService<ITargetPolicy>(), sp.GetRequiredService<IUiaTree>(),
            sp.GetRequiredService<IUiaWorker>(), sp.GetRequiredService<IElementRegistry>()));
        services.AddSingleton<IInputSimulationService, InputSimulationService>();
        services.AddSingleton<ILayoutService>(sp =>
        {
            var windowService = sp.GetRequiredService<IWindowService>();
            var monitorService = sp.GetRequiredService<IMonitorService>();
            var processService = sp.GetRequiredService<IProcessService>();
            var foregroundService = sp.GetRequiredService<IForegroundService>();
            return new LayoutService(windowService, monitorService, processService, foregroundService, createLayoutStorage());
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
            sp.GetService<UiaEngineOptions>()));

        // The audit log of input and destructive actions (roadmap M2.17), written by AuditLogToolFilter
        services.AddSingleton<IAuditLog>(sp => new AuditLog(
            createAuditLog(), sp.GetRequiredService<INativeWindowWrapper>(), TimeProvider.System, sp.GetService<ILogger<AuditLog>>()));

        // The input guard (every input call, inside the input gate) and the target policy (input, close,
        // kill and window captures), both configured by the user in the server's environment
        services.AddSingleton<ITargetPolicy>(_ => new TargetPolicy(TargetPolicySettings.FromEnvironment()));
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

        // MCP tool classes
        services.AddSingleton<WindowFinderTools>();
        services.AddSingleton<WindowManagerTools>();
        services.AddSingleton<MonitorInfoTools>();
        services.AddSingleton<AppLauncherTools>();
        services.AddSingleton<LayoutPresetTools>();
        services.AddSingleton<BrowserReadingTools>();
        services.AddSingleton<ScreenshotTools>();
        services.AddSingleton<UIElementTools>();
        services.AddSingleton<PopupTools>();
        services.AddSingleton<InputSimulationTools>();
        services.AddSingleton<InputControlTools>();
        services.AddSingleton<ServerInfoTools>();
        services.AddSingleton<ClipboardTools>();

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
    /// Registers the call filters and the server's tools with the MCP server, and serves the ones
    /// <paramref name="toolsets"/> chooses (roadmap M2.14).
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
            .WithServerTools<BrowserReadingTools>()
            .WithServerTools<ScreenshotTools>()
            .WithServerTools<UIElementTools>()
            .WithServerTools<PopupTools>()
            .WithServerTools<InputSimulationTools>()
            .WithServerTools<InputControlTools>()
            .WithServerTools<ServerInfoTools>()
            .WithServerTools<ClipboardTools>()
            // Last: removes the tools the user's toolsets, exclusions or read-only mode leave out
            .WithToolsets(toolsets ?? ToolsetSelection.All());
    }
}
