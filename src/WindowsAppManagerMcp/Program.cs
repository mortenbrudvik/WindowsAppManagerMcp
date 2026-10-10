using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using WindowsAppManagerMcp.Tools;

// Set per-monitor DPI awareness for accurate window positioning on high-DPI displays
try
{
    NativeMethods.Shcore.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
}
catch
{
    // Windows 8.1+ required for SetProcessDpiAwareness, ignore on older versions
}

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (important for STDIO-based MCP servers)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register services
builder.Services.AddSingleton<INativeWindowWrapper, NativeWindowWrapper>();
builder.Services.AddSingleton<INativeInputWrapper, NativeInputWrapper>();
builder.Services.AddSingleton<IUIAutomationWrapper, UIAutomationWrapper>();
builder.Services.AddSingleton<ICdpService, CdpService>();
builder.Services.AddSingleton<IMonitorService, MonitorService>();
builder.Services.AddSingleton<IWindowService, WindowService>();
builder.Services.AddSingleton<IInputValidationService, InputValidationService>();
builder.Services.AddSingleton<IBrowserDetectionService, BrowserDetectionService>();
builder.Services.AddSingleton<IBrowserReadingService, BrowserReadingService>();
builder.Services.AddSingleton<IProcessService, ProcessService>();
builder.Services.AddSingleton<IScreenCaptureWrapper, ScreenCaptureWrapper>();
builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
builder.Services.AddSingleton<IUIElementService, UIElementService>();
builder.Services.AddSingleton<IInputSimulationService, InputSimulationService>();
builder.Services.AddSingleton<ILayoutService>(sp =>
{
    var windowService = sp.GetRequiredService<IWindowService>();
    var monitorService = sp.GetRequiredService<IMonitorService>();
    var processService = sp.GetRequiredService<IProcessService>();
    // %LOCALAPPDATA%\WindowsAppManagerMcp\layouts or WAM_LAYOUTS_PATH, outside the versioned plugin folder
    return new LayoutService(windowService, monitorService, processService, LayoutStorageOptions.FromEnvironment());
});

// Register MCP tools
builder.Services.AddSingleton<WindowFinderTools>();
builder.Services.AddSingleton<WindowManagerTools>();
builder.Services.AddSingleton<MonitorInfoTools>();
builder.Services.AddSingleton<AppLauncherTools>();
builder.Services.AddSingleton<LayoutPresetTools>();
builder.Services.AddSingleton<BrowserReadingTools>();
builder.Services.AddSingleton<ScreenshotTools>();
builder.Services.AddSingleton<UIElementTools>();
builder.Services.AddSingleton<InputSimulationTools>();

// Configure MCP Server
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "windows-app-manager",
        Version = WindowsAppManagerMcp.ServerVersion.Current
    };
})
.WithStdioServerTransport()
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

var app = builder.Build();

await app.RunAsync();
