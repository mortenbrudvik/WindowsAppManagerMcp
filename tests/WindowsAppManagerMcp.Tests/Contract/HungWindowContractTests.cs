using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract (CONCU-01): a window tool whose window does not answer cannot wedge the server. The window tools run
/// inside the process-wide input gate, and SetWindowPos on a hung window would block until the application
/// recovered; WindowService gives up on it at a deadline, so the tool returns NotResponding, the gate is released,
/// and the other window and input calls go on. Runs a real MCP client and server in-process, with the server's
/// filters in the server's order; the native window wrapper is a mock whose SetWindowPos blocks for the hung window.
/// Nothing touches a real window.
/// </summary>
public sealed class HungWindowContractTests : IDisposable
{
    /// <summary>
    /// The deadline when a responsive window's call must answer: far above the time a new thread takes to start, even
    /// on a loaded machine, so that call is never given up on.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    /// <summary>The deadline when every call blocks: nothing has to answer in time.</summary>
    private static readonly TimeSpan BlockedDeadline = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private const long Hung = 0x1001, Responsive = 0x2001;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly ManualResetEventSlim _hungAnswers = new();
    private object _flow = new();

    public HungWindowContractTests()
    {
        foreach (var (window, process) in new[] { ((nint)Hung, 4242u), ((nint)Responsive, 5151u) })
        {
            _native.Setup(n => n.IsWindow(window)).Returns(true);
            _native.Setup(n => n.GetWindowRect(window)).Returns((0, 0, 800, 600));
            _native.Setup(n => n.GetWindowThreadProcessId(window)).Returns(process);
        }

        // The hung application stopped answering a moment ago, so Windows does not report it hung yet
        _native.Setup(n => n.SetWindowPos((nint)Hung, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(() => _hungAnswers.Wait(Long) ? Win32CallResult.Ok : Win32CallResult.Failure(0));
        _native.Setup(n => n.ShowWindow((nint)Hung, It.IsAny<int>())).Returns(() => _hungAnswers.Wait(Long));
        _native.Setup(n => n.SetWindowPos((nint)Responsive, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Ok);
        TestDataFactory.SimulateShowStates(_native, (nint)Responsive);
    }

    public void Dispose()
    {
        // Let the abandoned calls return
        _hungAnswers.Set();
    }

    [Fact]
    public async Task AWindowToolOnAHungWindow_ReturnsNotResponding_AndReleasesTheInputGate()
    {
        // Arrange
        await using var server = await StartServerAsync(BlockedDeadline);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await server.CallToolAsync("move_window", Arguments(Hung), timeout: Long);

        // Assert - the call gave up at the deadline, not when the application answered
        stopwatch.Elapsed.Should().BeLessThan(Long / 2);
        var payload = Payload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.NotResponding));

        // The gate is free again (another test may hold it in parallel, so this test's calls are what is checked)
        InputGate.IsHeldBy(_flow).Should().BeFalse("the window tool released the input gate");
    }

    [Fact]
    public async Task WhileAWindowToolWaitsForAHungWindow_OtherWindowCallsWaitOnlyUntilItsDeadline()
    {
        // Arrange - the first call holds the gate while its SetWindowPos is blocked
        await using var server = await StartServerAsync();
        var stopwatch = Stopwatch.StartNew();
        var hungCall = server.CallToolAsync("set_window_bounds", Arguments(Hung, ("width", 640), ("height", 480)), timeout: Long);

        // Act - a call for a responsive window in parallel, and the same hung window again after the first gave up
        var responsiveCall = server.CallToolAsync("set_window_state", new Dictionary<string, object?>
        {
            ["handle"] = Responsive,
            ["state"] = "minimize"
        }, timeout: Long);
        var hung = Payload(await hungCall);
        var responsive = Payload(await responsiveCall);
        var again = Payload(await server.CallToolAsync("move_window", Arguments(Hung), timeout: Long));

        // Assert - nothing waited for the application; the second call to it failed at once
        stopwatch.Elapsed.Should().BeLessThan(Long / 2);
        hung.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.NotResponding));
        responsive.GetProperty("success").GetBoolean().Should().BeTrue();
        again.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.NotResponding));
        again.GetProperty("error").GetString().Should().Contain("still waiting");
    }

    [Fact]
    public async Task ABatchWithAHungWindow_FailsThatWindow_AndPlacesTheOthers()
    {
        await using var server = await StartServerAsync();

        var result = Payload(await server.CallToolAsync("set_windows_bounds_batch", new Dictionary<string, object?>
        {
            ["placements"] = new[]
            {
                new { handle = Hung, x = 0, y = 0, width = 640, height = 480 },
                new { handle = Responsive, x = 640, y = 0, width = 640, height = 480 }
            },
            ["delayBetweenMs"] = 0
        }, timeout: Long));

        result.GetProperty("succeeded").GetInt32().Should().Be(1);
        var items = result.GetProperty("results").EnumerateArray().ToList();
        items[0].GetProperty("success").GetBoolean().Should().BeFalse();
        items[0].GetProperty("error").GetString().Should().Contain("did not answer");
        items[1].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private Task<InProcessMcpServer> StartServerAsync(TimeSpan? deadline = null)
    {
        // The server's calls run under this test's tag: the gate check sees this test's calls only
        _flow = InputGate.TagFlow();
        var runner = new WindowCallRunner(_native.Object, deadline ?? Deadline, maxStuckCallsPerProcess: 1, maxStuckCalls: 8);
        var windows = new WindowService(TestDataFactory.CreateMockMonitorService().Object, _native.Object, Mock.Of<IHostGuard>(),
            windowCalls: runner);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder
                // The server's filters, in the server's order (ServiceRegistration.AddWindowsAppManagerTools)
                .AddToolErrorReporting()
                .AddInputGateSerialization()
                .WithServerTools(new WindowManagerTools(windows, Mock.Of<IForegroundService>())));
    }

    private static Dictionary<string, object?> Arguments(long handle, params (string Name, object Value)[] more)
    {
        var arguments = new Dictionary<string, object?> { ["handle"] = handle, ["x"] = 10, ["y"] = 10 };
        foreach (var (name, value) in more)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
}
