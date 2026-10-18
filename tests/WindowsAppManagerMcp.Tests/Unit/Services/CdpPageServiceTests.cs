using System.Diagnostics;
using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// CdpPageService (roadmap M5.8) over the in-memory fakes behind the real CdpTransport and CdpConnection: a fake tab
/// (<see cref="FakePage"/>) that answers the DOM, accessibility, input, dialog and capture commands. No real browser or
/// network is involved.
/// </summary>
public class CdpPageServiceTests
{
    private const int Port = 9222;

    /// <summary>The browser's process: it lists the tab, and listens on its port when each connection opens.</summary>
    private const int BrowserPid = 4242;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private static readonly CdpTarget Tab = new("T1", "page", "Form", "https://example.com/form", "ws://127.0.0.1:9222/devtools/page/T1", Port,
        BrowserPid);

    private static CdpPageService CreateSut(FakePage page) =>
        new(new CdpTransport(FakeCdpHttpHandler.Targets(), new FakeCdpWebSocketFactory(page.Socket), new FakePortOwnerResolver(BrowserPid)));

    private static BrowserElementLocator BySelector(string selector, int? index = null) => new(selector, null, null, null, index);

    #region browser_evaluate

    [Fact]
    public async Task EvaluateAsync_ReturnsTheValueAndItsType_SentByValueInsideTheGate()
    {
        var gate = new RecordingGate();
        var page = new FakePage { GateHeld = () => gate.Held, Evaluate = _ => new { result = new { type = "object", value = new { a = 1 } } } };

        var result = await CreateSut(page).EvaluateAsync(Tab, "({ a: 1 })", awaitPromise: true, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Value!.Value.GetRawText().Should().Be("{\"a\":1}");
        result.Value.ValueType.Should().Be("object");
        var sent = page.Command("Runtime.evaluate", c => Param(c, "expression") == "({ a: 1 })");
        sent.GetProperty("params").GetProperty("returnByValue").GetBoolean().Should().BeTrue();
        sent.GetProperty("params").GetProperty("awaitPromise").GetBoolean().Should().BeTrue();
        sent.GetProperty("params").GetProperty("timeout").GetDouble().Should().BeGreaterThan(0, "the script itself is stopped at the limit");
        page.HeldAt("Runtime.evaluate", c => Param(c, "expression") == "({ a: 1 })").Should().BeTrue();
        page.HeldAt("Runtime.evaluate", c => Param(c, "expression")!.Contains("visibilityState")).Should().BeFalse(
            "the page is read, without the gate, before the script runs");
        gate.Held.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_APageThatIsNoLongerAWebPage_IsBrowserInternalPage_AndTheScriptIsNotRun()
    {
        // The tab was listed with a web page, and has since gone to an extension's page
        var page = new FakePage { PageUrl = "blob:chrome-extension://abcdefghijklmnop/6f1c0e1a", Evaluate = _ => new { result = new { type = "number", value = 1 } } };

        var result = await CreateSut(page).EvaluateAsync(Tab, "document.cookie", true, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserInternalPage));
        page.Commands("Runtime.evaluate").Should().NotContain(c => Param(c, "expression") == "document.cookie");
    }

    [Theory]
    [InlineData("NaN", "number")]
    [InlineData("-0", "number")]
    [InlineData("12345678901234567890n", "bigint")]
    public async Task EvaluateAsync_AValueThatIsNotJson_IsTheStringCdpGives(string unserializable, string type)
    {
        var page = new FakePage { Evaluate = _ => new { result = new { type, unserializableValue = unserializable } } };

        var result = await CreateSut(page).EvaluateAsync(Tab, "x", true, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.Value!.Value.GetString().Should().Be(unserializable);
        result.Value.ValueType.Should().Be(type);
    }

    [Fact]
    public async Task EvaluateAsync_Undefined_HasNoValue()
    {
        var page = new FakePage { Evaluate = _ => new { result = new { type = "undefined" } } };

        var result = await CreateSut(page).EvaluateAsync(Tab, "void 0", true, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.Value.Should().BeNull();
        result.Value.ValueType.Should().Be("undefined");
    }

    [Fact]
    public async Task EvaluateAsync_WhatTheScriptThrew_IsReturnedAsData()
    {
        var page = new FakePage
        {
            Evaluate = _ => new
            {
                result = new { type = "object", subtype = "error" },
                exceptionDetails = new
                {
                    text = "Uncaught",
                    lineNumber = 2,
                    columnNumber = 7,
                    exception = new { type = "object", subtype = "error", description = "Error: nope\n    at <anonymous>:3:8" }
                }
            }
        };

        var result = await CreateSut(page).EvaluateAsync(Tab, "throw new Error('nope')", true, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue("a script that throws is evaluated: what it threw is the result");
        result.Value!.Exception.Should().Be(new BrowserScriptException("Error: nope\n    at <anonymous>:3:8", 2, 7));
        result.Value.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_AThrownValueThatIsNoError_IsDescribed()
    {
        var page = new FakePage
        {
            Evaluate = _ => new { exceptionDetails = new { text = "Uncaught", exception = new { type = "number", value = 42 } } }
        };

        var result = await CreateSut(page).EvaluateAsync(Tab, "throw 42", true, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.Exception!.Text.Should().Be("Uncaught 42");
    }

    [Fact]
    public async Task EvaluateAsync_AValueThatCannotBeReturnedByValue_IsCdpEvaluationFailed()
    {
        var page = new FakePage { EvaluateError = "Object couldn't be returned by value" };

        var result = await CreateSut(page).EvaluateAsync(Tab, "window", true, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpEvaluationFailed));
        result.Error.Should().Contain("returned by value");
    }

    [Fact]
    public async Task EvaluateAsync_AScriptThatGivesNoResultInTime_IsCdpTimeout_AndTheGateIsNotHeldMeanwhile()
    {
        var gate = new RecordingGate();
        var page = new FakePage { ScriptHangs = true, GateHeld = () => gate.Held };
        var stopwatch = Stopwatch.StartNew();
        var scriptLimit = TimeSpan.FromSeconds(5);

        // Act - wait until the gate was taken and let go again, at most well before the script's own limit: the gate is let
        // go after GateHold (a second), while a call that held it for the script's work would hold it until the limit. The
        // margins are seconds on both sides, so a loaded test run neither fails a correct call nor passes a wrong one
        var evaluate = CreateSut(page).EvaluateAsync(Tab, "new Promise(() => {})", true, gate.EnterAsync, scriptLimit);
        var letGoWhileWaiting = await Task.Run(() =>
            SpinWait.SpinUntil(() => gate.Entered > 0 && !gate.Held, scriptLimit - TimeSpan.FromMilliseconds(1500)));
        var stillWaitingForTheScript = !evaluate.IsCompleted;
        var result = await evaluate;

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTimeout));
        result.Error.Should().Contain("promise").And.Contain("browser_handle_dialog").And.NotContain("respond_to_dialog");
        letGoWhileWaiting.Should().BeTrue("the gate is let go after at most a second, not held for a script's work");
        stillWaitingForTheScript.Should().BeTrue("the gate was let go while the call still waited for the script");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task EvaluateAsync_AScriptTheBrowserStoppedAtItsLimit_IsCdpTimeout()
    {
        var page = new FakePage { EvaluateError = "Execution was terminated" };

        var result = await CreateSut(page).EvaluateAsync(Tab, "while (true) {}", false, new RecordingGate().EnterAsync, TimeSpan.FromSeconds(1));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTimeout));
        result.Error.Should().Contain("stopped it");
        page.Command("Runtime.evaluate", c => Param(c, "expression") == "while (true) {}").GetProperty("params").GetProperty("timeout")
            .GetDouble().Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task EvaluateAsync_AScriptThatOpensADialog_IsCdpDialogOpen()
    {
        var page = new FakePage { Evaluate = _ => FakePage.OpenDialog };

        var result = await CreateSut(page).EvaluateAsync(Tab, "alert('x')", true, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpDialogOpen));
    }

    #endregion

    #region Dialogs (roadmap M5.8, dialogs)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClickAsync_WithTheDialogOption_AnswersTheDialogTheClickOpens_OnItsOwnConnection(bool accept)
    {
        // As on Chrome and Edge 155: a dialog opened while this connection has Page.enable is answered on it
        var page = new FakePage { DialogOnRelease = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#delete"), MouseButton.Primary, 1, new RecordingGate().EnterAsync,
            LongTimeout, dialogAnswer: new BrowserDialogAnswer(accept));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Dialog!.Type.Should().Be("confirm");
        result.Value.Answer.Should().Be(new CdpAnsweredDialog(result.Value.Dialog, accept));
        result.Value.AnswerFailure.Should().BeNull();
        var sent = page.Command("Page.handleJavaScriptDialog").GetProperty("params");
        sent.GetProperty("accept").GetBoolean().Should().Be(accept);
        sent.TryGetProperty("promptText", out _).Should().BeFalse("only a prompt takes text");
        page.DialogOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ClickAsync_WithThePromptText_AnswersAPromptWithIt()
    {
        var page = new FakePage { DialogOnRelease = true, DialogType = "prompt" };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#rename"), MouseButton.Primary, 1, new RecordingGate().EnterAsync,
            LongTimeout, dialogAnswer: new BrowserDialogAnswer(true, "Kari Nordmann"));

        result.Value!.Answer!.Accepted.Should().BeTrue();
        page.Command("Page.handleJavaScriptDialog").GetProperty("params").GetProperty("promptText").GetString().Should().Be("Kari Nordmann");
    }

    [Fact]
    public async Task ClickAsync_WithoutTheDialogOption_LeavesTheDialogOpen()
    {
        var page = new FakePage { DialogOnRelease = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#delete"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.Dialog.Should().NotBeNull();
        result.Value.Answer.Should().BeNull();
        page.Methods.Should().NotContain("Page.handleJavaScriptDialog");
        page.DialogOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ClickAsync_ADialogTheBrowserWillNotAnswer_IsLeftOpen_WithTheReason()
    {
        var page = new FakePage { DialogOnRelease = true, RefusesAnswers = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#delete"), MouseButton.Primary, 1, new RecordingGate().EnterAsync,
            LongTimeout, dialogAnswer: new BrowserDialogAnswer(true));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Dialog.Should().NotBeNull();
        result.Value.Answer.Should().BeNull();
        result.Value.AnswerFailure.Should().Contain("No dialog is showing");
    }

    [Fact]
    public async Task TypeAsync_WithTheDialogOption_AnswersTheDialogEnterOpens()
    {
        var page = new FakePage { DialogOnEnter = true };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("query", null, false, PressEnter: true), new RecordingGate().EnterAsync,
            LongTimeout, dialogAnswer: new BrowserDialogAnswer(false));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Dialog!.Type.Should().Be("confirm");
        result.Value.Answer!.Accepted.Should().BeFalse();
        page.Command("Page.handleJavaScriptDialog").GetProperty("params").GetProperty("accept").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ADialogOpenBeforeTheConnection_IsNeverReported_AndTheBrowserWillNotAnswerIt()
    {
        // Chrome and Edge 155 on a real desktop: neither Page.enable nor any session of the browser hands a dialog that was open
        // before the connection over, and Page.handleJavaScriptDialog answers "No dialog is showing": the call can only say a
        // dialog is probably open, and point to browser_handle_dialog, which answers it through UI Automation
        var page = new FakePage { SilentDialog = true };
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#save"), TimeSpan.FromSeconds(1));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTimeout));
        result.Error.Should().Contain("browser_handle_dialog").And.NotContain("respond_to_dialog");
        page.Methods.Should().NotContain("Page.handleJavaScriptDialog");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    #endregion

    #region browser_element_rect

    [Fact]
    public async Task LocateAsync_BySelector_ReturnsTheBoxOfItsQuads_AndThePagesViewport_ChangingNothing()
    {
        var page = new FakePage { Quads = [[100, 200, 188, 200, 188, 232, 100, 232], [100, 232, 150, 232, 150, 248, 100, 248]] };

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#save"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        var box = result.Value!;
        box.Rect.Should().Be(new CssRect(100, 200, 88, 48), "the box around every quad of an element that wraps");
        box.Quads.Should().Equal(new CssRect(100, 200, 88, 32), new CssRect(100, 232, 50, 16));
        box.Visibility.Should().Be("visible");
        box.DevicePixelRatio.Should().Be(1.5);
        (box.ViewportWidth, box.ViewportHeight).Should().Be((1280, 720));
        box.Element.Should().Be(new BrowserElementInfo("button", "Save draft"));
        box.MatchCount.Should().Be(1);
        page.Methods.Should().NotContain(m => m.StartsWith("Input.", StringComparison.Ordinal) || m == "DOM.scrollIntoViewIfNeeded",
            "browser_element_rect only reads");
        Param(page.Command("Runtime.evaluate", c => Param(c, "expression")!.Contains("querySelectorAll")), "expression")
            .Should().Contain(JsonSerializer.Serialize("#save"));
    }

    [Fact]
    public async Task LocateAsync_ReadsThePagesVisibilityAndViewport_InAnIsolatedWorld_WhereThePageCannotLie()
    {
        // A background page that redefines document.visibilityState and its viewport in its main world would pass
        // browser_element_rect's gate and move the rect; the isolated world reads the real ones
        var page = new FakePage { Visibility = "hidden", MainWorldView = ("visible", 640, 360, 3) };

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#save"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Visibility.Should().Be("hidden");
        (result.Value.ViewportWidth, result.Value.ViewportHeight, result.Value.DevicePixelRatio).Should().Be((1280, 720, 1.5));
        page.Command("Page.createIsolatedWorld").GetProperty("params").GetProperty("frameId").GetString().Should().Be(FakePage.MainFrameId);
        page.Command("Runtime.evaluate", c => Param(c, "expression")!.Contains("visibilityState"))
            .GetProperty("params").GetProperty("contextId").GetInt32().Should().Be(FakePage.IsolatedContextId);
    }

    [Fact]
    public async Task LocateAsync_AnIsolatedWorldThePageRefuses_IsAFailure_NotAMainWorldRead()
    {
        // Fails closed: without the isolated world the page's own word is not taken instead
        var sut = new CdpPageService(new CdpTransport(FakeCdpHttpHandler.Targets(), new FakeCdpWebSocketFactory(new FakeCdpWebSocket((socket, command) =>
        {
            switch (command.GetProperty("method").GetString())
            {
                case "Page.getFrameTree":
                    socket.Enqueue(JsonSerializer.Serialize(new
                    {
                        id = command.GetProperty("id").GetInt32(),
                        result = new { frameTree = new { frame = new { id = FakePage.MainFrameId, loaderId = "L1", url = Tab.Url } } }
                    }));
                    break;
                case "Page.createIsolatedWorld":
                    socket.ReplyError(command, -32000, "Could not create isolated world");
                    break;
                case "Runtime.evaluate":
                    socket.ReplyWithValue(command, new { visibility = "visible", vw = 1280, vh = 720, dpr = 1.5, scale = 1, url = Tab.Url, title = Tab.Title });
                    break;
                default:
                    socket.ReplyEmpty(command);
                    break;
            }
        })), new FakePortOwnerResolver(BrowserPid)));

        var result = await sut.LocateAsync(Tab, BySelector("#save"), LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpProtocolError));
        result.Error.Should().Contain("Page.createIsolatedWorld");
    }

    [Fact]
    public async Task LocateAsync_AnElementInsideAFrame_HasTheMainFramesViewport_NotItsFrames()
    {
        // The element's own functions run in its frame (a 300 x 150 iframe here), but its quads are in the main frame's
        // viewport: the viewport is read in the main frame
        var page = new FakePage
        {
            AxNodes = [(31, "button", "Save draft", false)], ElementRealmViewport = (300, 150, 1), Quads = [[10, 410, 98, 410, 98, 442, 10, 442]]
        };

        var result = await CreateSut(page).LocateAsync(Tab, new BrowserElementLocator(null, null, "button", "Save draft"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        (result.Value!.ViewportWidth, result.Value.ViewportHeight, result.Value.DevicePixelRatio).Should().Be((1280, 720, 1.5));
    }

    [Fact]
    public async Task LocateAsync_SeveralMatchesWithoutAnIndex_IsAmbiguousElement_DescribingTheFirst()
    {
        var page = new FakePage { Matches = 3, First = "button 'delete'; button 'delete'; a 'delete all'" };

        var result = await CreateSut(page).LocateAsync(Tab, new BrowserElementLocator(null, "Delete", null, null), LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        result.Error.Should().Contain("3 elements").And.Contain("button 'delete'").And.Contain("index");
    }

    [Fact]
    public async Task LocateAsync_NoMatch_IsElementNotFound()
    {
        var page = new FakePage { Matches = 0 };

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#missing"), LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
    }

    [Fact]
    public async Task LocateAsync_ASelectorThatIsNotCss_IsCdpEvaluationFailed()
    {
        var page = new FakePage { BadSelector = "'##' is not a valid selector." };

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("##"), LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpEvaluationFailed));
        result.Error.Should().Contain("not valid CSS");
    }

    [Fact]
    public async Task LocateAsync_AnElementWithoutABox_IsElementOffscreen()
    {
        var page = new FakePage { Quads = [] };

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#hidden"), LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementOffscreen));
    }

    [Fact]
    public async Task LocateAsync_ByRoleAndName_AsksTheAccessibilityTree_SkippingIgnoredNodes()
    {
        var page = new FakePage
        {
            AxNodes = [(11, "button", "Save draft", true), (12, "button", "Save draft", false)]
        };

        var result = await CreateSut(page).LocateAsync(Tab, new BrowserElementLocator(null, null, "button", "Save draft"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.MatchCount.Should().Be(1);
        var query = page.Command("Accessibility.queryAXTree").GetProperty("params");
        query.GetProperty("role").GetString().Should().Be("button");
        query.GetProperty("accessibleName").GetString().Should().Be("Save draft");
        query.GetProperty("objectId").GetString().Should().Be(FakePage.DocumentObject);
        page.Command("DOM.resolveNode").GetProperty("params").GetProperty("backendNodeId").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task LocateAsync_ByRole_WithAnIndex_TakesThatMatch_AndWithout_IsAmbiguous()
    {
        List<(int, string, string, bool)> links = [(21, "link", "Docs", false), (22, "link", "Blog", false)];
        var page = new FakePage { AxNodes = links };

        // One connection per call: each call gets its own page socket
        var ambiguous = await CreateSut(new FakePage { AxNodes = links }).LocateAsync(Tab, new BrowserElementLocator(null, null, "link", null), LongTimeout);
        var second = await CreateSut(page).LocateAsync(Tab, new BrowserElementLocator(null, null, "link", null, Index: 1), LongTimeout);

        ambiguous.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        ambiguous.Error.Should().Contain("link 'Docs'").And.Contain("link 'Blog'");
        second.Success.Should().BeTrue(second.Error);
        page.Command("DOM.resolveNode").GetProperty("params").GetProperty("backendNodeId").GetInt32().Should().Be(22);
    }

    [Fact]
    public async Task LocateAsync_AnIndexPastTheMatches_IsElementNotFound()
    {
        var page = new FakePage { AxNodes = [(21, "link", "Docs", false)] };

        var result = await CreateSut(page).LocateAsync(Tab, new BrowserElementLocator(null, null, "link", null, Index: 4), LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        result.Error.Should().Contain("index 4");
    }

    [Fact]
    public async Task LocateAsync_APageThatAnswersNothing_IsCdpTimeout_WithTheDialogAdvice()
    {
        var page = new FakePage { Unresponsive = true };
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateSut(page).LocateAsync(Tab, BySelector("#save"), TimeSpan.FromSeconds(1));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTimeout));
        result.Error.Should().Contain("browser_handle_dialog");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    #endregion

    #region browser_click

    [Fact]
    public async Task ClickAsync_ScrollsHitTestsAndClicksTheMiddle_WithTheMouseEventsInsideTheGate()
    {
        var gate = new RecordingGate();
        var page = new FakePage { GateHeld = () => gate.Held };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Point.Should().Be(new CssPoint(144, 216));
        result.Value.Element.Should().Be(new BrowserElementInfo("button", "Save draft"));
        page.Methods.Should().ContainInOrder("DOM.scrollIntoViewIfNeeded", "DOM.getContentQuads", "Runtime.callFunctionOn",
            "Emulation.setFocusEmulationEnabled", "Input.dispatchMouseEvent");
        var mouse = page.Commands("Input.dispatchMouseEvent").Select(c => c.GetProperty("params")).ToList();
        mouse.Select(m => m.GetProperty("type").GetString()).Should().Equal("mouseMoved", "mousePressed", "mouseReleased");
        mouse.Should().OnlyContain(m => m.GetProperty("x").GetDouble() == 144 && m.GetProperty("y").GetDouble() == 216);
        mouse[1].GetProperty("button").GetString().Should().Be("left");
        mouse[1].GetProperty("buttons").GetInt32().Should().Be(1);
        mouse[1].GetProperty("clickCount").GetInt32().Should().Be(1);
        page.GateAt["Input.dispatchMouseEvent"].Should().BeTrue();
        page.GateAt["DOM.scrollIntoViewIfNeeded"].Should().BeTrue("scrolling changes the page");
        page.GateAt["Runtime.evaluate"].Should().BeFalse("the element is found without the gate");
        gate.Entered.Should().Be(1);
        gate.Held.Should().BeFalse();
    }

    [Fact]
    public async Task ClickAsync_AnElementInsideAFrame_IsClippedToTheMainViewport_NotTheFrames()
    {
        // A button in a 300 x 150 iframe placed 400 pixels down: its quad is in the main frame's viewport
        var page = new FakePage
        {
            AxNodes = [(31, "button", "Save draft", false)], ElementRealmViewport = (300, 150, 1), Quads = [[10, 410, 98, 410, 98, 442, 10, 442]]
        };

        var result = await CreateSut(page).ClickAsync(Tab, new BrowserElementLocator(null, null, "button", "Save draft"), MouseButton.Primary, 1,
            new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Point.Should().Be(new CssPoint(54, 426));
    }

    [Fact]
    public async Task ClickAsync_AnElementInAFrameOfAnotherOrigin_IsElementObscured_SayingItCannotBeChecked()
    {
        var page = new FakePage { HitTestReply = new { hit = false, frame = true } };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ElementObscured));
        result.Error.Should().Contain("another origin");
        page.Methods.Should().NotContain("Input.dispatchMouseEvent");
    }

    [Fact]
    public async Task ClickAsync_APageThatIsNoLongerAWebPage_IsBrowserInternalPage_AndNothingIsClicked()
    {
        var page = new FakePage { PageUrl = "chrome-extension://abcdefghijklmnop/options.html" };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserInternalPage));
        page.Methods.Should().NotContain(m => m.StartsWith("Input.", StringComparison.Ordinal) || m == "DOM.scrollIntoViewIfNeeded");
    }

    [Fact]
    public async Task ClickAsync_APauseBeforeThePress_StopsTheClick_WithPausedByUser()
    {
        var page = new FakePage();
        var gate = new RecordingGate { Paused = () => page.Commands("Input.dispatchMouseEvent").Any() };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, gate.EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("0 of 1 click");
        page.Commands("Input.dispatchMouseEvent").Select(c => c.GetProperty("params").GetProperty("type").GetString())
            .Should().Equal("mouseMoved");
        gate.Held.Should().BeFalse();
    }

    [Fact]
    public async Task ClickAsync_APauseTheServerSetItself_SaysSo_NotThatTheUserPressedTheHotkey()
    {
        // Arrange - the server paused itself: it could not read the pause it shares with the other servers (review F4)
        var page = new FakePage();
        var gate = new RecordingGate
        {
            Paused = () => page.Commands("Input.dispatchMouseEvent").Any(),
            PausedBy = InputControlSources.SharedPauseUnreadable
        };

        // Act
        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, gate.EnterAsync, LongTimeout);

        // Assert - the same PausedByUser and the same advice, but never that the user pressed the hotkey
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("could not read the pause it shares").And.Contain("paused itself").And.Contain("0 of 1 click")
            .And.Contain("Stop and ask the user").And.NotContain("pressed the pause hotkey");
    }

    [Fact]
    public async Task TypeAsync_APauseTheUserMade_SaysTheyPressedTheHotkey_WithItsChord()
    {
        var page = new FakePage();
        var gate = new RecordingGate
        {
            Paused = () => page.Commands("DOM.focus").Any(),
            PausedBy = InputControlSources.Hotkey,
            PauseChord = "Ctrl+Alt+Shift+Pause"
        };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("query", "#q", false, false), gate.EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)").And.Contain("nothing was typed");
    }

    [Fact]
    public async Task ClickAsync_APauseAfterAPress_StillReleasesIt_AndSendsNoFurtherClick()
    {
        var page = new FakePage();
        var gate = new RecordingGate { Paused = () => page.Commands("Input.dispatchMouseEvent").Count() >= 2 };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 2, gate.EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("1 of 2 click");
        page.Commands("Input.dispatchMouseEvent").Select(c => c.GetProperty("params").GetProperty("type").GetString())
            .Should().Equal("mouseMoved", "mousePressed", "mouseReleased");
    }

    [Fact]
    public async Task ClickAsync_ADoubleClickWithTheSecondaryButton_SendsTwoClicksOfTheRightButton()
    {
        var page = new FakePage();

        await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Secondary, 2, new RecordingGate().EnterAsync, LongTimeout);

        var mouse = page.Commands("Input.dispatchMouseEvent").Select(c => c.GetProperty("params")).Skip(1).ToList();
        mouse.Select(m => (m.GetProperty("type").GetString(), m.GetProperty("clickCount").GetInt32())).Should().Equal(
            ("mousePressed", 1), ("mouseReleased", 1), ("mousePressed", 2), ("mouseReleased", 2));
        mouse.Should().OnlyContain(m => m.GetProperty("button").GetString() == "right");
    }

    [Fact]
    public async Task ClickAsync_ADisabledElement_IsElementNotEnabled_AndNothingIsClicked()
    {
        var page = new FakePage { Disabled = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#archive"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotEnabled));
        page.Methods.Should().NotContain("Input.dispatchMouseEvent");
    }

    [Fact]
    public async Task ClickAsync_AnElementUnderAnother_IsElementObscured_NamingIt_AndNothingIsClicked()
    {
        var gate = new RecordingGate();
        var page = new FakePage { CoveredBy = "div#cover 'Accept cookies'" };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, gate.EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ElementObscured));
        result.Error.Should().Contain("div#cover 'Accept cookies'");
        page.Methods.Should().NotContain("Input.dispatchMouseEvent");
        gate.Entered.Should().Be(1, "the scroll and the hit test right before the click are inside the gate");
        gate.Held.Should().BeFalse();
        var hitTest = page.Command("Runtime.callFunctionOn", c => Param(c, "functionDeclaration")!.Contains("elementFromPoint"));
        hitTest.GetProperty("params").GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("value").GetDouble())
            .Should().Equal(144, 216);
    }

    [Fact]
    public async Task ClickAsync_AnElementOutsideTheViewport_IsElementOffscreen()
    {
        var page = new FakePage { Quads = [[100, 900, 188, 900, 188, 932, 100, 932]] };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#bottom"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementOffscreen));
        page.Methods.Should().NotContain("Input.dispatchMouseEvent");
    }

    [Fact]
    public async Task ClickAsync_AClickThatOpensADialog_Succeeds_WithTheDialog_WithoutWaitingForTheRelease()
    {
        var page = new FakePage { DialogOnRelease = true };
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#delete"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Dialog!.Type.Should().Be("confirm");
        result.Value.Warning.Should().BeNull();
        stopwatch.Elapsed.Should().BeLessThan(CdpPageService.InputAckTime, "the dialog ends the wait for the release's answer");
    }

    [Fact]
    public async Task ClickAsync_AClickThatOpensADialogOnThePress_StillReleasesTheButton()
    {
        // A mousedown handler opens the dialog: the press is never answered, and the release must still go out, or the
        // page keeps the button pressed
        var page = new FakePage { DialogOnPress = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#delete"), MouseButton.Primary, 2, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Dialog!.Type.Should().Be("confirm");
        page.Commands("Input.dispatchMouseEvent").Select(c => c.GetProperty("params").GetProperty("type").GetString())
            .Should().Equal(["mouseMoved", "mousePressed", "mouseReleased"], "the release of the press, and no second click");
    }

    [Fact]
    public async Task ClickAsync_APageThatDoesNotConfirmTheEvents_Succeeds_WithAWarning()
    {
        var page = new FakePage { InputNeverAcked = true };

        var result = await CreateSut(page).ClickAsync(Tab, BySelector("#save"), MouseButton.Primary, 1, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Warning.Should().Contain("did not confirm");
        page.Commands("Input.dispatchMouseEvent").Should().HaveCount(3, "every event is sent, also when one is not confirmed");
    }

    [Theory]
    [InlineData(0, 0, 800, 600, 400, 300)]
    [InlineData(-50, 100, 100, 40, 25, 120)]
    [InlineData(1200, 700, 200, 100, 1240, 710)]
    public void ClickPoint_IsTheMiddleOfTheBoxsPartInsideTheViewport(double x, double y, double width, double height, double expectedX, double expectedY)
    {
        CdpPageService.ClickPoint([new CssRect(x, y, width, height)], 1280, 720).Should().Be(new CssPoint(expectedX, expectedY));
    }

    [Fact]
    public void ClickPoint_IsNull_WhenNoBoxReachesIntoTheViewport()
    {
        CdpPageService.ClickPoint([new CssRect(10, 800, 50, 20), new CssRect(-90, 10, 80, 20)], 1280, 720).Should().BeNull();
    }

    #endregion

    #region browser_type

    [Fact]
    public async Task TypeAsync_FocusesTheSelectorsField_AndInsertsTheText_InsideTheGate()
    {
        var gate = new RecordingGate();
        var page = new FakePage { GateHeld = () => gate.Held, FieldLengths = new Queue<int>([0, 5]) };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("hello", "#q", Replace: false, PressEnter: false),
            gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.TypedLength.Should().Be(5);
        result.Value.Warning.Should().BeNull();
        page.Methods.Should().ContainInOrder("DOM.focus", "Runtime.evaluate", "Emulation.setFocusEmulationEnabled", "Input.insertText");
        page.Command("Input.insertText").GetProperty("params").GetProperty("text").GetString().Should().Be("hello");
        page.GateAt["Input.insertText"].Should().BeTrue();
        page.GateAt["DOM.focus"].Should().BeTrue("moving the focus changes the page");
        page.HeldAt("Runtime.evaluate", c => Param(c, "expression")!.Contains("querySelectorAll")).Should().BeFalse(
            "the field is found without the gate");
        page.Command("Runtime.callFunctionOn", c => Param(c, "functionDeclaration")!.Contains("activeElement"))
            .GetProperty("params").GetProperty("objectId").GetString().Should().Be("EL", "the focus is checked to be on the field");
        page.Methods.Should().NotContain("Input.dispatchKeyEvent");
    }

    [Fact]
    public async Task TypeAsync_AFieldWhosePageMovesTheFocusOn_IsFocusNotOnElement_AndNothingIsTyped()
    {
        // An auto-advancing code field, an overlay that grabs the focus: the text must not follow the focus
        var page = new FakePage { FocusMovedTo = "textarea" };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("secret", "#password", false, false),
            new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.FocusNotOnElement));
        result.Error.Should().Contain("moved the focus to a textarea");
        page.Methods.Should().NotContain("Input.insertText");
    }

    [Fact]
    public async Task TypeAsync_APauseAfterTheText_DoesNotPressEnter_AndSaysTheTextWasTyped()
    {
        var page = new FakePage();
        var gate = new RecordingGate { Paused = () => page.Commands("Input.insertText").Any() };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("query", "#q", false, PressEnter: true), gate.EnterAsync,
            LongTimeout);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("text was typed, but Enter was not pressed");
        page.Methods.Should().NotContain("Input.dispatchKeyEvent");
        gate.Held.Should().BeFalse();
    }

    [Fact]
    public async Task TypeAsync_APauseBeforeTheText_TypesNothing()
    {
        var page = new FakePage();
        var gate = new RecordingGate { Paused = () => page.Commands("DOM.focus").Any() };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("query", "#q", false, false), gate.EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("nothing was typed");
        page.Methods.Should().NotContain("Input.insertText");
    }

    [Fact]
    public async Task TypeAsync_ReplaceSelectsTheContentFirst_AndPressEnterSendsEnterAfterTheText()
    {
        var page = new FakePage();

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("bye", null, Replace: true, PressEnter: true),
            new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.PressedEnter.Should().BeTrue();
        Param(page.Command("Runtime.evaluate", c => Param(c, "expression")!.Contains("activeElement")), "expression")
            .Should().Contain("const replace = true");
        var keys = page.Commands("Input.dispatchKeyEvent").Select(c => c.GetProperty("params")).ToList();
        keys.Select(k => (k.GetProperty("type").GetString(), k.GetProperty("key").GetString())).Should().Equal(("keyDown", "Enter"), ("keyUp", "Enter"));
        page.Methods.Should().ContainInOrder("Input.insertText", "Input.dispatchKeyEvent");
        page.Methods.Should().NotContain("DOM.focus", "without a selector the focused element takes the text");
    }

    [Fact]
    public async Task TypeAsync_AFocusedElementThatTakesNoText_IsFocusNotOnElement_AndNothingIsTyped()
    {
        var page = new FakePage { Editable = false };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("hello", null, false, false), new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.FocusNotOnElement));
        result.Error.Should().Contain("button");
        page.Methods.Should().NotContain("Input.insertText");
    }

    [Fact]
    public async Task TypeAsync_AFieldWhoseLengthDidNotChange_Succeeds_WithAWarning()
    {
        var page = new FakePage { FieldLengths = new Queue<int>([3, 3]) };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("abcdef", "#short", false, false), new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Warning.Should().Contain("did not change");
    }

    [Fact]
    public async Task TypeAsync_ASelectorThatMatchesSeveral_IsAmbiguousElement()
    {
        var page = new FakePage { Matches = 2, First = "input; input" };

        var result = await CreateSut(page).TypeAsync(Tab, new BrowserTypeOptions("x", "input", false, false), new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        result.Error.Should().Contain("Narrow the selector").And.Contain("browser_click").And.NotContain("Pass index",
            "browser_type takes no index");
        page.Methods.Should().NotContain("Input.insertText");
    }

    #endregion

    #region browser_screenshot

    [Fact]
    public async Task CaptureAsync_AShownPage_IsCapturedWithoutBringingItToTheFront()
    {
        var gate = new RecordingGate();
        var page = new FakePage();

        var result = await CreateSut(page).CaptureAsync(Tab, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Png.Should().Equal(FakePage.Png);
        result.Value.Activated.Should().BeFalse();
        result.Value.Url.Should().Be("https://example.com/form");
        page.Methods.Should().NotContain("Page.bringToFront");
        gate.Entered.Should().Be(0, "nothing changes, so the gate is not taken");
        var shot = page.Command("Page.captureScreenshot").GetProperty("params");
        shot.GetProperty("format").GetString().Should().Be("png");
        shot.GetProperty("captureBeyondViewport").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CaptureAsync_APageThatIsNotShown_IsBroughtToTheFrontInsideTheGate_First()
    {
        var gate = new RecordingGate();
        var page = new FakePage { Visibility = "hidden", GateHeld = () => gate.Held };

        var result = await CreateSut(page).CaptureAsync(Tab, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Activated.Should().BeTrue();
        page.Methods.Should().ContainInOrder("Page.bringToFront", "Page.captureScreenshot");
        page.GateAt["Page.bringToFront"].Should().BeTrue();
        page.GateAt["Page.captureScreenshot"].Should().BeFalse();
    }

    [Fact]
    public async Task CaptureAsync_NoFrame_IsCdpTimeout_PointingToFocusWindow()
    {
        var page = new FakePage { CaptureNeverAnswers = true };

        var result = await CreateSut(page).CaptureAsync(Tab, new RecordingGate().EnterAsync, TimeSpan.FromSeconds(1));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTimeout));
        result.Error.Should().Contain("focus_window");
    }

    #endregion

    private static string? Param(JsonElement command, string name) =>
        command.TryGetProperty("params", out var p) && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Takes the gate and records whether it is held, as BrowserPageService hands it out: a hold that tells when the user
    /// has paused input (<see cref="Paused"/>).
    /// </summary>
    private sealed class RecordingGate
    {
        public int Entered { get; private set; }

        public bool Held { get; private set; }

        /// <summary>Whether the user has paused input, asked each time the hold is.</summary>
        public Func<bool>? Paused { get; init; }

        /// <summary>How input was paused (InputPauseState.PausedBy), as the hold reports it.</summary>
        public string? PausedBy { get; init; }

        /// <summary>The pause hotkey's chord, as the hold reports it.</summary>
        public string? PauseChord { get; init; }

        public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            Entered++;
            Held = true;
            return Task.FromResult<IDisposable>(new Release(this));
        }

        private sealed class Release(RecordingGate gate) : ICdpGateHold
        {
            public bool IsPaused => gate.Paused?.Invoke() == true;

            public string? PausedBy => gate.PausedBy;

            public string? PauseChord => gate.PauseChord;

            public void Dispose() => gate.Held = false;
        }
    }

    /// <summary>
    /// A tab's DevTools connection: a page with one element (#save, a "Save draft" button at 100,200 88x32 CSS pixels in a
    /// 1280x720 viewport at devicePixelRatio 1.5) unless told otherwise, the accessibility tree's <see cref="AxNodes"/>, a
    /// focused text field, and the dialog, input and capture commands.
    /// </summary>
    private sealed class FakePage
    {
        /// <summary>What <see cref="Evaluate"/> returns to have the page open a dialog instead of answering.</summary>
        public static readonly object OpenDialog = new();

        public const string DocumentObject = "DOC";

        /// <summary>The page's main frame id (Page.getFrameTree).</summary>
        public const string MainFrameId = "MAINFRAME";

        /// <summary>The execution context id Page.createIsolatedWorld answers with for the main frame.</summary>
        public const int IsolatedContextId = 41;

        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        private readonly object _lock = new();
        private bool _dialogOpen;

        public FakePage()
        {
            Socket = new FakeCdpWebSocket(Respond);
        }

        public FakeCdpWebSocket Socket { get; }

        public int Matches { get; init; } = 1;

        public string? First { get; init; }

        public string? BadSelector { get; init; }

        public List<(int BackendId, string Role, string Name, bool Ignored)> AxNodes { get; init; } = [];

        public double[][] Quads { get; init; } = [[100, 200, 188, 200, 188, 232, 100, 232]];

        public bool Disabled { get; init; }

        public string? CoveredBy { get; init; }

        public bool Editable { get; init; } = true;

        /// <summary>The focused field's length at each read, in turn (the last repeats).</summary>
        public Queue<int> FieldLengths { get; init; } = new([0, 1]);

        public string Visibility { get; init; } = "visible";

        /// <summary>
        /// What a page that redefines document.visibilityState, innerWidth, innerHeight and devicePixelRatio
        /// (Object.defineProperty) reports in its main world; null reports the real ones (<see cref="Visibility"/>, 1280 x 720
        /// at 1.5) there too. The isolated world always reads the real ones.
        /// </summary>
        public (string Visibility, double Width, double Height, double Dpr)? MainWorldView { get; init; }

        public Func<JsonElement, object?>? Evaluate { get; init; }

        public string? EvaluateError { get; init; }

        /// <summary>
        /// A dialog that was open before the connection, as Chrome and Edge 155 keep it: never reported (not after Page.enable
        /// either), the page answers nothing, and Page.handleJavaScriptDialog answers "No dialog is showing".
        /// </summary>
        public bool SilentDialog { get; init; }

        /// <summary>The type of the dialog the page opens (DialogOnRelease, DialogOnPress, DialogOnEnter).</summary>
        public string DialogType { get; init; } = "confirm";

        /// <summary>Page.handleJavaScriptDialog is refused even for a dialog opened on this connection.</summary>
        public bool RefusesAnswers { get; init; }

        /// <summary>Enter's keyDown opens the dialog (a form's submit handler).</summary>
        public bool DialogOnEnter { get; init; }

        /// <summary>Whether a dialog the page opened on this connection is still open.</summary>
        public bool DialogOpen
        {
            get
            {
                lock (_lock)
                {
                    return _dialogOpen;
                }
            }
        }

        /// <summary>The page answers no Runtime command, and the browser shows no dialog to handle.</summary>
        public bool Unresponsive { get; init; }

        public bool DialogOnRelease { get; init; }

        /// <summary>A mousedown handler opens the dialog: the press is never answered.</summary>
        public bool DialogOnPress { get; init; }

        /// <summary>The page answers its reads, but not the agent's script.</summary>
        public bool ScriptHangs { get; init; }

        /// <summary>The address the page reports on the connection (default: the tab's).</summary>
        public string PageUrl { get; init; } = Tab.Url;

        /// <summary>
        /// The viewport the element's own frame has (innerWidth, innerHeight, devicePixelRatio), which its functions would read;
        /// default the main frame's.
        /// </summary>
        public (double Width, double Height, double Dpr) ElementRealmViewport { get; init; } = (1280, 720, 1.5);

        /// <summary>The tag of the element a focus handler moves the focus to, after DOM.focus; null: it stays.</summary>
        public string? FocusMovedTo { get; init; }

        /// <summary>The hit test's answer, instead of hit or <see cref="CoveredBy"/>.</summary>
        public object? HitTestReply { get; init; }

        public bool InputNeverAcked { get; init; }

        public bool CaptureNeverAnswers { get; init; }

        public Func<bool>? GateHeld { get; init; }

        /// <summary>Whether the gate was held when each method was first sent.</summary>
        public Dictionary<string, bool?> GateAt { get; } = new();

        /// <summary>Every command sent, with whether the gate was held then.</summary>
        private List<(JsonElement Command, bool? Held)> Sent { get; } = [];

        /// <summary>Whether the gate was held when the first command of <paramref name="method"/> that matches was sent.</summary>
        public bool? HeldAt(string method, Func<JsonElement, bool> where)
        {
            lock (_lock)
            {
                return Sent.First(s => s.Command.GetProperty("method").GetString() == method && where(s.Command)).Held;
            }
        }

        public IReadOnlyList<string> Methods => Socket.SentCommands.Select(c => c.GetProperty("method").GetString()!).ToList();

        public IEnumerable<JsonElement> Commands(string method) =>
            Socket.SentCommands.Where(c => c.GetProperty("method").GetString() == method);

        public JsonElement Command(string method, Func<JsonElement, bool>? where = null) =>
            Commands(method).First(c => where?.Invoke(c) ?? true);

        private void Respond(FakeCdpWebSocket socket, JsonElement command)
        {
            lock (_lock)
            {
                var method = command.GetProperty("method").GetString()!;
                var held = GateHeld?.Invoke();
                GateAt.TryAdd(method, held);
                Sent.Add((command.Clone(), held));
                var p = command.TryGetProperty("params", out var given) ? given : default;
                switch (method)
                {
                    case "Page.enable":
                        if (!SilentDialog && !Unresponsive)
                            socket.ReplyEmpty(command);
                        break;

                    case "Page.handleJavaScriptDialog":
                        if (_dialogOpen && !RefusesAnswers)
                        {
                            _dialogOpen = false;
                            socket.ReplyEmpty(command);
                            socket.EnqueueEvent("Page.javascriptDialogClosed", new { result = true, userInput = "" });
                        }
                        else
                        {
                            socket.ReplyError(command, -32602, "No dialog is showing");
                        }

                        break;

                    case "Runtime.evaluate" or "Runtime.getProperties" or "Runtime.callFunctionOn" or "Accessibility.queryAXTree"
                        or "DOM.resolveNode" or "DOM.getContentQuads" or "DOM.scrollIntoViewIfNeeded" or "DOM.focus"
                        when Unresponsive || SilentDialog:
                        break;

                    case "Runtime.evaluate":
                        RespondToEvaluate(socket, command, p);
                        break;

                    case "Runtime.getProperties":
                        var properties = new List<object> { new { name = "count", value = new { type = "number", value = Matches } } };
                        if (Matches == 1)
                            properties.Add(new { name = "el", value = new { type = "object", subtype = "node", objectId = "EL" } });
                        if (First != null)
                            properties.Add(new { name = "first", value = new { type = "string", value = First } });
                        if (BadSelector != null)
                            properties = [new { name = "error", value = new { type = "string", value = BadSelector } }];
                        Reply(socket, command, new { result = properties });
                        break;

                    case "Runtime.callFunctionOn":
                        var function = p.GetProperty("functionDeclaration").GetString()!;
                        if (function.Contains("elementFromPoint", StringComparison.Ordinal))
                        {
                            socket.ReplyWithValue(command, HitTestReply ?? (CoveredBy == null ? (object)new { hit = true } : new { hit = false, by = CoveredBy }));
                        }
                        else if (function.Contains("activeElement", StringComparison.Ordinal))
                        {
                            socket.ReplyWithValue(command, FocusMovedTo == null ? new { holds = true, tag = (string?)null } : new { holds = false, tag = (string?)FocusMovedTo });
                        }
                        else
                        {
                            // What the element's own frame would read of its viewport: never used for the page's
                            var (vw, vh, dpr) = ElementRealmViewport;
                            socket.ReplyWithValue(command, new
                            {
                                tag = "button", text = "Save draft", disabled = Disabled, top = true, dpr, vw, vh, scale = 1, visibility = Visibility
                            });
                        }

                        break;

                    case "Accessibility.queryAXTree":
                        Reply(socket, command, new
                        {
                            nodes = AxNodes.Select(n => new
                            {
                                nodeId = n.BackendId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ignored = n.Ignored,
                                role = new { type = "role", value = n.Role },
                                name = new { type = "computedString", value = n.Name },
                                backendDOMNodeId = n.BackendId
                            })
                        });
                        break;

                    case "DOM.resolveNode":
                        Reply(socket, command, new { @object = new { type = "object", subtype = "node", objectId = "AX" + p.GetProperty("backendNodeId").GetInt32() } });
                        break;

                    case "DOM.getContentQuads":
                        Reply(socket, command, new { quads = Quads });
                        break;

                    case "Input.dispatchMouseEvent" when InputNeverAcked:
                        break;

                    case "Input.dispatchMouseEvent" when DialogOnPress && p.GetProperty("type").GetString() == "mousePressed":
                        OpenDialogOnPage(socket);
                        break;

                    case "Input.dispatchMouseEvent" when DialogOnRelease && p.GetProperty("type").GetString() == "mouseReleased":
                        OpenDialogOnPage(socket);
                        break;

                    case "Input.dispatchKeyEvent" when DialogOnEnter && p.GetProperty("type").GetString() == "keyDown":
                        OpenDialogOnPage(socket);
                        break;

                    case "Page.captureScreenshot" when CaptureNeverAnswers:
                        break;

                    case "Page.captureScreenshot":
                        Reply(socket, command, new { data = Convert.ToBase64String(Png) });
                        break;

                    case "Page.getFrameTree":
                        Reply(socket, command, new { frameTree = new { frame = new { id = MainFrameId, loaderId = "L1", url = PageUrl } } });
                        break;

                    case "Page.createIsolatedWorld" when p.TryGetProperty("frameId", out var frame) && frame.GetString() == MainFrameId:
                        Reply(socket, command, new { executionContextId = IsolatedContextId });
                        break;

                    case "Page.createIsolatedWorld":
                        socket.ReplyError(command, -32602, "No frame for given id found");
                        break;

                    default:
                        socket.ReplyEmpty(command);
                        break;
                }
            }
        }

        private void RespondToEvaluate(FakeCdpWebSocket socket, JsonElement command, JsonElement p)
        {
            var expression = p.GetProperty("expression").GetString()!;
            if (expression.Contains("querySelectorAll(selector)", StringComparison.Ordinal))
            {
                Reply(socket, command, new { result = new { type = "object", objectId = "HOLDER" } });
            }
            else if (expression == "document")
            {
                Reply(socket, command, new { result = new { type = "object", subtype = "node", objectId = DocumentObject } });
            }
            else if (expression.Contains("activeElement", StringComparison.Ordinal))
            {
                var length = FieldLengths.Count > 1 ? FieldLengths.Dequeue() : FieldLengths.Peek();
                socket.ReplyWithValue(command, Editable ? new { editable = true, tag = "input", length } : new { editable = false, tag = "button", length });
            }
            else if (expression.Contains("visibilityState", StringComparison.Ordinal))
            {
                var isolated = p.TryGetProperty("contextId", out var context) && context.GetInt32() == IsolatedContextId;
                var (visibility, vw, vh, dpr) = isolated || MainWorldView is not { } spoofed ? (Visibility, 1280.0, 720.0, 1.5) : spoofed;
                socket.ReplyWithValue(command, new { visibility, vw, vh, dpr, scale = 1, url = PageUrl, title = Tab.Title });
            }
            else if (expression == "1")
            {
                socket.ReplyWithValue(command, 1);
            }
            else if (ScriptHangs)
            {
                // The agent's script never settles
            }
            else if (EvaluateError != null)
            {
                socket.ReplyError(command, -32000, EvaluateError);
            }
            else
            {
                var answer = Evaluate?.Invoke(command);
                if (ReferenceEquals(answer, OpenDialog))
                {
                    _dialogOpen = true;
                    socket.EnqueueEvent("Page.javascriptDialogOpening", new { url = Tab.Url, message = "x", type = "alert", hasBrowserHandler = true });
                }
                else
                {
                    Reply(socket, command, answer ?? new { result = new { type = "undefined" } });
                }
            }
        }

        /// <summary>The page opens its dialog (<see cref="DialogType"/>): the browser reports it to this connection.</summary>
        private void OpenDialogOnPage(FakeCdpWebSocket socket)
        {
            _dialogOpen = true;
            socket.EnqueueEvent("Page.javascriptDialogOpening",
                new { url = Tab.Url, message = DialogType == "prompt" ? "Your name?" : "Delete it?", type = DialogType, defaultPrompt = "", hasBrowserHandler = true });
        }

        private static void Reply(FakeCdpWebSocket socket, JsonElement command, object result) =>
            socket.Enqueue(JsonSerializer.Serialize(new { id = command.GetProperty("id").GetInt32(), result }));
    }
}
