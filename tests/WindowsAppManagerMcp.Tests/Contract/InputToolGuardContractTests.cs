using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: what an MCP client sends to and gets back from the input tools now that they are guarded
/// (roadmap M1.1, M1.5, M1.12): the windowHandle and allowHostTarget parameters, the refusals and the
/// receivingWindow, foregroundAfter and actualX/actualY fields. Runs the real InputSimulationTools,
/// InputSimulationService and InputTargetGuard through an in-process MCP server, with every native wrapper,
/// probe and the foreground service mocked, so the input that is or is not sent is a mock call.
/// </summary>
public class InputToolGuardContractTests
{
    private static readonly string[] InputTools = ["click", "right_click", "double_click", "mouse_move", "type_text", "send_keys"];

    private static readonly nint Terminal = 0x100;
    private static readonly nint Notepad = 0x200;
    private const int TerminalPid = 1001;
    private const int NotepadPid = 2001;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IHostGuard> _hostGuard = new();
    private nint _inFront;

    public InputToolGuardContractTests()
    {
        Window(Terminal, TerminalPid, "WindowsTerminal", "pwsh");
        Window(Notepad, NotepadPid, "notepad", "Untitled - Notepad");
        _hostGuard.Setup(g => g.IsHostWindow(Terminal)).Returns(true);

        var medium = new ProcessIntegrity(0, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null);
        _security.Setup(s => s.ServerIntegrity).Returns(medium);
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns(medium);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);

        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _inFront);
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) =>
            {
                _inFront = handle;
                return new FocusOutcome { Target = handle, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = handle };
            });
        _foreground.Setup(f => f.IsInFront(It.IsAny<nint>())).Returns((nint handle) => handle == _inFront);

        _input.Setup(i => i.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
        _input.Setup(i => i.GetCursorPos()).Returns((400, 300));
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
    }

    private void Window(nint handle, int processId, string processName, string title)
    {
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        _windows.Setup(w => w.GetWindowLongPtr(handle, -16)).Returns(new nint(0x10CF0000)); // visible overlapped window
        _windowService.Setup(s => s.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)handle, title, processName, processId));
    }

    private void At(nint window)
    {
        _hitTest.Setup(h => h.WindowFromPoint(400, 300)).Returns(window);
        _hitTest.Setup(h => h.GetRootWindow(window)).Returns(window);
    }

    private void VerifyNothingSent()
    {
        _input.Verify(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Never);
        _input.Verify(i => i.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        var guard = new InputTargetGuard(
            _windows.Object, _hitTest.Object, new Mock<INativeThreadInputWrapper>().Object, new Mock<INativeProcessWrapper>().Object,
            _windowService.Object, _foreground.Object, _security.Object, _hostGuard.Object,
            new TargetPolicy(TargetPolicySettings.Default), InputGuardOptions.Default);
        var service = new InputSimulationService(monitors.Object, _input.Object, guard);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddToolErrorReporting().WithTools(new InputSimulationTools(service)));
    }

    private static JsonElement ParsePayload(CallToolResult result)
    {
        result.IsError.Should().NotBe(true, "guard refusals are ordinary results with success: false");
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
    }

    [Fact]
    public async Task ToolsList_EveryInputTool_HasOptionalWindowHandleAndAllowHostTarget()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var tools = await server.Client.ListToolsAsync();

        // Assert
        foreach (var name in InputTools)
        {
            var schema = tools.Single(t => t.Name == name).JsonSchema;
            var properties = schema.GetProperty("properties");
            properties.TryGetProperty("windowHandle", out _).Should().BeTrue("{0} takes windowHandle", name);
            properties.GetProperty("allowHostTarget").GetProperty("default").GetBoolean().Should().BeFalse();
            var required = schema.TryGetProperty("required", out var list)
                ? list.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];
            required.Should().NotContain(["windowHandle", "allowHostTarget"], "{0} works without a target", name);
        }
    }

    [Fact]
    public async Task TypeText_WithoutWindowHandle_TerminalInFront_IsRefusedAsTheAgentHost()
    {
        // Arrange - nothing named, and the agent's own terminal has focus
        _inFront = Terminal;
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("type_text", new Dictionary<string, object?> { ["text"] = "exit" }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        payload.GetProperty("charactersTyped").GetInt32().Should().Be(0);
        var receiving = payload.GetProperty("receivingWindow");
        receiving.GetProperty("handle").GetInt64().Should().Be(Terminal);
        receiving.GetProperty("processName").GetString().Should().Be("WindowsTerminal");
        receiving.GetProperty("processId").GetInt32().Should().Be(TerminalPid);
        payload.GetProperty("foregroundAfter").GetProperty("title").GetString().Should().Be("pwsh");
        VerifyNothingSent();
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TypeText_WithWindowHandle_BringsTheWindowToTheFrontAndReportsIt()
    {
        // Arrange
        _inFront = Terminal;
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("type_text", new Dictionary<string, object?>
        {
            ["text"] = "hello",
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.None));
        payload.GetProperty("charactersTyped").GetInt32().Should().Be(5);
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Notepad);
        payload.GetProperty("foregroundAfter").GetProperty("processName").GetString().Should().Be("notepad");
        payload.TryGetProperty("blockingWindow", out _).Should().BeFalse("null fields are left out");
        _foreground.Verify(f => f.EnsureForeground(Notepad, It.IsAny<int>()), Times.Once);
        _input.Verify(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_DestructiveChord_NeedsWindowHandle()
    {
        // Arrange
        _inFront = Notepad;
        await using var server = await StartServerAsync();

        // Act
        var refused = ParsePayload(await server.CallToolAsync("send_keys", new Dictionary<string, object?> { ["keys"] = "^w" }));

        // Assert
        refused.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.DestructiveChordNeedsTarget));
        refused.GetProperty("error").GetString().Should().Contain("windowHandle");
        VerifyNothingSent();

        // ... and is sent with it
        var sent = ParsePayload(await server.CallToolAsync("send_keys", new Dictionary<string, object?>
        {
            ["keys"] = "^w",
            ["windowHandle"] = (long)Notepad
        }));
        sent.GetProperty("success").GetBoolean().Should().BeTrue();
        _input.Verify(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_KeysAfterAltTab_AreRefusedEvenWithWindowHandle()
    {
        // Arrange - Alt+Tab goes to the previous window (often the agent's terminal) before the command and Enter
        _inFront = Notepad;
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("send_keys", new Dictionary<string, object?>
        {
            ["keys"] = "%{TAB}exit{ENTER}",
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.KeysAfterFocusChange));
        payload.GetProperty("error").GetString().Should().Contain("%{TAB}");
        VerifyNothingSent();
    }

    [Fact]
    public async Task Click_WithWindowHandle_PointOnAnotherWindow_IsTargetOccluded_WithTheCursorPosition()
    {
        // Arrange
        At(Terminal);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetOccluded));
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Terminal);
        payload.GetProperty("actualX").GetInt32().Should().Be(400);
        payload.GetProperty("actualY").GetInt32().Should().Be(300);
        VerifyNothingSent();
    }

    [Fact]
    public async Task Click_AllowHostTargetWithoutWindowHandle_StillCannotReachTheHost()
    {
        // Arrange
        At(Terminal);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["allowHostTarget"] = true
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        payload.GetProperty("error").GetString().Should().Contain("no effect without windowHandle");
        VerifyNothingSent();
    }

    [Fact]
    public async Task Click_WithTheHostAsWindowHandleAndAllowHostTarget_IsSent()
    {
        // Arrange
        At(Terminal);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["windowHandle"] = (long)Terminal,
            ["allowHostTarget"] = true
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Terminal);
        _input.Verify(i => i.SetCursorPos(400, 300), Times.Once);
    }

    [Fact]
    public async Task MouseMove_WithoutWindowHandle_OnAnOrdinaryWindow_ReportsItAndTheCursor()
    {
        // Arrange
        At(Notepad);
        _inFront = Terminal;
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("mouse_move", new Dictionary<string, object?> { ["x"] = 400, ["y"] = 300 }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("receivingWindow").GetProperty("title").GetString().Should().Be("Untitled - Notepad");
        payload.GetProperty("foregroundAfter").GetProperty("handle").GetInt64().Should().Be(Terminal);
        payload.GetProperty("actualX").GetInt32().Should().Be(400);
    }
}
