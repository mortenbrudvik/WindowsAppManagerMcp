using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: every tool on <see cref="InputGateToolFilter.SelfGatedTools"/> really does its window, process, page or launch work
/// inside the input gate. The filter lets these tools run without the gate (they ask the user, resolve a selector or find a tab
/// first), so a tool that is put on the list and never takes the gate itself would change windows between an input call's
/// checks and its input, and the list check of <see cref="InputGateToolFilterTests"/> would not see it. The list is read from
/// the filter, and every tool on it needs an entry here that says how its gated work is seen:
/// <list type="bullet">
/// <item><see cref="Work"/>: the tool's typical call (EveryToolContractTests' arguments) through the production pipeline, with
/// its services' mutating calls watched (<see cref="GateObserver"/>): each must run while this test's call holds the gate. A
/// service the tool hands a gate function to (a debug browser's close, a tab's or a page's CDP commands) is watched by
/// entering that function: it must take the gate.</item>
/// <item><see cref="Through"/>: tools whose every action goes through element_action's service, which takes the gate for each
/// action (ElementActionServiceTests checks it does). The tool's call must reach the service, and the production
/// registrations must lead there: each service of the chain is registered with the type that calls the next one and makes no
/// native action of its own (no window change, input, clipboard write or UI Automation action: NativeFakedServer.IsAction),
/// and the last one takes the gate (read from their IL).</item>
/// </list>
/// The services are the contract run's mocks (ContractServices); nothing touches the desktop, a browser or a process.
/// </summary>
public sealed class SelfGatedToolsContractTests : IDisposable
{
    private readonly ContractServices _services = new(ContractMode.Typical);

    public void Dispose() => _services.Dispose();

    /// <summary>How a tool's gated work is set up to be seen: its services, or the real service over CDP mocks, watched.</summary>
    /// <param name="Arrange">Replaces the tool's services with watched ones, after the contract run registered its mocks.</param>
    /// <param name="Arguments">The call's arguments; null for the tool's typical call (EveryToolContractTests).</param>
    public sealed record GatedWork(
        Action<ContractServices, IServiceCollection, GateObserver> Arrange,
        IReadOnlyDictionary<string, object?>? Arguments = null);

    /// <summary>A tool that acts only through a chain of services whose last one takes the gate for every action.</summary>
    /// <param name="Service">The service the tool calls.</param>
    /// <param name="Method">Its method the tool's typical call must reach.</param>
    /// <param name="Chain">Each service of the chain with the type the server registers for it; each calls the next one's
    /// interface, and the last one takes the gate.</param>
    public sealed record GatedThrough(Type Service, string Method, IReadOnlyList<(Type Service, Type Implementation)> Chain);

    /// <summary>The tools whose services' mutating calls are watched in a typical call.</summary>
    private static readonly IReadOnlyDictionary<string, GatedWork> Work = new Dictionary<string, GatedWork>(StringComparer.Ordinal)
    {
        // Asks the user first when the client can ask (typical: confirm=true and no elicitation), then kills inside the gate
        ["kill_process"] = Watch<IProcessService>(c => c.Processes.Object, nameof(IProcessService.KillProcessAsync)),
        ["launch_application"] = Watch<IProcessService>(c => c.Processes.Object, nameof(IProcessService.LaunchApplicationAsync)),
        ["close_window"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.CloseWindow)),
        ["close_windows_batch"] = Watch<IWindowService>(c => c.Windows.Object,
            nameof(IWindowService.CloseWindowsBatchAsync), nameof(IWindowService.CloseWindow)),
        ["close_application"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.CloseWindow)),
        ["set_window_bounds"] = Watch<IWindowService>(c => c.Windows.Object,
            nameof(IWindowService.SetWindowFrame), nameof(IWindowService.SetWindowBounds), nameof(IWindowService.MoveWindow),
            nameof(IWindowService.ResizeWindow)),
        ["set_window_state"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SetWindowState)),
        ["focus_window"] = Watch<IForegroundService>(c => c.Foreground.Object, nameof(IForegroundService.EnsureForeground)),
        ["snap_window"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SnapWindow)),
        ["move_window_to_monitor"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.MoveWindowToMonitor)),
        ["set_window_zorder"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SetWindowZOrder)),
        ["set_windows_bounds_batch"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SetWindowBoundsBatchAsync)),
        ["snap_windows_batch"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SnapWindowsBatchAsync)),
        ["set_windows_state_batch"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.SetWindowStateBatchAsync)),
        // The arrangement, not the dry run a question may need first, which takes no gate (roadmap M6.8)
        ["apply_layout"] = Watch<ILayoutService>(c => c.Layouts.Object,
            new WatchedCall(nameof(ILayoutService.ApplyPresetAsync), args => args[3] is not LayoutApplyOptions { DryRun: true })),
        // A restore, not a dry run (roadmap M7.5)
        ["restore_workspace"] = Watch<IWorkspaceService>(c => c.Workspaces.Object,
            new WatchedCall(nameof(IWorkspaceService.RestoreAsync), args => args[1] is false)),
        // Browser.close and an end of a browser that did not exit run inside the gate entry the tool hands the lifecycle (roadmap M5.4)
        ["close_debug_browser"] = Watch<IDebugBrowserLifecycle>(c => c.DebugBrowsers.Object,
            new WatchedCall(nameof(IDebugBrowserLifecycle.CloseAsync), HandsOnTheGate: true)),
        // The run holds the gate for every step (roadmap M6.2): its focus, element action, typing and keys
        ["run_steps"] = new GatedWork((c, services, observer) =>
        {
            Replace(services, observer, c.Foreground.Object, new WatchedCall(nameof(IForegroundService.EnsureForeground)));
            Replace(services, observer, c.ElementActions.Object, new WatchedCall(nameof(IElementActionService.ActAsync)));
            Replace(services, observer, c.Input.Object,
                new WatchedCall(nameof(IInputSimulationService.TypeTextAsync)), new WatchedCall(nameof(IInputSimulationService.PressKeysAsync)));
        }),
        // The tab tools (roadmap M5.5) and the DOM bridge's actions (roadmap M5.8): the real services over a debug Chrome's CDP mocks
        ["activate_tab"] = BrowserTabs(nameof(ICdpTabService.ActivateAsync)),
        ["browser_navigate"] = BrowserTabs(nameof(ICdpTabService.NavigateAsync)),
        ["browser_click"] = BrowserPages(nameof(ICdpPageService.ClickAsync)),
        ["browser_type"] = BrowserPages(nameof(ICdpPageService.TypeAsync)),
        ["browser_evaluate"] = BrowserPages(nameof(ICdpPageService.EvaluateAsync)),
        // Takes the gate only to bring a tab that is not shown to the front: the gate it hands on must be the input gate
        ["browser_screenshot"] = BrowserPages(nameof(ICdpPageService.CaptureAsync)),
    };

    /// <summary>The tools that act only through element_action's service, which takes the gate for each action.</summary>
    private static readonly IReadOnlyDictionary<string, GatedThrough> Through = new Dictionary<string, GatedThrough>(StringComparer.Ordinal)
    {
        // Finds its element without the gate; the service takes it around the pattern call (roadmap M3.6)
        ["element_action"] = new(typeof(IElementActionService), nameof(IElementActionService.ActAsync),
            [(typeof(IElementActionService), typeof(ElementActionService))]),
        // Read the dialog without the gate; press and set through element_action's service (roadmap M3.5, M3.14)
        ["respond_to_dialog"] = new(typeof(IDialogService), nameof(IDialogService.RespondAsync),
            [(typeof(IDialogService), typeof(DialogService)), (typeof(IElementActionService), typeof(ElementActionService))]),
        ["file_dialog"] = new(typeof(IFileDialogService), nameof(IFileDialogService.CompleteAsync),
            [(typeof(IFileDialogService), typeof(FileDialogService)), (typeof(IElementActionService), typeof(ElementActionService))]),
        // Reads a JavaScript dialog without the gate; sets a prompt's text and presses its button through element_action's service
        ["browser_handle_dialog"] = new(typeof(IBrowserDialogService), nameof(IBrowserDialogService.HandleAsync),
            [(typeof(IBrowserDialogService), typeof(BrowserDialogService)), (typeof(IElementActionService), typeof(ElementActionService))]),
    };

    public static TheoryData<string> SelfGatedTools()
    {
        var data = new TheoryData<string>();
        foreach (var tool in InputGateToolFilter.SelfGatedTools.Order(StringComparer.Ordinal))
        {
            data.Add(tool);
        }

        return data;
    }

    [Fact]
    public void EverySelfGatedTool_SaysHowItsGatedWorkIsSeen_AndNoOtherToolDoes()
    {
        Work.Keys.Concat(Through.Keys).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(InputGateToolFilter.SelfGatedTools,
                "a tool that takes the input gate itself needs an entry that shows it does (Work or Through), and a tool that left " +
                "the list loses its entry");
    }

    [Theory]
    [MemberData(nameof(SelfGatedTools))]
    public async Task ASelfGatedTool_DoesItsWorkInsideTheInputGate(string tool)
    {
        if (Through.TryGetValue(tool, out var through))
        {
            await AssertActsOnlyThroughTheGatedService(tool, through);
            return;
        }

        Work.TryGetValue(tool, out var work).Should().BeTrue(
            $"'{tool}' takes the input gate itself (InputGateToolFilter.SelfGatedTools): add an entry to {nameof(SelfGatedToolsContractTests)}.{nameof(Work)} " +
            "that watches its mutating service calls, or to Through when a gated service makes them");

        // Arrange - the server's calls run under this test's tag, so the observer sees this test's hold of the gate, not another's
        var flow = InputGate.TagFlow();
        var observer = new GateObserver(flow);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services =>
            {
                _services.Register(services);
                work!.Arrange(_services, services, observer);
            });

        // Act
        var result = await server.CallToolAsync(tool, work!.Arguments ?? EveryToolContractTests.ArgumentsFor(tool));

        // Assert
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        observer.Seen.Should().NotBeEmpty("the typical call of '{0}' must reach the work it gates: {1}", tool, text);
        observer.Seen.Where(o => !o.GateHeld).Select(o => o.Call).Should().BeEmpty(
            "'{0}' is on SelfGatedTools, so the filter does not hold the gate for it: it must hold the input gate itself while it acts", tool);
        InputGate.IsHeldBy(flow).Should().BeFalse("'{0}' releases the gate when it is done", tool);
    }

    /// <summary>
    /// The typical call reaches <see cref="GatedThrough.Service"/>, and the production registrations lead from it to a service
    /// that takes the gate: each service is registered with the expected type, which calls the next service's interface, and the
    /// last type takes the gate.
    /// </summary>
    private async Task AssertActsOnlyThroughTheGatedService(string tool, GatedThrough through)
    {
        // Arrange - the call is seen, with or without the gate (the gate is the mocked service's job)
        var observer = new GateObserver(InputGate.TagFlow());
        var mock = through.Service switch
        {
            _ when through.Service == typeof(IElementActionService) => (object)_services.ElementActions.Object,
            _ when through.Service == typeof(IDialogService) => _services.Dialogs.Object,
            _ when through.Service == typeof(IFileDialogService) => _services.FileDialogs.Object,
            _ when through.Service == typeof(IBrowserDialogService) => _services.BrowserDialogs.Object,
            _ => throw new InvalidOperationException($"No contract mock for {through.Service.Name}.")
        };
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services =>
            {
                _services.Register(services);
                services.Replace(ServiceDescriptor.Singleton(through.Service, observer.Watch(through.Service, mock, [new WatchedCall(through.Method)])));
            });

        // Act
        await server.CallToolAsync(tool, EveryToolContractTests.ArgumentsFor(tool));

        // Assert
        observer.Seen.Should().NotBeEmpty("'{0}' acts through {1}.{2}", tool, through.Service.Name, through.Method);
        through.Chain[0].Service.Should().Be(through.Service);
        var registrations = ProductionRegistrations();
        for (var i = 0; i < through.Chain.Count; i++)
        {
            var (service, implementation) = through.Chain[i];
            registrations.Should().ContainKey(service).WhoseValue.Should().Be(implementation,
                "the server registers {0} for {1}", implementation.Name, service.Name);
            var calls = IlCalls.CallsMadeBy(implementation).ToList();
            if (i < through.Chain.Count - 1)
            {
                var next = through.Chain[i + 1].Service;
                calls.Should().Contain(m => m.DeclaringType == next, "{0} acts through {1}", implementation.Name, next.Name);
                NativeActions(calls).Should().BeEmpty(
                    "{0} acts only through {1}, which takes the input gate: a native action of its own would run outside the gate",
                    implementation.Name, through.Chain[^1].Implementation.Name);
            }
            else
            {
                calls.Any(TakesTheGate).Should().BeTrue("{0} takes the input gate around its actions (ElementActionServiceTests)", implementation.Name);
            }
        }
    }

    [Fact]
    public void TheChainCheck_SeesANativeActionInAServicesCode()
    {
        // Act - a sample's IL, as the check reads a chain service's
        var actions = NativeActions(IlCalls.CallsMadeBy(typeof(NativeActionSample))).ToList();

        // Assert - a window change, a UI Automation action and a pattern action in a lambda, input and a clipboard write
        actions.Should().Contain([
            "INativeWindowWrapper.SetForegroundWindow", "IUIAutomationWrapper.CloseWindow", "IUiaTree.SetFocus", "IUiaInvokePattern.Invoke",
            "IUiaValuePattern.SetValue", "INativeInputWrapper.SendInput", "INativeClipboardWrapper.WriteText"
        ]);
        actions.Should().NotContain(["IUiaTree.GetPattern", "IUiaValuePattern.get_Value", "IUiaTextPattern.GetText", "INativeInputWrapper.GetCursorPos"],
            "reads are no actions");
    }

    /// <summary>The calls among <paramref name="calls"/> that act on a native seam (NativeFakedServer.IsAction), as "Interface.Member".</summary>
    private static IEnumerable<string> NativeActions(IEnumerable<System.Reflection.MethodBase> calls) =>
        calls.OfType<System.Reflection.MethodInfo>()
            .Where(m => m.DeclaringType != null && NativeFakedServer.IsAction(m.DeclaringType, m))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Distinct();

    /// <summary>
    /// A sample for <see cref="TheChainCheck_SeesANativeActionInAServicesCode"/>, which only scans its IL. Nothing calls it, and
    /// its seams would be mocks if anything did.
    /// </summary>
    private static class NativeActionSample
    {
        public static void Acts(INativeWindowWrapper windows, IUIAutomationWrapper automation, IUiaTree tree, IUiaNode node,
            INativeInputWrapper input, INativeClipboardWrapper clipboard)
        {
            windows.SetForegroundWindow(0x10010);
            automation.CloseWindow(0x10010, CancellationToken.None);
            tree.SetFocus(node);
            Action press = () => tree.GetPattern<IUiaInvokePattern>(node)?.Invoke();
            press();
            if (tree.GetPattern<IUiaValuePattern>(node) is { Value: not null } value && tree.GetPattern<IUiaTextPattern>(node)?.GetText(10) != null)
            {
                value.SetValue("sample");
            }

            _ = input.GetCursorPos();
            input.SendInput([]);
            clipboard.WriteText("sample");
        }
    }

    /// <summary>A call that takes the input gate: the filter's EnterAsync or HoldAsync, or one of InputGate's ways in.</summary>
    private static bool TakesTheGate(System.Reflection.MethodBase method) =>
        (method.DeclaringType == typeof(InputGateToolFilter) && method.Name is nameof(InputGateToolFilter.EnterAsync) or nameof(InputGateToolFilter.HoldAsync)) ||
        (method.DeclaringType == typeof(InputGate) && method.Name is nameof(InputGate.Enter) or nameof(InputGate.EnterAsync) or nameof(InputGate.TryEnter)
            or nameof(InputGate.TryEnterAsync) or nameof(InputGate.TryHoldAsync));

    /// <summary>The service types the server registers with an implementation type, and that type (no service is created).</summary>
    private static Dictionary<Type, Type> ProductionRegistrations() =>
        new ServiceCollection()
            .AddWindowsAppManagerServices(() => throw new InvalidOperationException("The registrations are only read."))
            .Where(d => !d.IsKeyedService && d.ImplementationType != null)
            .GroupBy(d => d.ServiceType)
            .ToDictionary(g => g.Key, g => g.Last().ImplementationType!);

    // ---- entries ----

    private static GatedWork Watch<T>(Func<ContractServices, T> inner, params string[] methods) where T : class =>
        Watch(inner, methods.Select(m => new WatchedCall(m)).ToArray());

    private static GatedWork Watch<T>(Func<ContractServices, T> inner, params WatchedCall[] calls) where T : class =>
        new((c, services, observer) => Replace(services, observer, inner(c), calls));

    private static void Replace<T>(IServiceCollection services, GateObserver observer, T inner, params WatchedCall[] calls) where T : class =>
        services.Replace(ServiceDescriptor.Singleton(typeof(T), observer.Watch(inner, calls)));

    private static GatedWork BrowserTabs(string method) => new((c, services, observer) =>
    {
        var browser = new DebugChrome();
        var tabs = observer.Watch(browser.Tabs.Object, new WatchedCall(method, HandsOnTheGate: true));
        services.Replace(ServiceDescriptor.Singleton<IBrowserTabService>(new BrowserTabService(
            browser.Windows.Object, new BrowserDetectionService(), browser.Reading.Object, browser.Cdp.Object, tabs, browser.Owners,
            new TargetPolicy(TargetPolicySettings.Default), browser.Locator.Object, c.Session)));
    });

    private static GatedWork BrowserPages(string method) => new((c, services, observer) =>
    {
        var browser = new DebugChrome();
        var pages = observer.Watch(browser.Pages.Object, new WatchedCall(method, HandsOnTheGate: true));
        services.Replace(ServiceDescriptor.Singleton<IBrowserPageService>(new BrowserPageService(
            browser.Windows.Object, new BrowserDetectionService(), browser.Reading.Object, browser.Cdp.Object, pages, browser.Owners,
            Mock.Of<IBrowserViewportLocator>(), new TargetPolicy(TargetPolicySettings.Default), browser.Locator.Object, c.Session)));
    });

    /// <summary>
    /// The typical desktop's Chrome window (TypicalDesktop.BrowserHandle) as the browser services resolve it: a Chrome process with
    /// its own DevTools port 9222, a shown tab, and CDP commands that succeed. Nothing reaches a browser.
    /// </summary>
    private sealed class DebugChrome
    {
        private const int Port = 9222;

        public DebugChrome()
        {
            var window = TypicalDesktop.Windows.Single(w => w.Handle == (nint)TypicalDesktop.BrowserHandle);
            var handle = window.Handle;
            var processId = (uint)window.ProcessId;
            var shown = new CdpTarget("8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F", "page", "Windows App Manager - GitHub",
                "https://github.com/mortenbrudvik/WindowsAppManagerMcp", $"ws://127.0.0.1:{Port}/devtools/page/8F4C1D2E9A7B6C5D4E3F2A1B0C9D8E7F", Port);
            Owners = new FakePortOwnerResolver(owner: (int)processId);

            Windows.Setup(w => w.IsWindow(handle)).Returns(true);
            Windows.Setup(w => w.GetAncestor(handle, WindowAncestor.Root)).Returns(handle);
            Windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(processId);
            Windows.Setup(w => w.GetProcessName(processId)).Returns(window.ProcessName);
            Windows.Setup(w => w.GetWindowText(handle)).Returns(window.Title);
            Windows.Setup(w => w.GetClassName(handle)).Returns("Chrome_WidgetWin_1");
            Windows.Setup(w => w.GetWindowRect(handle)).Returns((1280, 0, 2560, 1392));
            Windows.Setup(w => w.GetDpiForWindow(handle)).Returns(96u);
            Reading.Setup(r => r.GetBrowserType(It.IsAny<nint>(), It.IsAny<string>())).Returns("chrome");
            Locator.Setup(l => l.Find((int)processId)).Returns(new DebugPortLookup(DebugPortLookupStatus.Found, Port, "found"));
            Cdp.Setup(c => c.FindTargetForWindowAsync(Port, It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CdpResult<CdpTarget>.Ok(shown));
            Cdp.Setup(c => c.FindTargetByIdAsync(Port, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, int _, string id, TimeSpan _, CancellationToken _) => CdpResult<CdpTarget>.Ok(shown with { Id = id }));

            Tabs.Setup(t => t.ActivateAsync(It.IsAny<CdpTarget>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CdpTarget target, CdpGate _, TimeSpan _, CancellationToken _) => CdpResult<CdpTarget>.Ok(target));
            Tabs.Setup(t => t.NavigateAsync(It.IsAny<CdpTarget>(), It.IsAny<string>(), It.IsAny<BrowserWaitUntil>(), It.IsAny<CdpGate>(),
                    It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
                .ReturnsAsync(CdpResult<CdpNavigation>.Ok(new CdpNavigation("https://example.com/docs/", "Docs - Example", 200, "load", false)));
            Pages.Setup(p => p.EvaluateAsync(It.IsAny<CdpTarget>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CdpPageResult<CdpEvaluation>.Ok(new CdpEvaluation(JsonSerializer.SerializeToElement(42), "number")));
            Pages.Setup(p => p.ClickAsync(It.IsAny<CdpTarget>(), It.IsAny<BrowserElementLocator>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                    It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
                .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("button", "Save draft"), 1)));
            Pages.Setup(p => p.TypeAsync(It.IsAny<CdpTarget>(), It.IsAny<BrowserTypeOptions>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
                .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("input"), 1, TypedLength: 21)));
            Pages.Setup(p => p.CaptureAsync(It.IsAny<CdpTarget>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CdpPageResult<CdpScreenshot>.Fail(BrowserErrorCode.CdpTimeout, "The tab produced no frame to capture."));
        }

        public Mock<INativeWindowWrapper> Windows { get; } = new();

        public Mock<IBrowserReadingService> Reading { get; } = new();

        public Mock<ICdpService> Cdp { get; } = new();

        public Mock<ICdpTabService> Tabs { get; } = new();

        public Mock<ICdpPageService> Pages { get; } = new();

        public Mock<IDebugPortLocator> Locator { get; } = new();

        public FakePortOwnerResolver Owners { get; }
    }
}
