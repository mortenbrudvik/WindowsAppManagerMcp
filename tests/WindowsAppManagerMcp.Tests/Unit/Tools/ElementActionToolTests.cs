namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// element_action's arguments (roadmap M3.6): a ref, or window and element criteria resolved through find_ui_elements' own
/// search and request model to exactly one element; what is refused before anything is searched or done. The action itself
/// is ElementActionServiceTests'.
/// </summary>
public class ElementActionToolTests
{
    private static readonly nint Handle = 0x8100;

    private readonly Mock<IElementActionService> _actions = new();
    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementRegistry> _registry = new();
    private ElementQuery? _query;
    private ElementActionRequest? _request;
    private FindElementsResult _found = Found(1, Element("w1e7"));

    public ElementActionToolTests()
    {
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _query = query)
            .ReturnsAsync(() => _found);
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _request = request)
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                new ElementActionResult(true, "invoke", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Invoke));
        _registry.Setup(r => r.Lookup(It.IsAny<string>())).Returns((string r) => new ElementLookup(r, Handle, null, 1));
    }

    private readonly Mock<IMenuPathService> _menuPaths = new();
    private readonly Mock<IWindowService> _windowService = new();
    private MenuPathRequest? _menuRequest;

    private ElementActionTools CreateSut() =>
        new(_actions.Object, _search.Object, _registry.Object, _windowService.Object, menuPaths: _menuPaths.Object);

    private void SetUpMenuPaths() =>
        _menuPaths.Setup(m => m.RunAsync(It.IsAny<MenuPathRequest>(), It.IsAny<CancellationToken>()))
            .Callback((MenuPathRequest request, CancellationToken _) => _menuRequest = request)
            .ReturnsAsync(new ElementActionResult(true, "menu_path", "w2e1", Handle, ElementActionStatus.Done, ElementActionMethod.Invoke));

    private static FoundElement Element(string elementRef, string name = "Save") =>
        new(elementRef, name, "Button", "SaveBtn", "Button", null, null, true, false, ["Invoke"]);

    private static FindElementsResult Found(int matchCount, params FoundElement[] elements) =>
        new(true, Handle, "Main", matchCount, matchCount > elements.Length, elements, 1);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task ARef_IsActedOn_Normalized()
    {
        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, @ref: "W1E7").Payload<ElementActionResult>();

        // Assert
        result.Success.Should().BeTrue();
        _request!.Ref.Should().Be("w1e7");
        _request.Action.Should().Be(ElementActionKind.Invoke);
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Criteria_AreSearchedWithFindUiElementsRequestModel_IncludingOffscreenAndDisabledElements()
    {
        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Toggle, handle: Handle, name: "wrap", nameMatch: ElementNameMatch.Contains,
            automationId: "WrapBox", controlType: "CheckBox, Button", className: "Btn", value: "on", timeoutMs: 2000).Payload<ElementActionResult>();

        // Assert
        result.Success.Should().BeTrue();
        var query = _query!;
        (query.WindowHandle, query.WithinRef, query.Name, query.NameMatch, query.AutomationId, query.ClassName)
            .Should().Be((Handle, (string?)null, "wrap", ElementNameMatch.Contains, "WrapBox", "Btn"));
        query.ControlTypes.Should().Equal("CheckBox", "Button");
        (query.VisibleOnly, query.EnabledOnly, query.MaxResults, query.Timeout).Should().Be((false, false, 2, TimeSpan.FromSeconds(2)));
        _request!.Ref.Should().Be("w1e7");
        _request.Value.Should().Be("on");
        _request.Timeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Criteria_ThatMatchSeveralElements_AreAmbiguous_AndNothingIsDone()
    {
        _found = Found(3, Element("w1e7"), Element("w1e8"));

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Delete").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("AmbiguousElement");
        result.Error.Should().Contain("3 elements").And.Contain("w1e7").And.Contain("w1e8");
        _actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Criteria_ThatMatchNothing_AreElementNotFound()
    {
        _found = Found(0);

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, automationId: "Missing").Payload<ElementActionResult>();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ElementNotFound");
    }

    [Fact]
    public async Task ASearchThatDidNotCoverTheWindow_CannotProveTheMatchIsTheOnlyOne()
    {
        _found = Found(1, Element("w1e7")) with { Truncated = true, TruncationReason = ElementSearchTruncation.Timeout };

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Save").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("OperationTimedOut");
        _actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASearchThatDidNotCoverTheWindow_AndLeftOutAMatchForThePolicy_SaysBoth()
    {
        // Arrange - the search timed out, and had left out a match of an app the target policy refuses
        const string leftOut = "1 element of window 33024 belongs to process 777 ('KeePass'), ...";
        _found = Found(0) with
        {
            Truncated = true, TruncationReason = ElementSearchTruncation.Timeout,
            PolicyLeftOut = new PolicyLeftOutMatches(1, leftOut, OtherwiseComplete: false)
        };

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Save").Payload<ElementActionResult>();

        // Assert - the search may not have reached the element yet: more time can still help
        result.ErrorCode.Should().Be("OperationTimedOut");
        result.Error.Should().EndWith(leftOut);
        _actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASearchWhoseOnlyMatchesThePolicyLeftOut_IsTargetDeniedByPolicy()
    {
        const string leftOut = "2 elements of window 33024 belong to process 777 ('KeePass'), ...";
        _found = Found(0) with
        {
            Truncated = true, TruncationReason = ElementSearchTruncation.PartsSkipped,
            PolicyLeftOut = new PolicyLeftOutMatches(2, leftOut, OtherwiseComplete: true)
        };

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Save").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        result.Error.Should().StartWith("The criteria match only 2 elements of an app the server's target policy refuses, so nothing was done.")
            .And.EndWith(leftOut);
        _actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASearchThatDidNotCoverTheWindowAndFoundNothing_IsOperationTimedOut_NotElementNotFound()
    {
        _found = Found(0) with { Truncated = true, TruncationReason = ElementSearchTruncation.SearchLimit };

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Save").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("OperationTimedOut");
        result.Error.Should().Contain("0 found so far");
    }

    [Fact]
    public async Task ASearchThatDidNotCoverTheWindowButFoundSeveral_IsAmbiguous_AsReadElementTextReportsIt()
    {
        // More search cannot make two matches one
        _found = Found(2, Element("w1e7"), Element("w1e8")) with { TruncationReason = ElementSearchTruncation.Timeout };

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Delete").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("AmbiguousElement");
        result.Error.Should().Contain("At least 2 elements").And.Contain("w1e7").And.Contain("w1e8");
        _actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASearchThatFails_ReportsItsCode()
    {
        _found = new FindElementsResult(false, Handle, null, 0, false, null, 1, Error: "The window is not responding.",
            ErrorCode: UIElementErrorCode.NotResponding);

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Save").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("NotResponding");
    }

    [Fact]
    public async Task RefAndCriteria_AreRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Invoke, @ref: "w1e7", name: "Save"));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task NeitherRefNorCriteria_AreRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle));

        error.Message.Should().Contain("pass ref");
    }

    [Fact]
    public async Task AStringThatIsNoRef_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Invoke, @ref: "Save button"));

        error.Message.Should().Contain("not an element ref");
    }

    [Fact]
    public async Task ArgumentsThatDoNotFitTheAction_AreRefusedBeforeTheSearch()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.SetValue, handle: Handle, name: "Name"));

        error.Message.Should().Contain("set_value needs value");
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AllowHostTarget_WithASelectorByName_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Invoke, window: new WindowSelector { ProcessName = "pwsh" },
            name: "Run", allowHostTarget: true));

        error.Message.Should().Contain("allowHostTarget");
    }

    [Fact]
    public async Task AllowHostTarget_WithARef_IsPassedOn()
    {
        await CreateSut().ElementAction(ElementActionKind.Invoke, @ref: "w1e7", allowHostTarget: true);

        _request!.AllowHostTarget.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ElementSearchScope.Window)]
    [InlineData(ElementSearchScope.WindowAndPopups)]
    [InlineData(ElementSearchScope.Desktop)]
    public async Task TheScope_ReachesTheSearch_OfTheCriteria(ElementSearchScope? scope)
    {
        await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Delete", controlType: "MenuItem", scope: scope);

        _query!.Scope.Should().Be(scope, "find_ui_elements' search runs with the scope asked for (roadmap M3.5)");
        _request!.Ref.Should().Be("w1e7");
    }

    [Fact]
    public async Task AScope_WithARef_IsRefused_BeforeAnythingIsDone()
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Invoke, @ref: "w1e7", scope: ElementSearchScope.Window));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("not to a ref").And.EndWith("Nothing was done.");
        _actions.VerifyNoOtherCalls();
        _search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ASearchThatLeftWindowsOfItsScopeOut_IsNotProofOfOneMatch_AndNothingIsDone()
    {
        // More popups were open than one search covers (or, for desktop, more windows): another match may be in the rest
        _found = Found(1, Element("w1e7")) with
        {
            Scope = ElementSearchScope.WindowAndPopups, TruncationReason = ElementSearchTruncation.WindowLimit
        };

        var result = await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Delete").Payload<ElementActionResult>();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.OperationTimedOut));
        result.Error.Should().Contain("every window of its scope").And.Contain("WindowLimit");
        _actions.VerifyNoOtherCalls();
    }

    #region observe (roadmap M3.12)

    private readonly Mock<IDesktopStateService> _desktopState = new();
    private readonly Mock<IWindowService> _windows = new();

    private ElementActionTools CreateObservingSut()
    {
        _windows.Setup(w => w.IsValidWindow(Handle)).Returns(true);
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopStateRequest request, CancellationToken _) => new DesktopStateSnapshot(
                new DesktopState(true, request.Window ?? 0x77, "after", null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                    null, null, null, null, null, null, 9),
                request.Include.Contains(DesktopStateSection.Screenshot) ? new EncodedImage([1, 2, 3], "image/png") : null));
        return new ElementActionTools(_actions.Object, _search.Object, _registry.Object, _windows.Object, _desktopState.Object,
            _menuPaths.Object);
    }

    [Fact]
    public async Task WithoutObserve_TheResultIsTheActionsAlone()
    {
        var result = await CreateObservingSut().ElementAction(ElementActionKind.Invoke, @ref: "w1e7");

        result.Content.Should().ContainSingle();
        result.Payload<ElementActionResult>().Observed.Should().BeNull();
        _desktopState.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ObserveState_AddsTheObservationOfTheElementsWindow_AfterTheAction()
    {
        // Arrange - the observation is read only once the action returned
        var order = new List<string>();
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("act"))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                new ElementActionResult(true, "invoke", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Invoke));
        var sut = CreateObservingSut();
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => order.Add($"observe {request.Window}"))
            .ReturnsAsync(new DesktopStateSnapshot(new DesktopState(true, Handle, "after", null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                null, null, null, null, null, null, 9), null));

        // Act
        var result = await sut.ElementAction(ElementActionKind.Invoke, @ref: "w1e7", observe: ObserveMode.State);

        // Assert
        order.Should().Equal("act", $"observe {Handle}");
        var payload = result.Payload<ElementActionResult>();
        payload.Success.Should().BeTrue();
        payload.Observed!.WindowHandle.Should().Be(Handle);
        result.Content.Should().ContainSingle("no capture was asked for");
    }

    [Fact]
    public async Task ObserveState_ReadsTheWindowInFront_OnceTheElementsWindowClosed()
    {
        var sut = CreateObservingSut();
        _windows.Setup(w => w.IsValidWindow(Handle)).Returns(false);
        DesktopStateRequest? observed = null;
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => observed = request)
            .ReturnsAsync(new DesktopStateSnapshot(new DesktopState(true, 0x77, null, null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                null, null, null, null, null, null, 9), null));

        await sut.ElementAction(ElementActionKind.Invoke, @ref: "w1e7", observe: ObserveMode.State);

        observed!.Window.Should().BeNull("the dialog the action closed is gone; the window in front is what the agent sees next");
        observed.Include.Should().BeEquivalentTo(DesktopStateRequest.DefaultInclude);
    }

    [Fact]
    public async Task ObserveScreenshot_PutsTheCaptureFirst()
    {
        var result = await CreateObservingSut().ElementAction(ElementActionKind.Invoke, @ref: "w1e7", observe: ObserveMode.Screenshot);

        result.Content.Should().HaveCount(2);
        result.Content[0].Should().BeOfType<ModelContextProtocol.Protocol.ImageContentBlock>();
        result.Payload<ElementActionResult>().Observed.Should().NotBeNull();
    }

    [Fact]
    public async Task AFailedAction_IsStillObserved_AndStaysAnError()
    {
        _found = Found(0);

        var result = await CreateObservingSut().ElementAction(ElementActionKind.Invoke, handle: Handle, automationId: "Missing",
            observe: ObserveMode.State);

        result.IsError.Should().BeTrue();
        var payload = result.Payload<ElementActionResult>();
        payload.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        payload.Observed.Should().NotBeNull("what is there helps after ElementNotFound");
    }

    [Theory]
    [InlineData(nameof(InputErrorCode.TargetDeniedByPolicy))]
    [InlineData(nameof(InputErrorCode.TargetIsAgentHost))]
    public async Task AnActionRefusedForItsWindow_DoesNotObserveThatWindowByName(string refusal)
    {
        // The agent asked for an action, not a read of a denied app or its own terminal: the default rules apply
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementActionResult(false, "invoke", "w1e7", Handle, Error: "Refused.", ErrorCode: refusal));
        var sut = CreateObservingSut();
        DesktopStateRequest? observed = null;
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => observed = request)
            .ReturnsAsync(new DesktopStateSnapshot(new DesktopState(true, 0, null, null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                null, null, null, null, null, null, 9), null));

        var result = await sut.ElementAction(ElementActionKind.Invoke, @ref: "w1e7", observe: ObserveMode.State);

        result.IsError.Should().BeTrue();
        observed!.Window.Should().BeNull("the window the action was refused for is not read by name");
    }

    [Fact]
    public async Task ObserveScreenshot_IsRefusedBeforeAnythingIsDone_WhenCapturesAreOff()
    {
        var sut = CreateObservingSut();
        _desktopState.Setup(s => s.ScreenshotUnavailable()).Returns("A screenshot needs capture_window.");

        var error = await RefusedAsync(() => sut.ElementAction(ElementActionKind.Invoke, @ref: "w1e7", observe: ObserveMode.Screenshot));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("capture_window").And.EndWith("Nothing was done.");
        _actions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ObserveMode.State)]
    [InlineData(ObserveMode.Screenshot)]
    public async Task Observe_IsRefusedBeforeAnythingIsDone_WhenGetDesktopStateIsNotServed(ObserveMode observe)
    {
        // Arrange - WAM_TOOLSETS=-elements: the observation is get_desktop_state's read, which the user turned off
        var sut = CreateObservingSut();
        _desktopState.Setup(s => s.StateUnavailable()).Returns("The observation needs get_desktop_state, which this server does not serve.");

        // Act - criteria that match nothing would make the call a pure read
        var error = await RefusedAsync(() => sut.ElementAction(ElementActionKind.Invoke, window: new WindowSelector { ProcessName = "notepad" },
            name: "zzz", observe: observe));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("get_desktop_state").And.EndWith("Nothing was done.");
        _search.VerifyNoOtherCalls();
        _actions.VerifyNoOtherCalls();
        _desktopState.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    [Fact]
    public async Task MenuPath_FromAWindowAlone_StartsInTheWindow_WithTheScope()
    {
        SetUpMenuPaths();

        var result = await CreateSut().ElementAction(ElementActionKind.MenuPath, handle: Handle, value: "File > Save As",
            scope: ElementSearchScope.WindowAndPopups, timeoutMs: 3000).Payload<ElementActionResult>();

        result.Success.Should().BeTrue();
        _menuRequest.Should().BeEquivalentTo(new MenuPathRequest(Handle, null, ["File", "Save As"], ElementSearchScope.WindowAndPopups,
            false, TimeSpan.FromSeconds(3)));
        _search.VerifyNoOtherCalls();
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MenuPath_FromARef_StartsBelowItsElement_InItsWindow()
    {
        SetUpMenuPaths();

        await CreateSut().ElementAction(ElementActionKind.MenuPath, @ref: "w1e30", value: "File>Save As");

        (_menuRequest!.Window, _menuRequest.StartRef).Should().Be((Handle, "w1e30"));
    }

    [Fact]
    public async Task MenuPath_FromAStaleRef_ReportsTheRegistrysError_AndOpensNothing()
    {
        SetUpMenuPaths();
        _registry.Setup(r => r.Lookup("w1e30")).Returns(new ElementLookup("w1e30", 0, null, 0, UIElementErrorCode.ElementStale, "Its window closed."));

        var result = await CreateSut().ElementAction(ElementActionKind.MenuPath, @ref: "w1e30", value: "File>Save As").Payload<ElementActionResult>();

        (result.Success, result.Action, result.ErrorCode).Should().Be((false, "menu_path", "ElementStale"));
        _menuPaths.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MenuPath_FromCriteria_StartsBelowTheElementTheyFind()
    {
        SetUpMenuPaths();

        await CreateSut().ElementAction(ElementActionKind.MenuPath, handle: Handle, controlType: "MenuBar", value: "File>Save As");

        _query!.ControlTypes.Should().Equal("MenuBar");
        _menuRequest!.StartRef.Should().Be("w1e7");
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MenuPath_WithoutAPath_IsRefusedBeforeAnythingIsSearched()
    {
        SetUpMenuPaths();

        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.MenuPath, handle: Handle));

        error.Message.Should().Contain("menu_path needs value");
        _menuPaths.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MenuPath_WithFallbackMouse_IsRefused_NotIgnored()
    {
        SetUpMenuPaths();

        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.MenuPath, handle: Handle, value: "File>Save As",
            fallback: ElementActionFallback.Mouse));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("fallback mouse");
        _menuPaths.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MenuPath_WithObserveState_IsObserved_AfterThePathRan()
    {
        // Arrange - observe (roadmap M3.12) wraps menu_path (roadmap M3.5) as it wraps every other action
        var order = new List<string>();
        _menuPaths.Setup(m => m.RunAsync(It.IsAny<MenuPathRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("menu"))
            .ReturnsAsync(new ElementActionResult(true, "menu_path", "w2e1", Handle, ElementActionStatus.Done, ElementActionMethod.Invoke));
        var sut = CreateObservingSut();
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => order.Add($"observe {request.Window}"))
            .ReturnsAsync(new DesktopStateSnapshot(new DesktopState(true, Handle, "after", null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                null, null, null, null, null, null, 9), null));

        // Act
        var result = await sut.ElementAction(ElementActionKind.MenuPath, handle: Handle, value: "File>Save As", observe: ObserveMode.State);

        // Assert
        order.Should().Equal("menu", $"observe {Handle}");
        var payload = result.Payload<ElementActionResult>();
        (payload.Success, payload.Action).Should().Be((true, "menu_path"));
        payload.Observed!.WindowHandle.Should().Be(Handle);
    }
}
