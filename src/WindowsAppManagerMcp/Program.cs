using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Infrastructure;

// Per-monitor v2 DPI awareness comes from app.manifest, so every coordinate is a physical pixel. Setting it through
// the API as well would fail with ACCESS_DENIED; ServerStartupLog logs the effective awareness instead.
var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (important for STDIO-based MCP servers)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
// Warning and above, plus one Information line at the start and end of every tool call (tool, duration,
// outcome; never arguments or results). See ServiceRegistration.AddWindowsAppManagerLogLevels.
builder.Logging.AddWindowsAppManagerLogLevels();

// Register services and MCP tool classes (shared with DependencyInjectionTests, see ServiceRegistration).
// Layout presets live in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts or WAM_LAYOUTS_PATH, outside the
// versioned plugin folder (LayoutStorageOptions.FromEnvironment).
builder.Services.AddWindowsAppManagerServices();

// The toolsets, excluded tools and read-only mode the user chose (WAM_TOOLSETS, WAM_EXCLUDE_TOOLS, WAM_READ_ONLY),
// read before the tool registrations; ServerStartupLog logs them and what they leave out
var toolsets = ToolsetSelection.FromEnvironment();

// Configure MCP Server: its name, version (with the commit) and the instructions every client gets
builder.Services.AddMcpServer(ServerIdentity.Configure)
.WithStdioServerTransport()
// The tool error filter (ToolErrorReporting), the input gate and the tools the toolsets serve
.AddWindowsAppManagerTools(toolsets);

var app = builder.Build();

await app.RunAsync();
