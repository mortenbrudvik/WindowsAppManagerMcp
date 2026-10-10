using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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
            return new LayoutService(windowService, monitorService, processService, createLayoutStorage());
        });

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

        return services;
    }

    /// <summary>
    /// The server's log levels: Warning and above for everything, plus the per-call lines of
    /// <see cref="ToolErrorReporting.CallLoggerCategory"/> (tool, duration and outcome of every call)
    /// at Information. Program.cs writes them to stderr; stdout carries the MCP protocol.
    /// </summary>
    public static ILoggingBuilder AddWindowsAppManagerLogLevels(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        return logging
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(ToolErrorReporting.CallLoggerCategory, LogLevel.Information);
    }

    /// <summary>
    /// Registers the tool error filter and every tool of the server with the MCP server.
    /// </summary>
    public static IMcpServerBuilder AddWindowsAppManagerTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            // Report tool exceptions to the agent (type + message, isError=true) and log them to stderr,
            // instead of the SDK's opaque "An error occurred invoking '<tool>'."
            .AddToolErrorReporting()
            .WithTools<WindowFinderTools>()
            .WithTools<WindowManagerTools>()
            .WithTools<MonitorInfoTools>()
            .WithTools<AppLauncherTools>()
            .WithTools<LayoutPresetTools>()
            .WithTools<BrowserReadingTools>()
            .WithTools<ScreenshotTools>()
            .WithTools<UIElementTools>()
            .WithTools<InputSimulationTools>();
    }
}
