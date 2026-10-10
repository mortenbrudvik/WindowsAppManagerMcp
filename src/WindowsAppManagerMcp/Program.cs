using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Native;

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

// Register services and MCP tool classes (shared with DependencyInjectionTests, see ServiceRegistration).
// Layout presets live in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts or WAM_LAYOUTS_PATH, outside the
// versioned plugin folder (LayoutStorageOptions.FromEnvironment).
builder.Services.AddWindowsAppManagerServices();

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
// The tool error filter (ToolErrorReporting) and every tool
.AddWindowsAppManagerTools();

var app = builder.Build();

await app.RunAsync();
