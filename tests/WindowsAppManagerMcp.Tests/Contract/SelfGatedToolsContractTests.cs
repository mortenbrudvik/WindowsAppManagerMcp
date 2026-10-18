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
/// <item><see cref="Work"/>: the tool's typical call (EveryToolContractTests' arguments, or a call of the entry's own that
/// reaches more of its work) through the production pipeline, with every call that may change the desktop watched
/// (<see cref="GateObserver.WatchEveryChange"/>) on every service of <see cref="NoDesktopChange"/>: each must run while this
/// test's call holds the gate, and the call must reach the work the entry names. A service the tool hands a gate function to
/// (a debug browser's close, a tab's or a page's CDP commands) is watched by entering that function: it must take the
/// gate.</item>
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
    /// <param name="Arrange">Replaces the tool's services with watched ones, after the contract run registered its mocks and every
    /// service of <see cref="NoDesktopChange"/> was watched.</param>
    /// <param name="Reaches">The watched calls ("Interface.Method") the call must reach: one of them, or all with
    /// <paramref name="ReachesAll"/>.</param>
    /// <param name="Arguments">The call's arguments; null for the tool's typical call (EveryToolContractTests).</param>
    /// <param name="ReachesAll">The call must reach every call of <paramref name="Reaches"/>.</param>
    public sealed record GatedWork(
        Action<ContractServices, IServiceCollection, GateObserver> Arrange,
        IReadOnlyList<string> Reaches,
        IReadOnlyDictionary<string, object?>? Arguments = null,
        bool ReachesAll = false);

    /// <summary>A tool that acts only through a chain of services whose last one takes the gate for every action.</summary>
    /// <param name="Service">The service the tool calls.</param>
    /// <param name="Method">Its method the tool's typical call must reach.</param>
    /// <param name="Chain">Each service of the chain with the type the server registers for it; each calls the next one's
    /// interface, and the last one takes the gate.</param>
    public sealed record GatedThrough(Type Service, string Method, IReadOnlyList<(Type Service, Type Implementation)> Chain);

    /// <summary>
    /// The members of the contract run's services that change nothing on the desktop: reads, checks, waits, a question's answer
    /// kept, the server's own files. Every other member is watched for every tool (<see cref="GateObserver.WatchEveryChange"/>),
    /// so a member added to one of these services is watched until it is listed here.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> NoDesktopChange = new()
    {
        [typeof(IWindowService)] = new(StringComparer.Ordinal)
        {
            nameof(IWindowService.GetAllWindows), nameof(IWindowService.FindWindows), nameof(IWindowService.ResolveWindow),
            nameof(IWindowService.GetWindowDetails), nameof(IWindowService.GetForegroundWindow), nameof(IWindowService.GetWindowInfo),
            nameof(IWindowService.IsUserFacingAppWindow), nameof(IWindowService.WaitForSettledFrame), nameof(IWindowService.GetNormalBounds),
            nameof(IWindowService.IsValidWindow), nameof(IWindowService.EvaluateTargetPolicy), nameof(IWindowService.CheckClose),
            nameof(IWindowService.GetOwnedWindows), nameof(IWindowService.FindBlockingDialog), nameof(IWindowService.GetApplicationWindows),
            nameof(IWindowService.GetApplicationWindowsOnOtherDesktops)
        },
        [typeof(IForegroundService)] = new(StringComparer.Ordinal) { nameof(IForegroundService.IsInFront) },
        [typeof(IProcessService)] = new(StringComparer.Ordinal)
        {
            nameof(IProcessService.GetRunningProcesses), nameof(IProcessService.GetProcessName), nameof(IProcessService.GetProcessPath),
            nameof(IProcessService.CheckKillTarget), nameof(IProcessService.PreviewElevation), nameof(IProcessService.CheckLaunch)
        },
        [typeof(IInputSimulationService)] = new(StringComparer.Ordinal)
        {
            nameof(IInputSimulationService.GetCursorPosition), nameof(IInputSimulationService.IsValidScreenCoordinate)
        },
        [typeof(IElementActionService)] = new(StringComparer.Ordinal),
        [typeof(ILayoutService)] = new(StringComparer.Ordinal)
        {
            nameof(ILayoutService.GetStorageError), nameof(ILayoutService.GetAllPresets), nameof(ILayoutService.GetPreset),
            nameof(ILayoutService.GetInvalidFiles), nameof(ILayoutService.AreLaunchesConfirmed), nameof(ILayoutService.ConfirmLaunches),
            nameof(ILayoutService.SavePresetAsync), nameof(ILayoutService.DeletePresetAsync), nameof(ILayoutService.CaptureCurrentLayoutAsync),
            nameof(ILayoutService.SaveAuthoredLayoutAsync)
        },
        [typeof(IWorkspaceService)] = new(StringComparer.Ordinal) { nameof(IWorkspaceService.SaveAsync) },
        [typeof(IDebugBrowserLifecycle)] = new(StringComparer.Ordinal) { nameof(IDebugBrowserLifecycle.CheckClose) },
        [typeof(IWindowCloseVerifier)] = new(StringComparer.Ordinal)
        {
            nameof(IWindowCloseVerifier.ShownWindows), nameof(IWindowCloseVerifier.WaitForCloseAsync), nameof(IWindowCloseVerifier.FindPromptAsync),
            nameof(IWindowCloseVerifier.WaitForProcessesToExitAsync)
        },
        [typeof(ILaunchPlacementService)] = new(StringComparer.Ordinal),
        [typeof(ICdpTabService)] = new(StringComparer.Ordinal) { nameof(ICdpTabService.ListTabsAsync), nameof(ICdpTabService.WaitAsync) },
        [typeof(ICdpPageService)] = new(StringComparer.Ordinal) { nameof(ICdpPageService.LocateAsync) },
    };

    /// <summary>The contract run's mocks of the services of <see cref="NoDesktopChange"/>, watched for every tool.</summary>
    private static IEnumerable<(Type Service, object Mock)> WatchedServices(ContractServices c) =>
    [
        (typeof(IWindowService), c.Windows.Object), (typeof(IForegroundService), c.Foreground.Object), (typeof(IProcessService), c.Processes.Object),
        (typeof(IInputSimulationService), c.Input.Object), (typeof(IElementActionService), c.ElementActions.Object),
        (typeof(ILayoutService), c.Layouts.Object), (typeof(IWorkspaceService), c.Workspaces.Object),
        (typeof(IDebugBrowserLifecycle), c.DebugBrowsers.Object), (typeof(IWindowCloseVerifier), c.CloseVerifier.Object),
        (typeof(ILaunchPlacementService), c.Placement.Object)
    ];

    /// <summary>The tools whose services' mutating calls are watched in a typical call.</summary>
    private static readonly IReadOnlyDictionary<string, GatedWork> Work = new Dictionary<string, GatedWork>(StringComparer.Ordinal)
    {
        // Asks the user first when the client can ask (typical: confirm=true and no elicitation), then kills inside the gate
        ["kill_process"] = Watch<IProcessService>(c => c.Processes.Object, nameof(IProcessService.KillProcessAsync)),
        // The launch and its placement's window change run inside the gate (roadmap M3.13): a call with a placement reaches both
        ["launch_application"] = new GatedWork((_, _, _) => { },
            [Call<IProcessService>(nameof(IProcessService.LaunchApplicationAsync)), Call<ILaunchPlacementService>(nameof(ILaunchPlacementService.PlaceAsync))],
            Args("""{ "executable": "notepad", "waitForWindow": true, "placement": { "snap": "left_half", "focus": true } }"""),
            ReachesAll: true),
        ["close_window"] = Watch<IWindowService>(c => c.Windows.Object, nameof(IWindowService.CloseWindow)),
        ["close_windows_batch"] = Watch<IWindowService>(c => c.Windows.Object,
            nameof(IWindowService.CloseWindowsBatchAsync), nameof(IWindowService.CloseWindow)),
        // A packaged app's frame that does not take WM_CLOSE is asked through UI Automation's Window pattern, inside the gate too
        ["close_application"] = new GatedWork((c, _, _) =>
            {
                c.Windows.Setup(w => w.CloseWindow((nint)CalculatorFrame, false)).Returns(
                    WindowCloseResult.Failed((nint)CalculatorFrame, WindowErrorCode.CloseFailed, "The frame did not take WM_CLOSE."));
                c.CloseVerifier.Setup(v => v.CloseWithUiAutomationAsync((nint)CalculatorFrame, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            },
            [Call<IWindowService>(nameof(IWindowService.CloseWindow)), Call<IWindowCloseVerifier>(nameof(IWindowCloseVerifier.CloseWithUiAutomationAsync))],
            Args($$"""{ "handle": {{CalculatorFrame}}, "timeoutMs": 0 }"""),
            ReachesAll: true),
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
        // The run holds the gate for every step (roadmap M6.2): its focus, element action, typing, keys, clicks, drag, pointer
        // move and scroll on the run's window
        ["run_steps"] = new GatedWork((_, _, _) => { },
            [
                Call<IForegroundService>(nameof(IForegroundService.EnsureForeground)), Call<IElementActionService>(nameof(IElementActionService.ActAsync)),
                Call<IInputSimulationService>(nameof(IInputSimulationService.TypeTextAsync)),
                Call<IInputSimulationService>(nameof(IInputSimulationService.SendKeysAsync)),
                Call<IInputSimulationService>(nameof(IInputSimulationService.PressKeysAsync)),
                Call<IInputSimulationService>(nameof(IInputSimulationService.MouseClick)), Call<IInputSimulationService>(nameof(IInputSimulationService.DragAsync)),
                Call<IInputSimulationService>(nameof(IInputSimulationService.MoveMouse)), Call<IInputSimulationService>(nameof(IInputSimulationService.Scroll))
            ],
            Args("""
                {
                  "window": { "handle": 1706796 },
                  "steps": [
                    { "tool": "focus_window" },
                    { "tool": "element_action", "args": { "ref": "w4120035e17", "action": "set_value", "value": "window layout presets" } },
                    { "tool": "type_text", "args": { "text": "Meeting notes for Monday" } },
                    { "tool": "send_keys", "args": { "keys": "{HOME}" } },
                    { "tool": "press_keys", "args": { "keys": "ctrl+s" } },
                    { "tool": "mouse_click", "args": { "x": 560, "y": 110 } },
                    { "tool": "drag", "args": { "fromX": 560, "fromY": 110, "toX": 760, "toY": 110 } },
                    { "tool": "mouse_move", "args": { "x": 560, "y": 110 } },
                    { "tool": "scroll", "args": { "deltaY": 3 } }
                  ]
                }
                """),
            ReachesAll: true),
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

        // Arrange - the server's calls run under this test's tag, so the observer sees this test's hold of the gate, not another's.
        // Every call that may change the desktop is watched on every service, then the entry replaces what it watches its own way
        var flow = InputGate.TagFlow();
        var observer = new GateObserver(flow);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services =>
            {
                _services.Register(services);
                foreach (var (service, mock) in WatchedServices(_services))
                {
                    services.Replace(ServiceDescriptor.Singleton(service, observer.WatchEveryChange(service, mock, NoDesktopChange[service], [])));
                }

                work!.Arrange(_services, services, observer);
            });

        // Act
        var result = await server.CallToolAsync(tool, work!.Arguments ?? EveryToolContractTests.ArgumentsFor(tool));

        // Assert
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        var reached = work.Reaches.Where(call => observer.Seen.Any(o => o.Call == call || o.Call.StartsWith(call + " (", StringComparison.Ordinal))).ToList();
        if (work.ReachesAll)
        {
            reached.Should().BeEquivalentTo(work.Reaches, "the call of '{0}' must reach all the work it gates: {1}", tool, text);
        }
        else
        {
            reached.Should().NotBeEmpty("the typical call of '{0}' must reach the work it gates ({1}): {2}", tool, string.Join(", ", work.Reaches), text);
        }

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

    /// <summary>Calculator's frame on the typical desktop (TypicalDesktop.Windows[10]): an ApplicationFrameWindow of a packaged app.</summary>
    private const long CalculatorFrame = 0x300000 + 10 * 0x1F4;

    private static string Call<T>(string method) => $"{typeof(T).Name}.{method}";

    private static IReadOnlyDictionary<string, object?> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private static GatedWork Watch<T>(Func<ContractServices, T> inner, params string[] methods) where T : class =>
        Watch(inner, methods.Select(m => new WatchedCall(m)).ToArray());

    private static GatedWork Watch<T>(Func<ContractServices, T> inner, params WatchedCall[] calls) where T : class =>
        new((c, services, observer) => Replace(services, observer, inner(c), calls), calls.Select(w => Call<T>(w.Method)).ToList());

    /// <summary>Watches every change of <typeparamref name="T"/> (<see cref="NoDesktopChange"/>), <paramref name="calls"/> their own way.</summary>
    private static void Replace<T>(IServiceCollection services, GateObserver observer, T inner, params WatchedCall[] calls) where T : class =>
        services.Replace(ServiceDescriptor.Singleton(typeof(T), observer.WatchEveryChange(typeof(T), inner, NoDesktopChange[typeof(T)], calls)));

    private static GatedWork BrowserTabs(string method) => new((c, services, observer) =>
    {
        var browser = new DebugChrome();
        var tabs = (ICdpTabService)observer.WatchEveryChange(typeof(ICdpTabService), browser.Tabs.Object, NoDesktopChange[typeof(ICdpTabService)],
            [new WatchedCall(method, HandsOnTheGate: true)]);
        services.Replace(ServiceDescriptor.Singleton<IBrowserTabService>(new BrowserTabService(
            browser.Windows.Object, new BrowserDetectionService(), browser.Reading.Object, browser.Cdp.Object, tabs, browser.Owners,
            new TargetPolicy(TargetPolicySettings.Default), browser.Locator.Object, c.Session)));
    }, [Call<ICdpTabService>(method)]);

    private static GatedWork BrowserPages(string method) => new((c, services, observer) =>
    {
        var browser = new DebugChrome();
        var pages = (ICdpPageService)observer.WatchEveryChange(typeof(ICdpPageService), browser.Pages.Object, NoDesktopChange[typeof(ICdpPageService)],
            [new WatchedCall(method, HandsOnTheGate: true)]);
        services.Replace(ServiceDescriptor.Singleton<IBrowserPageService>(new BrowserPageService(
            browser.Windows.Object, new BrowserDetectionService(), browser.Reading.Object, browser.Cdp.Object, pages, browser.Owners,
            Mock.Of<IBrowserViewportLocator>(), new TargetPolicy(TargetPolicySettings.Default), browser.Locator.Object, c.Session)));
    }, [Call<ICdpPageService>(method)]);

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
