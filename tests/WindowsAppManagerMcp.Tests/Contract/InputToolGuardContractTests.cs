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
    private static readonly string[] InputTools =
        ["click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys", "press_keys"];

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
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();
    private nint _inFront;

    public InputToolGuardContractTests()
    {
        // Each window has a thread of its own, and no thread has captured the mouse or opened a menu
        _threads.Setup(t => t.GetWindowThreadId(It.IsAny<nint>())).Returns((nint window) => (uint)window + 1);
        _threads.Setup(t => t.GetGuiThreadInfo(It.IsAny<uint>())).Returns(new GuiThreadInfo(0, 0, 0, 0, 0));

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

        // "Scroll inactive windows when I hover over them" is on, as by default
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.MousePosition);
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
            _windows.Object, _hitTest.Object, _threads.Object, new Mock<INativeProcessWrapper>().Object,
            _windowService.Object, _foreground.Object, _security.Object, _hostGuard.Object,
            new TargetPolicy(TargetPolicySettings.Default), InputGuardOptions.Default);
        var service = new InputSimulationService(monitors.Object, _input.Object, guard, new InputSession(_input.Object),
            hitTest: _hitTest.Object, systemInfo: _systemInfo.Object);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddToolErrorReporting().WithServerTools(new InputSimulationTools(service)));
    }

    private static JsonElement ParsePayload(CallToolResult result)
    {
        // A guard refusal is a result with success false and its errorCode, sent with isError (roadmap M2.1)
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        if (payload.TryGetProperty("success", out var success))
        {
            (result.IsError == true).Should().Be(!success.GetBoolean(), "a result goes out with isError = !success (roadmap M2.1)");
        }

        return payload;
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
    public async Task PressKeys_TheSameChordReading_CtrlWNeedsWindowHandle_AndKeysAfterAltTabAreRefused()
    {
        // press_keys' chords become the same input events as send_keys', which the guard reads the same way (roadmap M4.3)
        _inFront = Notepad;
        await using var server = await StartServerAsync();

        var closing = ParsePayload(await server.CallToolAsync("press_keys", new Dictionary<string, object?> { ["keys"] = "ctrl+w" }));
        var asArray = ParsePayload(await server.CallToolAsync("press_keys", new Dictionary<string, object?> { ["keys"] = new[] { "ctrl", "w" } }));
        var afterAltTab = ParsePayload(await server.CallToolAsync("press_keys", new Dictionary<string, object?>
        {
            ["keys"] = "alt+tab enter",
            ["windowHandle"] = (long)Notepad
        }));

        closing.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.DestructiveChordNeedsTarget));
        asArray.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.DestructiveChordNeedsTarget));
        asArray.GetProperty("keys").GetString().Should().Be("ctrl+w");
        afterAltTab.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.KeysAfterFocusChange));
        VerifyNothingSent();

        // ... and ctrl+w is sent with windowHandle
        var sent = ParsePayload(await server.CallToolAsync("press_keys", new Dictionary<string, object?>
        {
            ["keys"] = "ctrl+w",
            ["windowHandle"] = (long)Notepad
        }));
        sent.GetProperty("success").GetBoolean().Should().BeTrue();
        sent.GetProperty("keysSent").GetInt32().Should().Be(1);
        _input.Verify(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
    }

    [Fact]
    public async Task PressKeys_KeysThatAreNeitherAStringNorAnArrayOfStrings_AreAToolError()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("press_keys", new Dictionary<string, object?> { ["keys"] = 5 });

        result.IsError.Should().BeTrue();
        VerifyNothingSent();
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
    public async Task MouseClick_ACtrlClickOnTheTarget_SendsCtrlAroundTheClick_InOneBatch()
    {
        // Arrange
        At(Notepad);
        await using var server = await StartServerAsync();
        var batches = new List<IReadOnlyList<InputEvent>>();
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback((IReadOnlyList<InputEvent> events) => batches.Add(events.ToList()))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);

        // Act
        var payload = ParsePayload(await server.CallToolAsync("mouse_click", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["modifiers"] = new[] { "ctrl" },
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("clickType").GetString().Should().Be("primary");
        payload.GetProperty("modifiers").EnumerateArray().Select(m => m.GetString()).Should().Equal("ctrl");
        var batch = batches.Should().ContainSingle().Subject;
        batch[0].Should().Be(InputEvent.Key(0xA2, 0));
        batch[^1].Should().Be(InputEvent.Key(0xA2, 0x0002));
    }

    [Fact]
    public async Task Drag_ADropOnTheAgentsTerminal_IsRefused_EvenNamedAsToWindowHandle_AndNothingIsSent()
    {
        // Arrange - from Notepad onto the terminal: a drop is input to the window there
        At(Notepad);
        _hitTest.Setup(h => h.WindowFromPoint(900, 300)).Returns(Terminal);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("drag", new Dictionary<string, object?>
        {
            ["fromX"] = 400,
            ["fromY"] = 300,
            ["toX"] = 900,
            ["toY"] = 300,
            ["windowHandle"] = (long)Notepad,
            ["toWindowHandle"] = (long)Terminal
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        payload.GetProperty("pressed").GetBoolean().Should().BeFalse();
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Notepad);
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_ADropOnAnotherWindow_WithoutToWindowHandle_IsTargetOccluded_AndNothingIsSent()
    {
        // Arrange - to must be on windowHandle's window unless toWindowHandle names the drop's window
        At(Notepad);
        var other = (nint)0x300;
        Window(other, 3001, "explorer", "Downloads");
        _hitTest.Setup(h => h.WindowFromPoint(900, 300)).Returns(other);
        _hitTest.Setup(h => h.GetRootWindow(other)).Returns(other);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("drag", new Dictionary<string, object?>
        {
            ["fromX"] = 400,
            ["fromY"] = 300,
            ["toX"] = 900,
            ["toY"] = 300,
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetOccluded));
        payload.GetProperty("error").GetString().Should().StartWith("The to point:");
        payload.GetProperty("dropWindow").GetProperty("handle").GetInt64().Should().Be(0x300);
        VerifyNothingSent();
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

    [Fact]
    public async Task Scroll_WithWindowHandleOnly_TurnsTheWheelAtItsClientCentre_AndReportsTheWindowUnderThePointer()
    {
        // Arrange - Notepad's client area is centred on (400, 300); the terminal is in front
        At(Notepad);
        _inFront = Terminal;
        _hitTest.Setup(h => h.GetClientAreaOnScreen(Notepad)).Returns(new WindowRect(200, 150, 400, 300));
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("scroll", new Dictionary<string, object?>
        {
            ["windowHandle"] = (long)Notepad,
            ["deltaY"] = 3
        }));

        // Assert - one batch: the move to the point and three notches down
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("x").GetInt32().Should().Be(400);
        payload.GetProperty("y").GetInt32().Should().Be(300);
        payload.GetProperty("deltaY").GetInt32().Should().Be(3);
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Notepad);
        payload.GetProperty("foregroundAfter").GetProperty("handle").GetInt64().Should().Be(Terminal);
        _input.Verify(i => i.SendInput(It.Is<IReadOnlyList<InputEvent>>(batch =>
            batch.Count == 4 && batch.Skip(1).All(e => e == InputEvent.Wheel(-120)))), Times.Once);
    }

    [Fact]
    public async Task Scroll_WithoutWindowHandle_OverTheTerminal_IsRefusedAsTheAgentHost()
    {
        At(Terminal);
        await using var server = await StartServerAsync();

        var payload = ParsePayload(await server.CallToolAsync("scroll", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["deltaY"] = -5
        }));

        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        VerifyNothingSent();
    }

    [Fact]
    public async Task Scroll_WhenTheWheelFollowsTheFocus_AndTheTerminalIsInFront_IsWheelFollowsFocus()
    {
        // Arrange - the user turned "scroll inactive windows" off: the wheel would scroll the agent's terminal
        At(Notepad);
        _inFront = Terminal;
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.Focus);
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("scroll", new Dictionary<string, object?>
        {
            ["x"] = 400,
            ["y"] = 300,
            ["deltaY"] = 2,
            ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.WheelFollowsFocus));
        payload.GetProperty("error").GetString().Should().Contain("focus_window");
        VerifyNothingSent();
    }

    [Theory]
    [InlineData("""{ "deltaY": 3 }""", "windowHandle")]
    [InlineData("""{ "window": { "foreground": true }, "deltaY": 3 }""", "names no window")] // review MCP-S-3
    [InlineData("""{ "x": 400, "deltaY": 3, "windowHandle": 512 }""", "both x and y")]
    [InlineData("""{ "windowHandle": 512 }""", "not both 0")]
    [InlineData("""{ "windowHandle": 512, "deltaY": 101 }""", "between -100 and 100")]
    [InlineData("""{ "windowHandle": 512, "deltaY": -2147483648 }""", "between -100 and 100")] // no overflow
    [InlineData("""{ "windowHandle": 512, "deltaX": -2147483648 }""", "between -100 and 100")]
    public async Task Scroll_InvalidArguments_AreAToolError_AndNothingIsSent(string arguments, string message)
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("scroll",
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!);

        result.IsError.Should().BeTrue();
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain(message).And.Contain("Nothing was scrolled");
        VerifyNothingSent();
    }
}
