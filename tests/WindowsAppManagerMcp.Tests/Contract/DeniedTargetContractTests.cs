using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the user's target policy (docs/TOOLS.md, Target policy) holds for every tool that names a window or a process. Every
/// served tool whose input schema takes a window (handle, windowHandle, toWindowHandle, aboveHandle, a window selector, a batch's
/// windows, placements or changes, a launch's expectWindow or placement), an element's ref (ref, withinRef, listRef) or a
/// processId is called with a window, element or process the policy denies, through the production pipeline and the real
/// services over faked native seams (<see cref="NativeFakedServer"/>): it must answer TargetDeniedByPolicy, and nothing may act
/// (no window change, input, process start or stop, clipboard write, UI Automation action or CDP command), read the window
/// through UI Automation (by its handle, or by an element of it: its properties, a search under it, its children) or capture
/// its pixels (the window by its handle, or a screen rectangle over it). The tools that legitimately do not apply the policy
/// are on <see cref="Exempt"/>, each with the reason; the launch tools, which may start the denied app's program but must
/// neither reuse, place nor focus its window, are on <see cref="Launches"/>; a tool whose refusal this harness cannot reach is
/// on <see cref="CheckedElsewhere"/> with the test that checks it; a tool on none of the lists fails, so a new tool that takes
/// a window must say which. <see cref="Variants"/> call some tools again by another way of naming the target: a selector, a
/// process name, a ref.
/// </summary>
/// <remarks>
/// The denied window is a Chrome window (<see cref="DeniedDesktop"/>, the policy's default deny list plus chrome), so the browser
/// tools reach their policy check rather than refusing a window that is not a browser. Nothing touches the desktop: every native
/// seam is a fake, and the harness refuses to start when a real one is reachable.
/// </remarks>
public sealed class DeniedTargetContractTests
{
    private const long Denied = DeniedDesktop.DeniedWindow;
    private const int DeniedPid = DeniedDesktop.DeniedProcessId;
    private const long DeniedDialog = DeniedDesktop.DeniedDialog;

    /// <summary>Where the refusal is in the result.</summary>
    public enum RefusalAt
    {
        /// <summary>The call's own errorCode.</summary>
        Result,

        /// <summary>A batch item's, a run step's or a placement's errorCode (the call reports each item).</summary>
        AnItem
    }

    /// <summary>A call that targets the denied window or process, and where its refusal is reported.</summary>
    /// <param name="Arguments">The call's arguments, as JSON; <see cref="DeniedRef"/> stands for the ref of <paramref name="RefTo"/>.</param>
    /// <param name="At">Where the refusal is reported.</param>
    /// <param name="Before">A call made first that must succeed, such as saving the layout the call applies.</param>
    /// <param name="RefTo">The denied window's element whose ref the server's registry gives before the call, as a read would
    /// have given it to the agent.</param>
    public sealed record DeniedCall(string Arguments, RefusalAt At = RefusalAt.Result, (string Tool, string Arguments)? Before = null,
        Func<DeniedDesktop, FakeUiaNode>? RefTo = null);

    /// <summary>The placeholder in <see cref="DeniedCall.Arguments"/> for the ref of <see cref="DeniedCall.RefTo"/>.</summary>
    private const string DeniedRef = "{deniedRef}";

    /// <summary>The tools that must refuse the denied window or process, with the call that names it.</summary>
    private static readonly IReadOnlyDictionary<string, DeniedCall> Checked = new Dictionary<string, DeniedCall>(StringComparer.Ordinal)
    {
        // Window Control and the closes
        ["set_window_bounds"] = new($$"""{ "handle": {{Denied}}, "x": 0, "y": 0 }"""),
        ["set_window_state"] = new($$"""{ "handle": {{Denied}}, "state": "minimize" }"""),
        ["focus_window"] = new($$"""{ "handle": {{Denied}} }"""),
        ["snap_window"] = new($$"""{ "handle": {{Denied}}, "position": "left_half" }"""),
        ["move_window_to_monitor"] = new($$"""{ "handle": {{Denied}}, "monitor": "primary" }"""),
        ["set_window_zorder"] = new($$"""{ "handle": {{Denied}}, "mode": "topmost" }"""),
        ["close_window"] = new($$"""{ "handle": {{Denied}}, "force": true, "confirm": true }"""),
        ["close_application"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true }"""),
        ["close_windows_batch"] = new($$"""{ "windows": [ { "handle": {{Denied}} } ], "confirm": true }""", RefusalAt.AnItem),
        ["set_windows_bounds_batch"] = new($$"""{ "placements": [ { "handle": {{Denied}}, "x": 0, "y": 0, "width": 800, "height": 600 } ] }""", RefusalAt.AnItem),
        ["snap_windows_batch"] = new($$"""{ "placements": [ { "handle": {{Denied}}, "position": "left_half" } ] }""", RefusalAt.AnItem),
        ["set_windows_state_batch"] = new($$"""{ "changes": [ { "handle": {{Denied}}, "state": "minimize" } ] }""", RefusalAt.AnItem),
        // Processes
        ["kill_process"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true, "forceKill": true }"""),
        ["close_debug_browser"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true }"""),
        // Input, by windowHandle
        ["mouse_click"] = new($$"""{ "x": 500, "y": 500, "windowHandle": {{Denied}} }"""),
        ["mouse_move"] = new($$"""{ "x": 500, "y": 500, "windowHandle": {{Denied}} }"""),
        ["drag"] = new($$"""{ "fromX": 500, "fromY": 500, "toX": 600, "toY": 500, "windowHandle": {{Denied}} }"""),
        ["scroll"] = new($$"""{ "windowHandle": {{Denied}}, "deltaY": 3 }"""),
        ["type_text"] = new($$"""{ "text": "1000", "windowHandle": {{Denied}} }"""),
        ["send_keys"] = new($$"""{ "keys": "{ENTER}", "windowHandle": {{Denied}} }"""),
        ["press_keys"] = new($$"""{ "keys": "enter", "windowHandle": {{Denied}} }"""),
        // Elements and dialogs
        ["element_action"] = new($$"""{ "handle": {{Denied}}, "name": "Transfer money", "action": "invoke" }"""),
        // The denied app's own dialog: a window that is no dialog is DialogNotFound before anything else
        ["respond_to_dialog"] = new($$"""{ "handle": {{DeniedDialog}}, "button": "save" }"""),
        ["file_dialog"] = new($$"""{ "handle": {{DeniedDialog}}, "path": "C:\\Temp\\statement.pdf", "action": "save" }"""),
        // The UI Automation reads
        ["get_ui_elements"] = new($$"""{ "handle": {{Denied}} }"""),
        ["find_ui_elements"] = new($$"""{ "handle": {{Denied}}, "name": "Transfer money" }"""),
        ["get_active_popups"] = new($$"""{ "handle": {{Denied}} }"""),
        ["read_element_text"] = new($$"""{ "window": { "handle": {{Denied}} }, "name": "Transfer money" }"""),
        // A table of the denied window by a ref the agent holds (it takes nothing else)
        ["read_table"] = new($$"""{ "ref": "{{DeniedRef}}" }""", RefTo: d => d.TableNode),
        ["wait_for_element"] = new($$"""{ "window": { "handle": {{Denied}} }, "name": "Transfer money", "timeoutMs": 200 }"""),
        // Succeeds with what it may read, and lists the window it left out in errors (documented)
        ["get_desktop_state"] = new($$"""{ "handle": {{Denied}} }""", RefusalAt.AnItem),
        // Captures and the screen's text and pixels
        ["capture_window"] = new($$"""{ "handle": {{Denied}} }"""),
        ["capture_with_elements"] = new($$"""{ "handle": {{Denied}} }"""),
        ["read_screen_text"] = new($$"""{ "handle": {{Denied}} }"""),
        ["find_text_on_screen"] = new($$"""{ "handle": {{Denied}}, "text": "Transfer" }"""),
        ["wait_for_visual"] = new($$"""{ "handle": {{Denied}}, "timeoutMs": 200 }"""),
        // The browser tools: reads, tabs and the DOM bridge
        ["get_browser_url"] = new($$"""{ "handle": {{Denied}} }"""),
        ["get_browser_content"] = new($$"""{ "handle": {{Denied}} }"""),
        ["list_tabs"] = new($$"""{ "handle": {{Denied}} }"""),
        ["activate_tab"] = new($$"""{ "handle": {{Denied}}, "targetId": "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B" }"""),
        ["browser_navigate"] = new($$"""{ "handle": {{Denied}}, "url": "https://example.com/" }"""),
        ["wait_for_browser"] = new($$"""{ "handle": {{Denied}}, "selector": "#balance", "timeoutMs": 200 }"""),
        ["browser_evaluate"] = new($$"""{ "handle": {{Denied}}, "expression": "document.title" }"""),
        ["browser_handle_dialog"] = new($$"""{ "handle": {{Denied}}, "accept": false }"""),
        ["browser_element_rect"] = new($$"""{ "handle": {{Denied}}, "role": "button", "name": "Transfer money" }"""),
        ["browser_click"] = new($$"""{ "handle": {{Denied}}, "text": "Transfer money" }"""),
        ["browser_type"] = new($$"""{ "handle": {{Denied}}, "text": "1000", "selector": "#amount" }"""),
        ["browser_screenshot"] = new($$"""{ "handle": {{Denied}} }"""),
        // A saved layout that places the denied app's window: its placement is refused, and the others would still be arranged
        ["apply_layout"] = new("""{ "name": "bank" }""", RefusalAt.AnItem,
            Before: ("save_layout", """{ "name": "bank", "placements": [ { "processName": "chrome", "snap": "left_half" } ] }""")),
        // A run of steps on the denied window: its first step is refused, and the run with it
        ["run_steps"] = new($$"""{ "window": { "handle": {{Denied}} }, "steps": [ { "tool": "focus_window" }, { "tool": "type_text", "args": { "text": "1000" } } ] }""",
            RefusalAt.AnItem),
    };

    /// <summary>
    /// More calls of tools on <see cref="Checked"/>, each naming the denied target another way: a window selector or a process
    /// name instead of a handle, or a ref of the denied window's element. Keyed by "tool: how".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Tool, DeniedCall Call)> Variants =
        new Dictionary<string, (string Tool, DeniedCall Call)>(StringComparer.Ordinal)
        {
            ["focus_window: by a window selector"] = ("focus_window", new("""{ "window": { "processName": "chrome", "className": "Chrome_WidgetWin_1" } }""")),
            ["set_window_bounds: by a window selector"] = ("set_window_bounds",
                new("""{ "window": { "processName": "chrome", "titleContains": "Online banking" }, "x": 0, "y": 0 }""")),
            ["close_application: by its process name"] = ("close_application", new("""{ "processName": "chrome", "confirm": true }""")),
            ["element_action: by a ref"] = ("element_action", new($$"""{ "ref": "{{DeniedRef}}", "action": "invoke" }""", RefTo: d => d.ButtonNode)),
            ["read_element_text: by a ref"] = ("read_element_text", new($$"""{ "ref": "{{DeniedRef}}" }""", RefTo: d => d.ButtonNode)),
            ["wait_for_element: by a ref"] = ("wait_for_element",
                new($$"""{ "ref": "{{DeniedRef}}", "condition": "enabled", "timeoutMs": 200 }""", RefTo: d => d.ButtonNode)),
            ["find_ui_elements: within a ref"] = ("find_ui_elements",
                new($$"""{ "withinRef": "{{DeniedRef}}", "name": "Salary 3,200.00" }""", RefTo: d => d.TableNode)),
            // No handle: the window captured is the ref's own, read from the registry
            ["capture_window: by a ref"] = ("capture_window", new($$"""{ "ref": "{{DeniedRef}}" }""", RefTo: d => d.ButtonNode)),
        };

    /// <summary>A launch tool's call that would reuse, place or focus the denied window.</summary>
    /// <param name="Tool">The launch tool.</param>
    /// <param name="Arguments">The call, which launches the denied app's program ("chrome", <see cref="DeniedDesktop.Resolve"/>).</param>
    /// <param name="Places">The call places the launch's window: the placement must be refused with TargetDeniedByPolicy.
    /// Otherwise the call would reuse the open denied window: it must start the program instead (documented), not answer
    /// launchStatus alreadyOpen.</param>
    public sealed record DeniedLaunch(string Tool, string Arguments, bool Places);

    /// <summary>
    /// The launch tools' calls that would reuse, place or focus the denied window, keyed by "tool: how". Starting a program is
    /// no use of a window, and the user's policy does not deny it: the fakes start the denied app's program as the denied process
    /// (StartProcess returns its handle, whose main window is the denied window), so a placement gets the denied window to place,
    /// and a reuse finds it open.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DeniedLaunch> Launches = new Dictionary<string, DeniedLaunch>(StringComparer.Ordinal)
    {
        ["launch_application: reuseExisting"] = new("launch_application",
            """{ "executable": "chrome", "reuseExisting": true, "expectWindow": { "processName": "chrome" } }""", Places: false),
        ["launch_application: placement"] = new("launch_application",
            """{ "executable": "chrome", "waitTimeoutMs": 100, "placement": { "snap": "left_half", "focus": true } }""", Places: true),
        ["launch_applications_batch: reuseExisting"] = new("launch_applications_batch",
            """{ "applications": [ { "executable": "chrome", "reuseExisting": true, "expectWindow": { "processName": "chrome" } } ] }""", Places: false),
        ["launch_applications_batch: placement"] = new("launch_applications_batch",
            """{ "applications": [ { "executable": "chrome", "placement": { "snap": "left_half", "focus": true } } ], "waitTimeoutMs": 100 }""",
            Places: true),
    };

    /// <summary>
    /// The tools that take a window and apply the policy to it, but whose refusal this harness cannot reach, each with why and
    /// the tests that check it instead.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CheckedElsewhere = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["launch_browser_with_debug"] = "its placement is placed by the same service as launch_application's, which refuses a denied " +
                                        $"window ({nameof(Tests.Unit.Services.LaunchPlacementServiceTests)}.{nameof(Tests.Unit.Services.LaunchPlacementServiceTests.AWindowThePolicyDenies_IsNotTouched)}), " +
                                        "and its reuseIfRunning refuses a denied browser " +
                                        $"({nameof(Tests.Unit.Services.DebugBrowserLifecycleTests)}.{nameof(Tests.Unit.Services.DebugBrowserLifecycleTests.ReuseIfRunning_ABrowserThePolicyDenies_IsRefused_WithoutItsProcessIdOrPort)}); " +
                                        "its launch needs a debug browser that answers on its DevTools port, which the faked harness has none of",
    };

    /// <summary>The tools that take a window or a process but do not apply the policy to it, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["find_windows"] = "a window list: it lists a denied app's windows with their titles, as documented (Target policy), so no tool acts on them by mistake",
        ["wait_for_window"] = "a window list's wait: it reports the window as find_windows lists it, as documented (Target policy)",
        ["get_window_info"] = "find_windows' row of one window, with its state and integrity; it reads no content of the window",
        ["get_owned_windows"] = "a window list: the windows a window owns, as find_windows lists them",
        ["get_window_events"] = "the window event journal records every app's titles, as find_windows lists them; the policy does not hide them (documented)",
        ["list_processes"] = "a process list; a denied process is listed, but its command line is left out and never read (ProcessService)",
        ["save_layout"] = "writes a layout file: its placements name windows by matchers and it acts on none; a window whose process cannot be named is saved without its title",
    };

    /// <summary>The window, element and process properties of a tool's input schema, found at any depth.</summary>
    private static readonly HashSet<string> TargetProperties = new(StringComparer.Ordinal)
    {
        "handle", "windowHandle", "toWindowHandle", "aboveHandle", "window", "windows", "placements", "changes", "processId",
        "ref", "withinRef", "listRef", "expectWindow", "placement"
    };

    /// <summary>Every call to make: each tool of <see cref="Checked"/> by its name, and each of <see cref="Variants"/> by its key.</summary>
    public static TheoryData<string> CheckedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in Checked.Keys.Concat(Variants.Keys).Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void EveryToolThatNamesAWindowOrAProcess_IsCheckedOrExempt()
    {
        // Arrange - the served tools' input schemas, as tools/list sends them
        var targeting = ServedSchemas()
            .Where(t => TargetPropertiesOf(t.Schema).Any())
            .Select(t => t.Name)
            .ToList();

        // Assert
        var launches = Launches.Values.Select(l => l.Tool).Distinct().ToList();
        targeting.Should().Contain(["close_window", "type_text", "kill_process", "run_steps"], "the scan must see the schemas' window and process parameters");
        Checked.Keys.Concat(Exempt.Keys).Concat(launches).Concat(CheckedElsewhere.Keys).Should().OnlyHaveUniqueItems(
            "a tool is checked, exempt, a launch or checked elsewhere: one of them");
        targeting.Except(Checked.Keys).Except(Exempt.Keys).Except(launches).Except(CheckedElsewhere.Keys).Should().BeEmpty(
            "a tool that takes a window or a process must refuse one the user's target policy denies: add it to Checked with a call " +
            "that names the denied window (a launch tool to Launches), or to Exempt with the reason it legitimately does not apply " +
            "the policy");
        Checked.Keys.Concat(Exempt.Keys).Concat(launches).Concat(CheckedElsewhere.Keys).Except(targeting).Should().BeEmpty(
            "an entry names a served tool that takes a window or a process");
        Variants.Values.Select(v => v.Tool).Except(Checked.Keys).Should().BeEmpty("a variant calls a tool that is checked");
        targeting.Should().Contain("read_table", "the scan must see a tool that names its target only by a ref");
        targeting.Should().Contain(["launch_application", "launch_applications_batch"], "the scan must see a launch's expectWindow and placement");
    }

    [Theory]
    [MemberData(nameof(CheckedTools))]
    public async Task ACallThatNamesADeniedWindowOrProcess_IsTargetDeniedByPolicy_AndNothingIsDone(string name)
    {
        // Arrange
        var (tool, call) = Checked.TryGetValue(name, out var named) ? (name, named) : Variants[name];
        using var desktop = new NativeFakedServer(DeniedDesktop.Policy);
        await using var server = await desktop.StartAsync();

        if (call.Before is var (before, beforeArguments))
        {
            var prepared = await server.CallToolAsync(before, Arguments(beforeArguments));
            prepared.IsError.Should().NotBe(true, "{0} prepares the call: {1}", before, prepared.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        }

        var arguments = call.RefTo is { } refTo
            ? call.Arguments.Replace(DeniedRef, desktop.MintRef((nint)Denied, refTo(desktop.Desktop)), StringComparison.Ordinal)
            : call.Arguments;
        var readsBefore = TreeLog.Of(desktop.Desktop.Tree);

        // Act
        var result = await server.CallToolAsync(tool, Arguments(arguments));

        // Assert
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var payload = JsonDocument.Parse(text);
        var codes = call.At == RefusalAt.Result
            ? payload.RootElement.TryGetProperty("errorCode", out var code) ? [code.GetString()] : []
            : ErrorCodes(payload.RootElement).ToList();
        codes.Should().Contain(nameof(InputErrorCode.TargetDeniedByPolicy), "'{0}' must refuse a window or process the policy denies: {1}", tool, text);
        if (call.At == RefusalAt.Result)
        {
            result.IsError.Should().BeTrue("a refusal is a tool error: {0}", text);
        }

        desktop.Actions().Should().BeEmpty("'{0}' must do nothing to a window or process the policy denies", tool);
        AssertNothingOfTheDeniedWindowWasRead(desktop, readsBefore, tool);
    }

    public static TheoryData<string> LaunchCalls()
    {
        var data = new TheoryData<string>();
        foreach (var name in Launches.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LaunchCalls))]
    public async Task ALaunch_NeitherReusesNorPlacesADeniedWindow(string name)
    {
        // Arrange - the denied app's program starts as the denied process, whose main window is the denied window
        var launch = Launches[name];
        using var desktop = new NativeFakedServer(DeniedDesktop.Policy);
        desktop.Mock<INativeProcessWrapper>().Setup(p => p.StartProcess(It.IsAny<System.Diagnostics.ProcessStartInfo>()))
            .Returns(desktop.Desktop.ProcessHandle.Object);
        desktop.Desktop.ProcessHandle.Setup(h => h.RefreshMainWindowHandle()).Returns((nint)Denied);
        await using var server = await desktop.StartAsync();
        var readsBefore = TreeLog.Of(desktop.Desktop.Tree);

        // Act
        var result = await server.CallToolAsync(launch.Tool, Arguments(launch.Arguments));

        // Assert - the program was started, as documented; the denied window was not reused, placed or focused
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var payload = JsonDocument.Parse(text);
        desktop.Mock<INativeProcessWrapper>().Verify(p => p.StartProcess(It.Is<System.Diagnostics.ProcessStartInfo>(s => s.FileName == DeniedDesktop.DeniedProgram)),
            Times.Once, $"'{name}' starts the denied app's program: the policy denies its windows, not its start: {text}");
        if (launch.Places)
        {
            ErrorCodes(payload.RootElement).Should().Contain(nameof(InputErrorCode.TargetDeniedByPolicy),
                "'{0}' must refuse to place a window the policy denies: {1}", name, text);
            Values(payload.RootElement, "windowHandle").Should().Contain(Denied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "the launch found the denied window, so its placement was refused: {0}", text);
        }
        else
        {
            text.Should().NotContain("alreadyOpen", "'{0}' must not reuse a window the policy denies: {1}", name, text);
        }

        desktop.Actions().Where(a => a != $"{nameof(INativeProcessWrapper)}.{nameof(INativeProcessWrapper.StartProcess)}").Should().BeEmpty(
            "'{0}' must do nothing to a window the policy denies", name);
        AssertNothingOfTheDeniedWindowWasRead(desktop, readsBefore, name);
    }

    [Fact]
    public void TheCaptureCheck_SeesEveryWayToReadTheDeniedWindowsPixels()
    {
        // Arrange - the captures a tool could make, on the harness's own fakes (nothing is captured: they are mocks)
        using var desktop = new NativeFakedServer(DeniedDesktop.Policy);

        // Act
        DeniedCaptureSamples.Capture(desktop.Mock<IScreenCaptureWrapper>().Object, desktop.Mock<IGraphicsCaptureWrapper>().Object);

        // Assert - the seven of the denied window, and not the three beside it
        CapturesOfTheDeniedWindow(desktop).Should().HaveCount(7);
    }

    [Fact]
    public void TheReadCheck_SeesEveryWayToReadADeniedElement()
    {
        // Arrange - the harness's own fake tree (nothing is read: it is in memory)
        using var desktop = new NativeFakedServer(DeniedDesktop.Policy);
        var tree = desktop.Desktop.Tree;
        var before = TreeLog.Of(tree);

        // Act - a search under the table that finds nothing, the children listed, probed and fetched, a property read; and a
        // read of an element of no denied window
        tree.FindAll(desktop.Desktop.TableNode, UiaScope.Descendants, new UiaPropertyCondition(UiaProperty.Name, "No such element", false),
            CancellationToken.None);
        tree.GetChildren(desktop.Desktop.WindowNode, CancellationToken.None);
        tree.HasChildren(desktop.Desktop.DialogNode);
        tree.GetChildElements(desktop.Desktop.TableNode, CancellationToken.None);
        tree.Read(desktop.Desktop.ButtonNode);
        tree.Read(new FakeUiaNode("Button", "OK"));

        // Assert - the cached fetch reads the table's row too
        before.DeniedElementReadsSince(desktop.Desktop).Should().BeEquivalentTo(
        [
            "FindAll under Transactions:Descendants", "GetChildren of Online banking - Google Chrome", "HasChildren of Save As",
            "GetChildElements of Transactions", "Read:Salary 3,200.00", "Read:Transfer money"
        ]);
        desktop.Desktop.DeniedElementNames.Should().Contain(["Salary 3,200.00", "Save"], "the names are read from the denied windows' trees");
    }

    // ---- helpers ----

    /// <summary>
    /// Nothing of the denied windows was read through UI Automation since <paramref name="before"/> (by handle, or by an element:
    /// its properties, a search under it, its children listed, fetched or probed), and no pixel of them was captured.
    /// </summary>
    private static void AssertNothingOfTheDeniedWindowWasRead(NativeFakedServer desktop, TreeLog before, string call)
    {
        desktop.Desktop.WindowsReadThroughUiAutomation.Should().NotContain([(nint)Denied, (nint)DeniedDialog],
            "'{0}' must not read a denied window through UI Automation", call);
        before.DeniedElementReadsSince(desktop.Desktop)
            .Should().BeEmpty("'{0}' must not read a denied window's elements through UI Automation", call);
        CapturesOfTheDeniedWindow(desktop).Should().BeEmpty("'{0}' must not capture a denied window's pixels", call);
    }

    /// <summary>How far each of the fake UI Automation tree's logs had got.</summary>
    private sealed record TreeLog(int Reads, int Finds, int ChildListings, int CachedFetches, int ChildProbes)
    {
        public static TreeLog Of(FakeUiaTree tree) =>
            new(tree.Reads.Count, tree.Finds.Count, tree.ChildListings.Count, tree.CachedFetches.Count, tree.ChildProbes.Count);

        /// <summary>
        /// The entries logged since of an element of a denied window: a read ("Read:Name", "Element:Name",
        /// "Pattern:IUiaInvokePattern:Name", ...), a search under it ("Name:Descendants"), and its children listed, fetched or
        /// probed ("Name").
        /// </summary>
        public IEnumerable<string> DeniedElementReadsSince(DeniedDesktop desktop)
        {
            var denied = desktop.DeniedElementNames;
            var tree = desktop.Tree;
            return tree.Reads.Skip(Reads).Where(read => denied.Contains(read[(read.LastIndexOf(':') + 1)..]))
                .Concat(tree.Finds.Skip(Finds).Where(find => find.LastIndexOf(':') is var colon and >= 0 && denied.Contains(find[..colon]))
                    .Select(find => $"FindAll under {find}"))
                .Concat(tree.ChildListings.Skip(ChildListings).Where(denied.Contains).Select(node => $"GetChildren of {node}"))
                .Concat(tree.CachedFetches.Skip(CachedFetches).Where(denied.Contains).Select(node => $"GetChildElements of {node}"))
                .Concat(tree.ChildProbes.Skip(ChildProbes).Where(denied.Contains).Select(node => $"HasChildren of {node}"))
                .ToList();
        }
    }

    /// <summary>
    /// The captures of a denied window's pixels the fakes were asked for: the window by its handle (CaptureWindow,
    /// CaptureWindowFromScreen, WithPixels of a PixelSource.Window or WindowFromScreen, Windows.Graphics.Capture), or a screen
    /// rectangle over it (CaptureScreenRegion, WithPixels of a PixelSource.Screen).
    /// </summary>
    private static IEnumerable<string> CapturesOfTheDeniedWindow(NativeFakedServer desktop)
    {
        var screen = desktop.Mock<IScreenCaptureWrapper>().Invocations
            .Where(i => i.Method.Name switch
            {
                nameof(IScreenCaptureWrapper.CaptureWindow) or nameof(IScreenCaptureWrapper.CaptureWindowFromScreen) =>
                    i.Arguments[0] is nint handle && IsDenied(handle),
                nameof(IScreenCaptureWrapper.CaptureScreenRegion) =>
                    OverTheDeniedWindow((int)i.Arguments[0], (int)i.Arguments[1], (int)i.Arguments[2], (int)i.Arguments[3]),
                nameof(IScreenCaptureWrapper.WithPixels) => i.Arguments[0] switch
                {
                    PixelSource.Window window => IsDenied(window.Handle),
                    PixelSource.WindowFromScreen window => IsDenied(window.Handle),
                    PixelSource.Screen area => OverTheDeniedWindow(area.X, area.Y, area.Width, area.Height),
                    _ => true
                },
                _ => false
            })
            .Select(i => $"{i.Method.Name}({string.Join(", ", i.Arguments)})");
        var graphics = desktop.Mock<IGraphicsCaptureWrapper>().Invocations
            .Where(i => i.Arguments.OfType<nint>().Any(IsDenied))
            .Select(i => $"Windows.Graphics.Capture {i.Method.Name}({string.Join(", ", i.Arguments)})");
        return screen.Concat(graphics).ToList();

        static bool IsDenied(nint handle) => handle == (nint)Denied || handle == (nint)DeniedDialog;

        static bool OverTheDeniedWindow(int x, int y, int width, int height)
        {
            var (left, top, right, bottom) = DeniedDesktop.DeniedWindowRect;
            return width > 0 && height > 0 && x < right && x + (long)width > left && y < bottom && y + (long)height > top;
        }
    }

    /// <summary>Every errorCode in a result, at any depth (a batch's items, a run's steps, a layout's placements).</summary>
    private static IEnumerable<string?> ErrorCodes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "errorCode" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString();
                    }

                    foreach (var code in ErrorCodes(property.Value))
                    {
                        yield return code;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var code in ErrorCodes(item))
                    {
                        yield return code;
                    }
                }

                break;
        }
    }

    /// <summary>The raw values of every property named <paramref name="name"/> in a result, at any depth.</summary>
    private static IEnumerable<string> Values(JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .SelectMany(p => (p.Name == name ? [p.Value.GetRawText()] : Enumerable.Empty<string>()).Concat(Values(p.Value, name))),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(item => Values(item, name)),
        _ => []
    };

    private static IReadOnlyDictionary<string, object?> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The served tools and their input schemas, from the production registration (no service is created).</summary>
    private static IReadOnlyList<(string Name, JsonElement Schema)> ServedSchemas()
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => throw new InvalidOperationException("The schemas need no layout storage."));
        services.AddMcpServer().AddWindowsAppManagerTools();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Select(t => (t.ProtocolTool.Name, t.ProtocolTool.InputSchema.Clone()))
            .ToList();
    }

    /// <summary>The names of the window and process properties in a schema, at any depth (items of arrays, anyOf and the like).</summary>
    private static IEnumerable<string> TargetPropertiesOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (TargetProperties.Contains(property.Name))
                {
                    yield return property.Name;
                }

                foreach (var nested in TargetPropertiesOf(property.Value))
                {
                    yield return nested;
                }
            }
        }

        foreach (var key in new[] { "items", "anyOf", "oneOf", "allOf" })
        {
            if (!schema.TryGetProperty(key, out var value))
            {
                continue;
            }

            var children = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
            foreach (var nested in children.SelectMany(TargetPropertiesOf))
            {
                yield return nested;
            }
        }
    }
}

/// <summary>
/// The captures of <see cref="DeniedTargetContractTests.TheCaptureCheck_SeesEveryWayToReadTheDeniedWindowsPixels"/>, made on the
/// fakes it is given: seven of the denied window's pixels (by its handle, its dialog's, and screen rectangles over it) and three
/// beside them.
/// </summary>
internal static class DeniedCaptureSamples
{
    public static void Capture(IScreenCaptureWrapper screen, IGraphicsCaptureWrapper graphics)
    {
        var (left, top, right, bottom) = DeniedDesktop.DeniedWindowRect;
        screen.CaptureWindow((nint)DeniedDesktop.DeniedWindow, includeFrame: true);
        screen.CaptureWindowFromScreen((nint)DeniedDesktop.DeniedDialog, includeFrame: false);
        screen.WithPixels(new PixelSource.Window((nint)DeniedDesktop.DeniedWindow, false), _ => 0);
        screen.WithPixels(new PixelSource.WindowFromScreen((nint)DeniedDesktop.DeniedWindow, true), _ => 0);
        screen.CaptureScreenRegion(right - 1, bottom - 1, 50, 50);
        screen.WithPixels(new PixelSource.Screen(0, 0, left + 1, top + 1), _ => 0);
        graphics.CaptureWindow((nint)DeniedDesktop.DeniedWindow, includeFrame: false);

        // Not of the denied window: another window, and screen rectangles beside it
        screen.CaptureWindow((nint)DeniedDesktop.AllowedWindow, includeFrame: true);
        screen.CaptureScreenRegion(right, top, 100, 100);
        screen.WithPixels(new PixelSource.Screen(0, 0, left, 2000), _ => 0);
    }
}
