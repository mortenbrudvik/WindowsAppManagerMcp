using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: what an MCP client gets back when close_window or close_windows_batch meets a protected
/// window (roadmap M1.7). Runs the real WindowManagerTools and WindowService through an in-process MCP
/// server, with the server's error reporting filter, a mocked native window wrapper and a mocked host
/// guard, so the close message that is or is not posted is a mock call, never a real one.
/// </summary>
public class WindowCloseToolContractTests
{
    private const uint WmClose = 0x0010;

    private static readonly nint Desktop = new(0x1001);
    private static readonly nint Terminal = new(0x1002);
    private static readonly nint Notepad = new(0x1003);
    private static readonly nint Paint = new(0x1004);
    private static readonly nint Calculator = new(0x1005);

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IHostGuard> _hostGuard = new();

    public WindowCloseToolContractTests()
    {
        AddWindow(Desktop, "Progman");
        AddWindow(Terminal, "CASCADIA_HOSTING_WINDOW_CLASS");
        AddWindow(Notepad, "Notepad");
        AddWindow(Paint, "MSPaintApp");
        AddWindow(Calculator, "ApplicationFrameWindow");
        _hostGuard.Setup(g => g.IsHostWindow(Terminal)).Returns(true);
        _native.Setup(n => n.PostMessage(It.IsAny<nint>(), WmClose, nint.Zero, nint.Zero)).Returns(Win32CallResult.Ok);
    }

    [Fact]
    public async Task CloseWindow_OnTheDesktop_IsRefusedWithProtectedWindow_AndNothingIsPosted()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("close_window", new Dictionary<string, object?> { ["handle"] = (long)Desktop });

        // Assert
        result.IsError.Should().BeTrue("a refusal has success false, so it goes out with isError (roadmap M2.1)");
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ProtectedWindow));
        payload.GetProperty("error").GetString().Should().Contain("Progman").And.Contain("force=true");
        VerifyNothingPosted();
    }

    [Fact]
    public async Task CloseWindow_OnTheAgentHost_IsRefused_UnlessForceIsTrue()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var refused = ParsePayload(await server.CallToolAsync(
            "close_window", new Dictionary<string, object?> { ["handle"] = (long)Terminal }));
        var forced = ParsePayload(await server.CallToolAsync(
            "close_window", new Dictionary<string, object?> { ["handle"] = (long)Terminal, ["force"] = true }));

        // Assert
        refused.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ProtectedWindow));
        refused.GetProperty("error").GetString().Should().Contain("agent's host");
        forced.GetProperty("success").GetBoolean().Should().BeTrue();
        forced.TryGetProperty("errorCode", out _).Should().BeFalse();
        _native.Verify(n => n.PostMessage(Terminal, WmClose, nint.Zero, nint.Zero), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_MoreThanThreeWindowsWithoutConfirm_IsAToolError_AndNothingIsPosted()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("close_windows_batch", new Dictionary<string, object?>
        {
            ["windows"] = Windows(Notepad, Paint, Calculator, Desktop)
        });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
        payload.GetProperty("error").GetString().Should().Contain("confirm=true");
        VerifyNothingPosted();
    }

    [Fact]
    public async Task CloseWindowsBatch_WithConfirm_ClosesTheOrdinaryWindows_AndRefusesTheProtectedOnes()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("close_windows_batch", new Dictionary<string, object?>
        {
            ["windows"] = Windows(Notepad, Desktop, Terminal, Paint),
            ["delayBetweenMs"] = 0,
            ["confirm"] = true
        }));

        // Assert
        payload.GetProperty("totalRequested").GetInt32().Should().Be(4);
        payload.GetProperty("succeeded").GetInt32().Should().Be(2);
        payload.GetProperty("failed").GetInt32().Should().Be(2);
        var items = payload.GetProperty("results").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("handle").GetInt64()).Should().Equal((long)Notepad, (long)Desktop, (long)Terminal, (long)Paint);
        items[0].TryGetProperty("errorCode", out _).Should().BeFalse();
        items[1].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ProtectedWindow));
        items[2].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ProtectedWindow));
        _native.Verify(n => n.PostMessage(Notepad, WmClose, nint.Zero, nint.Zero), Times.Once);
        _native.Verify(n => n.PostMessage(Paint, WmClose, nint.Zero, nint.Zero), Times.Once);
        _native.Verify(n => n.PostMessage(Desktop, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
        _native.Verify(n => n.PostMessage(Terminal, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_BlockedByUipi_ReturnsAccessDenied()
    {
        // Arrange
        _native.Setup(n => n.PostMessage(Notepad, WmClose, nint.Zero, nint.Zero)).Returns(Win32CallResult.Failure(5));
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync(
            "close_window", new Dictionary<string, object?> { ["handle"] = (long)Notepad }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.AccessDenied));
        payload.GetProperty("error").GetString().Should().Contain("Win32 error 5");
    }

    private void AddWindow(nint hWnd, string className)
    {
        _native.Setup(n => n.IsWindow(hWnd)).Returns(true);
        _native.Setup(n => n.GetClassName(hWnd)).Returns(className);
        _native.Setup(n => n.GetAncestor(hWnd, It.IsAny<WindowAncestor>())).Returns(hWnd);
    }

    private void VerifyNothingPosted() =>
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);

    private static JsonElement Windows(params nint[] handles) =>
        JsonSerializer.SerializeToElement(handles.Select(h => new Dictionary<string, long> { ["handle"] = h }).ToList());

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var windowService = new WindowService(TestDataFactory.CreateMockMonitorService().Object, _native.Object, _hostGuard.Object);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .AddToolErrorReporting()
                .WithServerTools(new WindowManagerTools(windowService, Mock.Of<IForegroundService>())));
    }

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
