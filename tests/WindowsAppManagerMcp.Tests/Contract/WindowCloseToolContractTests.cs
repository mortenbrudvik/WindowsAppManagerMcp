using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: what an MCP client gets back when close_window or close_windows_batch meets a protected
/// window (roadmap M1.7), and what a verified close reports (roadmap M2.5): closed, or promptShown with the prompt.
/// Runs the real WindowManagerTools, WindowService and WindowCloseVerifier through an in-process MCP server, with the
/// server's error reporting filter, a mocked native window wrapper and a mocked host guard, so the close message that
/// is or is not posted is a mock call, never a real one. A window that gets WM_CLOSE is gone afterwards (IsWindow
/// false), unless a test gives it a prompt.
/// </summary>
public class WindowCloseToolContractTests
{
    private const uint WmClose = 0x0010;
    private const int GwlStyle = -16;
    private const uint GwEnabledPopup = 6;
    private const long WsDisabled = 0x08000000;

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
        _native.Setup(n => n.PostMessage(It.IsAny<nint>(), WmClose, nint.Zero, nint.Zero))
            .Callback((nint hWnd, uint _, nint _, nint _) => _native.Setup(n => n.IsWindow(hWnd)).Returns(false))
            .Returns(Win32CallResult.Ok);
    }

    [Fact]
    public async Task CloseWindow_AWindowThatCloses_IsClosed()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("close_window", new Dictionary<string, object?> { ["handle"] = (long)Notepad });

        // Assert
        result.IsError.Should().NotBe(true);
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("status").GetString().Should().Be("closed");
    }

    [Fact]
    public async Task CloseWindow_ASavePrompt_IsPromptShown_WithItsTextAndButtons_AndIsAnError()
    {
        // Arrange - Notepad answers WM_CLOSE with a message box: it disables Notepad and is its enabled popup
        var prompt = new nint(0x2001);
        var text = new nint(0x2002);
        var save = new nint(0x2003);
        var dontSave = new nint(0x2004);
        _native.Setup(n => n.PostMessage(Notepad, WmClose, nint.Zero, nint.Zero)).Callback(() =>
        {
            _native.Setup(n => n.GetWindowLongPtr(Notepad, GwlStyle)).Returns(new nint(WsDisabled));
            _native.Setup(n => n.GetWindow(Notepad, GwEnabledPopup)).Returns(prompt);
        }).Returns(Win32CallResult.Ok);
        AddWindow(prompt, "#32770");
        _native.Setup(n => n.IsWindowVisible(prompt)).Returns(true);
        _native.Setup(n => n.GetWindowText(prompt)).Returns("Notepad");
        AddChild(text, "Static", "Do you want to save changes to Untitled?");
        AddChild(save, "Button", "&Save");
        AddChild(dontSave, "Button", "Do&n't Save");
        _native.Setup(n => n.EnumChildWindows(prompt, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) =>
            {
                foreach (var child in new[] { text, save, dontSave })
                    callback(child);
            });
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("close_window", new Dictionary<string, object?> { ["handle"] = (long)Notepad });

        // Assert
        result.IsError.Should().BeTrue("the window did not close");
        var payload = ParsePayload(result);
        payload.GetProperty("status").GetString().Should().Be("promptShown");
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.PromptShown));
        var promptJson = payload.GetProperty("prompt");
        promptJson.GetProperty("handle").GetInt64().Should().Be((long)prompt);
        promptJson.GetProperty("title").GetString().Should().Be("Notepad");
        promptJson.GetProperty("className").GetString().Should().Be("#32770");
        promptJson.GetProperty("text").GetString().Should().Be("Do you want to save changes to Untitled?");
        promptJson.GetProperty("buttons").EnumerateArray().Select(b => b.GetString()).Should().Equal("Save", "Don't Save");
        promptJson.GetProperty("kind").GetString().Should().Be("modalPopup");
        payload.GetProperty("error").GetString().Should().Contain("Do not answer it yourself").And.Contain("ask which to choose");
    }

    [Theory]
    [InlineData("close_window")]
    [InlineData("close_windows_batch")]
    [InlineData("close_application")]
    public async Task TheCloseToolDescriptions_TellTheAgentNotToAnswerAPromptItself(string tool)
    {
        // Every client sees the descriptions; not every client loads the skill, which says the same. Answering a save
        // prompt with No or Don't Save destroys the user's unsaved work.
        var tools = await ToolSurface.ListToolsAsync();

        var description = ToolSurface.Tool(tools, tool)["description"]!.GetValue<string>();

        description.Should().MatchRegex("(?i)do not answer it yourself");
        description.Should().Contain("ask");
    }

    [Fact]
    public async Task CloseApplication_ClosesTheAppsAltTabWindows_ButNotItsHiddenHelpers()
    {
        // Arrange - Paint's main window, its IME window (visible here, excluded by its class) and a hidden tray window
        var ime = new nint(0x1010);
        var tray = new nint(0x1011);
        AddWindow(ime, "IME");
        AddWindow(tray, "TrayHelper");
        foreach (var window in new[] { Paint, ime, tray })
        {
            _native.Setup(n => n.GetWindowThreadProcessId(window)).Returns(800u);
        }

        _native.Setup(n => n.IsWindowVisible(Paint)).Returns(true);
        _native.Setup(n => n.IsWindowVisible(ime)).Returns(true);
        _native.Setup(n => n.GetWindowText(Paint)).Returns("Untitled - Paint");
        _native.Setup(n => n.GetWindowRect(Paint)).Returns((0, 0, 800, 600));
        _native.Setup(n => n.GetWindowRect(ime)).Returns((0, 0, 1, 1));
        _native.Setup(n => n.GetProcessName(800u)).Returns("mspaint");
        _native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback<Func<nint, bool>>(callback =>
        {
            foreach (var window in new[] { Paint, ime, tray }.Where(w => _native.Object.IsWindow(w)))
                callback(window);
        });
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("close_application", new Dictionary<string, object?> { ["processName"] = "mspaint" });

        // Assert
        var payload = ParsePayload(result);
        result.IsError.Should().NotBe(true, payload.ToString());
        payload.GetProperty("status").GetString().Should().Be("closed");
        payload.GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("handle").GetInt64()).Should().Equal((long)Paint);
        _native.Verify(n => n.PostMessage(Paint, WmClose, nint.Zero, nint.Zero), Times.Once);
        _native.Verify(n => n.PostMessage(ime, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
        _native.Verify(n => n.PostMessage(tray, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
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

    private void AddChild(nint hWnd, string className, string text)
    {
        _native.Setup(n => n.GetClassName(hWnd)).Returns(className);
        _native.Setup(n => n.GetWindowText(hWnd)).Returns(text);
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
                .WithServerTools(new WindowManagerTools(windowService, Mock.Of<IForegroundService>(),
                    closeVerifier: new WindowCloseVerifier(windowService, pollInterval: TimeSpan.FromMilliseconds(5),
                        promptCheckInterval: TimeSpan.FromMilliseconds(10)))));
    }

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
