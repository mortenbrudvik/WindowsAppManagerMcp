using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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
builder.Services.AddSingleton<IMonitorService, MonitorService>();
builder.Services.AddSingleton<IWindowService, WindowService>();
builder.Services.AddSingleton<IInputValidationService, InputValidationService>();
builder.Services.AddSingleton<IProcessService, ProcessService>();
builder.Services.AddSingleton<ILayoutService>(sp =>
{
    var windowService = sp.GetRequiredService<IWindowService>();
    var monitorService = sp.GetRequiredService<IMonitorService>();
    var processService = sp.GetRequiredService<IProcessService>();
    var layoutsPath = Path.Combine(AppContext.BaseDirectory, "layouts");
    return new LayoutService(windowService, monitorService, processService, layoutsPath);
});

// Register MCP tools
builder.Services.AddSingleton<WindowFinderTools>();
builder.Services.AddSingleton<WindowManagerTools>();
builder.Services.AddSingleton<MonitorInfoTools>();
builder.Services.AddSingleton<AppLauncherTools>();
builder.Services.AddSingleton<LayoutPresetTools>();

// Configure MCP Server
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "windows-app-manager",
        Version = "1.0.0"
    };
})
.WithStdioServerTransport()
.WithTools<WindowFinderTools>()
.WithTools<WindowManagerTools>()
.WithTools<MonitorInfoTools>()
.WithTools<AppLauncherTools>()
.WithTools<LayoutPresetTools>();

var app = builder.Build();

await app.RunAsync();
