using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public static IServiceCollection AddWindowsAppManagerServices(
        this IServiceCollection services,
        Func<LayoutStorageOptions>? layoutStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var createLayoutStorage = layoutStorage ?? LayoutStorageOptions.FromEnvironment;

        // Services
        services.AddSingleton<INativeWindowWrapper, NativeWindowWrapper>();
        services.AddSingleton<INativeInputWrapper, NativeInputWrapper>();
        services.AddSingleton<IUIAutomationWrapper, UIAutomationWrapper>();
        services.AddSingleton<ICdpService, CdpService>();
        services.AddSingleton<IMonitorService, MonitorService>();
        // SetWindowPos and ShowWindow wait for the window's thread; this gives up on a hung window at a deadline,
        // so a window tool cannot hold the input gate until the application recovers
        services.AddSingleton<IWindowCallRunner, WindowCallRunner>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IInputValidationService, InputValidationService>();
        services.AddSingleton<IBrowserDetectionService, BrowserDetectionService>();
        services.AddSingleton<IBrowserReadingService, BrowserReadingService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IScreenCaptureWrapper, ScreenCaptureWrapper>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        services.AddSingleton<IUIElementService, UIElementService>();
        // UI Automation tree seam (UIElementService) and single-element reads (get_focused_element,
        // get_element_at_point); UiaWorker runs every UI Automation read under a deadline
        services.AddSingleton<IUiaTree, UiaTree>();
        services.AddSingleton<IUiaWorker, UiaWorker>();
        services.AddSingleton<INativeHitTestWrapper, NativeHitTestWrapper>();
        services.AddSingleton<IElementInspectionService, ElementInspectionService>();
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

        // MCP tool classes
        services.AddSingleton<WindowFinderTools>();
        services.AddSingleton<WindowManagerTools>();
        services.AddSingleton<MonitorInfoTools>();
        services.AddSingleton<AppLauncherTools>();
        services.AddSingleton<LayoutPresetTools>();
        services.AddSingleton<BrowserReadingTools>();
        services.AddSingleton<ScreenshotTools>();
        services.AddSingleton<UIElementTools>();
        services.AddSingleton<InputSimulationTools>();
        services.AddSingleton<InputControlTools>();

        return services;
    }

    /// <summary>
    /// The server's log levels: Warning and above for everything, plus the per-call lines of
    /// <see cref="ToolErrorReporting.CallLoggerCategory"/> (tool, duration and outcome of every call)
    /// and the agent host that <see cref="HostGuard"/> records, the safety settings that
    /// <see cref="InputSafetyStartup"/> reports once at start and the pause hotkey that
    /// <see cref="InputSessionStartup"/> registers, at Information.
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
            .AddFilter(typeof(InputSessionStartup).FullName, LogLevel.Information);
    }

    /// <summary>
    /// Registers the tool error filter and every tool of the server with the MCP server.
    /// </summary>
    public static IMcpServerBuilder AddWindowsAppManagerTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The SDK runs the first filter added as the outermost one (although the XML documentation of
        // McpServerOptions.Filters says the last; ServerFilterPipelineTests pins the real order)
        return builder
            // Report tool exceptions to the agent (type + message, isError=true) and log them to stderr,
            // instead of the SDK's opaque "An error occurred invoking '<tool>'." Added first, so it is the
            // outermost filter: its call log includes the wait for the gate and the calls the gate filter
            // refuses while input is paused
            .AddToolErrorReporting()
            // Tools that can change which window is in front run inside the input gate, so they cannot
            // change it between an input call's guard check and its input
            .AddInputGateSerialization()
            .WithTools<WindowFinderTools>()
            .WithTools<WindowManagerTools>()
            .WithTools<MonitorInfoTools>()
            .WithTools<AppLauncherTools>()
            .WithTools<LayoutPresetTools>()
            .WithTools<BrowserReadingTools>()
            .WithTools<ScreenshotTools>()
            .WithTools<UIElementTools>()
            .WithTools<InputSimulationTools>()
            .WithTools<InputControlTools>();
    }
}
