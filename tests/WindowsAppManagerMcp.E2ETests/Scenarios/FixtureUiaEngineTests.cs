namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The two UI Automation engines (roadmap M3.2) against the fixture: the UIA3 COM client (the default) and the managed
/// UIA2 client (WAM_UIA_ENGINE=uia2) must read the same tree and find the same elements with the same identity, patterns
/// and state, and the bounded, cached, breadth-first walk (roadmap M3.1) must keep the levels nearest the window.
/// No-input tier: they only read.
/// </summary>
/// <remarks>
/// <para>These are the checks the M3.2 spike could not make without a desktop, and part of the engine release gate
/// (TESTING.md, "Release gate: the UI Automation engines"). Every test reads with both engines itself, whatever
/// WAM_UIA_ENGINE says. They confirm that the COM client's control view (its cache requests' TreeFilter and the
/// ControlViewCondition of FindAllBuildCache) lists the same children as the managed client, that both report the same
/// bounds, and that the tree reads work from the worker threads.</para>
/// <para>They also cover what only the UIA3 engine reads from cache requests of its own: the identity (process, window
/// handle, password flag), the interaction (focus and patterns), the cached runtime id that refs are keyed on (no runtime
/// id, no ref), and the search conditions (control types by id, automation ids ignoring case, runtime ids). Patterns are
/// compared by their names (<see cref="UiaPatternNames.All"/>), which leave out LegacyIAccessible: the UIA3 engine reports
/// it on nearly every element and the managed client never does, by design.</para>
/// <para>The window's non-client area (its title bar, the system menu bar and its item, and the minimize, maximize or restore
/// and close buttons) is not the server's or the fixture's: Windows serves it through its own proxies, natively to UIA3 and
/// through .NET's client-side proxy to UIA2, and those differ in automation ids and framework from run to run (the 1.15.0
/// gate's second run read 'Minimize-Restore' and no framework where UIA3 read 'Minimize' and Win32). The BothEngines_* facts
/// compare those elements by control type and name only (<see cref="IsNonClient"/>), and everything else in full.</para>
/// </remarks>
public class FixtureUiaEngineTests
{
    /// <summary>The engines can round a fractional rectangle differently by a pixel.</summary>
    private const int BoundsTolerance = 1;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The control types the cross-engine search looks for: every kind of control on the fixture's main window.</summary>
    private static readonly string[] SearchedControlTypes = ["Edit", "CheckBox", "ComboBox", "Button", "List"];

    /// <summary>
    /// The kinds of element Windows puts in a window's non-client area: the title bar, the system menu bar and its item, and
    /// the minimize, maximize or restore and close buttons.
    /// </summary>
    private static readonly string[] NonClientControlTypes = ["TitleBar", "MenuBar", "MenuItem", "Button"];

    private readonly ITestOutputHelper _output;

    public FixtureUiaEngineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task GetUIElements_WithEachEngine_ReportsTheFixtureAutomationIdsAndControlTypes(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Truncated.Should().BeFalse("the fixture's tree is small: {0}", result.TruncationReason);
        result.WindowTitle.Should().Be(FixtureContract.MainTitle(e2e.Fixture.Instance));
        var byId = UiaReads.Flatten(result.Elements)
            .Where(e => e.AutomationId != null)
            .GroupBy(e => e.AutomationId!)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var id in e2e.Fixture.Ready.Elements.Keys)
        {
            byId.Should().ContainKey(id, "the {0} engine must find '{1}'", engine, id);
            byId[id].Bounds.Should().NotBeNull();
        }

        foreach (var (id, controlType) in FixtureUiElementsTests.MainWindowControlTypes)
        {
            byId.Should().ContainKey(id, "the {0} engine must find '{1}'", engine, id);
            byId[id].ControlType.Should().Be(controlType, "the {0} engine's control type of '{1}'", engine, id);
        }

        _output.WriteLine($"{engine}: {result.TotalElementCount} elements in {result.ElapsedMilliseconds} ms, depth {result.DepthReached}.");
    }

    [DesktopNoInputFact]
    public async Task BothEngines_ReadTheSameTree()
    {
        // Arrange - one fixture, a server per engine
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: UiaEngine.Uia3);
        using var uia2 = ServerHarness.Create(DesktopTier.NoInput, e2e.Fixture.ProcessId, UiaEngine.Uia2, e2e.AgentHost);
        var window = e2e.Fixture.MainWindowHandle;
        var client = e2e.Fixture.Ready.ClientRect;

        // Act
        var fromUia3 = await e2e.Server.UiElements.GetUIElementsAsync(window);
        var fromUia2 = await uia2.UiElements.GetUIElementsAsync(window);

        // Assert - the same elements in the same order, with the same names, types, ids, frameworks and states, and a ref
        // for each (the UIA3 engine reads the runtime id a ref needs from its walk's cache request); the title bar's by type
        // and name only
        fromUia3.Success.Should().BeTrue(fromUia3.Error);
        fromUia2.Success.Should().BeTrue(fromUia2.Error);
        var uia3Elements = UiaReads.Flatten(fromUia3.Elements).ToList();
        var uia2Elements = UiaReads.Flatten(fromUia2.Elements).ToList();
        _output.WriteLine($"UIA3: {uia3Elements.Count} elements in {fromUia3.ElapsedMilliseconds} ms; " +
                          $"UIA2: {uia2Elements.Count} in {fromUia2.ElapsedMilliseconds} ms.");
        uia3Elements.Select(Describe).Should().Equal(uia2Elements.Select(Describe));
        for (var i = 0; i < uia3Elements.Count; i++)
        {
            if (IsNonClient(uia3Elements[i].ControlType, uia3Elements[i].Bounds, client))
            {
                continue;
            }

            var (a, b) = (uia3Elements[i].Bounds, uia2Elements[i].Bounds);
            (a == null).Should().Be(b == null, "element {0} ({1})", i, Describe(uia3Elements[i]));
            if (a != null && b != null)
            {
                a.X.Should().BeCloseTo(b.X, BoundsTolerance);
                a.Y.Should().BeCloseTo(b.Y, BoundsTolerance);
                a.Width.Should().BeCloseTo(b.Width, BoundsTolerance);
                a.Height.Should().BeCloseTo(b.Height, BoundsTolerance);
            }
        }

        string Describe(UIElementInfo e) => IsNonClient(e.ControlType, e.Bounds, client)
            ? $"{e.ControlType} '{e.Name}' (non-client: type and name only)"
            : $"{e.ControlType} '{e.Name}' aid={e.AutomationId} enabled={e.IsEnabled} offscreen={e.IsOffscreen} " +
              $"children={e.Children?.Count ?? 0} fw={e.FrameworkId} ref={e.Ref != null}";
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task FindUiElements_WithEachEngine_ReadsTheFixturesIdentityAndState_WithRefsThatResolve(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);

        // Act
        var results = await FindAsync(e2e.Server, Queries(e2e.Fixture.MainWindowHandle));

        // Assert - every element is the fixture's, the password box alone is a password field, and each has a click point
        // in its bounds and a ref that resolves to the live element
        foreach (var result in results)
        {
            result.Success.Should().BeTrue(result.Error);
            result.Truncated.Should().BeFalse("{0}", result.TruncationReason);
            foreach (var element in result.Elements!)
            {
                var name = Snapshot(element);
                _output.WriteLine($"{name} bounds={element.Bounds} click={element.ClickPoint} ref={element.Ref}");
                element.ProcessId.Should().Be(e2e.Fixture.ProcessId, "{0}", name);
                (element.IsPassword == true).Should().Be(element.AutomationId == FixtureContract.Ids.PasswordBox, "{0}", name);
                element.Ref.Should().NotBeNull("{0}", name);
                element.Bounds.Should().NotBeNull("{0}", name);
                element.ClickPoint.Should().NotBeNull("{0}", name);
                var (bounds, point) = (element.Bounds!, element.ClickPoint!);
                point.X.Should().BeInRange(bounds.X, bounds.X + bounds.Width, "{0}", name);
                point.Y.Should().BeInRange(bounds.Y, bounds.Y + bounds.Height, "{0}", name);

                var resolution = await e2e.Server.Get<IElementRegistry>().ResolveAsync(element.Ref!, Timeout, CancellationToken.None);
                resolution.Success.Should().BeTrue("{0}: {1}", name, resolution.Error);
                resolution.Element!.Via.Should().Be(ElementResolutionPath.Live, "{0}", name);
            }
        }

        // ... the search by control types found each of the fixture's controls, the one by automation id the check box
        var fixtureControls = FixtureUiElementsTests.MainWindowControlTypes
            .Where(c => SearchedControlTypes.Contains(c.Value))
            .Select(c => c.Key);
        results[0].Elements!.Select(e => e.AutomationId).Should().Contain(fixtureControls);
        var checkBox = results[1].Elements.Should().ContainSingle("automation ids are compared ignoring case").Which;
        checkBox.AutomationId.Should().Be(FixtureContract.Ids.CheckBox);
        checkBox.ToggleState.Should().NotBeNull("detail full reads a check box's state");

        // ... and the password box's text is never read, by find_ui_elements or by read_element_text
        var password = results[0].Elements!.Single(e => e.AutomationId == FixtureContract.Ids.PasswordBox);
        password.ControlType.Should().Be("Edit");
        password.IsPassword.Should().BeTrue();
        password.Value.Should().BeNull("a password field's value is never read");
        var text = await e2e.Server.Get<IElementReadService>().ReadTextAsync(new ElementTextRequest(password.Ref, null, 0, 1000, Timeout));
        text.Success.Should().BeTrue(text.Error);
        text.Source.Should().Be(ElementTextSource.Redacted, "a password field's text is never read");
        text.Text.Should().BeNull();
        text.IsPassword.Should().BeTrue();
    }

    [DesktopNoInputFact]
    public async Task BothEngines_FindTheSameElements_WithTheSameStateAndIdentity()
    {
        // Arrange - one fixture, a server per engine
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: UiaEngine.Uia3);
        using var uia2 = ServerHarness.Create(DesktopTier.NoInput, e2e.Fixture.ProcessId, UiaEngine.Uia2, e2e.AgentHost);
        var queries = Queries(e2e.Fixture.MainWindowHandle);
        var client = e2e.Fixture.Ready.ClientRect;

        // Act
        var fromUia3 = await FindAsync(e2e.Server, queries);
        var fromUia2 = await FindAsync(uia2, queries);

        // Assert - the same elements in the same order, with the same identity, patterns, state, description and bounds; the
        // title bar's buttons by type and name only
        for (var query = 0; query < queries.Count; query++)
        {
            fromUia3[query].Success.Should().BeTrue("UIA3, query {0}: {1}", query, fromUia3[query].Error);
            fromUia2[query].Success.Should().BeTrue("UIA2, query {0}: {1}", query, fromUia2[query].Error);
            var (a, b) = (fromUia3[query].Elements!, fromUia2[query].Elements!);
            _output.WriteLine($"Query {query}: UIA3 found {a.Count}, UIA2 {b.Count}.");
            a.Should().NotBeEmpty();
            a.Select(Compared).Should().Equal(b.Select(Compared), "query {0} must find the same elements with both engines", query);
            for (var i = 0; i < a.Count; i++)
            {
                if (IsNonClient(a[i].ControlType, a[i].Bounds, client))
                {
                    continue;
                }

                var (x, y) = (a[i].Bounds, b[i].Bounds);
                (x == null).Should().Be(y == null, "{0}", Snapshot(a[i]));
                if (x != null && y != null)
                {
                    x.X.Should().BeCloseTo(y.X, BoundsTolerance, "{0}", Snapshot(a[i]));
                    x.Y.Should().BeCloseTo(y.Y, BoundsTolerance, "{0}", Snapshot(a[i]));
                    x.Width.Should().BeCloseTo(y.Width, BoundsTolerance, "{0}", Snapshot(a[i]));
                    x.Height.Should().BeCloseTo(y.Height, BoundsTolerance, "{0}", Snapshot(a[i]));
                }
            }
        }

        string Compared(FoundElement e) => IsNonClient(e.ControlType, e.Bounds, client)
            ? $"{e.ControlType} '{e.Name}' (non-client: type and name only)"
            : Snapshot(e);
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task MaxElements_KeepsTheLevelsNearestTheWindow(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        var full = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);
        full.Success.Should().BeTrue(full.Error);
        var cap = Math.Max(1, full.TotalElementCount / 2);

        // Act
        var cut = await e2e.Server.UiElements.GetUIElementsAsync(
            e2e.Fixture.MainWindowHandle, filter: new UIElementFilter(MaxElements: cap));

        // Assert - breadth first: the window's own children come before anything deeper
        cut.Success.Should().BeTrue(cut.Error);
        cut.TotalElementCount.Should().Be(cap);
        cut.Truncated.Should().BeTrue();
        cut.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        cut.Elements!.Select(e => (e.ControlType, e.Name, e.AutomationId))
            .Should().Equal(full.Elements!.Take(cut.Elements!.Count).Select(e => (e.ControlType, e.Name, e.AutomationId)));
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public void TheVirtualizedList_IsFoundBySearch_AndOffersItsItemContainerPattern(UiaEngine engine)
    {
        // Arrange - the search and the pattern M3.4 and M3.10 build on, through the seam with each engine; nothing acts
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        var tree = e2e.Server.Get<IUiaTree>();
        var window = tree.FromHandle(e2e.Fixture.MainWindowHandle)!;

        // Act
        var list = tree.FindAll(window, UiaScope.Descendants,
            UiaCondition.PropertyEquals(UiaProperty.AutomationId, FixtureContract.Ids.VirtualList), CancellationToken.None).Single();
        var container = tree.GetPattern<IUiaItemContainerPattern>(list.Node);
        var liveRuntimeId = tree.GetRuntimeId(list.Node);

        // Assert
        list.Properties.ControlType.Should().Be("List");
        list.Patterns.Should().HaveFlag(UiaPatterns.ItemContainer);
        container.Should().NotBeNull();
        tree.ReadIdentity(list.Node).ProcessId.Should().Be(e2e.Fixture.ProcessId);

        // ... and the runtime id the search read with the element, which refs are keyed on, is the live one and finds the
        // list again (the condition the element registry finds a ref's element by)
        liveRuntimeId.Should().NotBeNullOrEmpty();
        list.RuntimeId.Should().Equal(liveRuntimeId!);
        tree.FindAll(window, UiaScope.Descendants, UiaCondition.PropertyEquals(UiaProperty.RuntimeId, list.RuntimeId!), CancellationToken.None)
            .Should().ContainSingle().Which.Properties.AutomationId.Should().Be(FixtureContract.Ids.VirtualList);
    }

    /// <summary>
    /// The searches of the find_ui_elements scenarios, at detail full, which reads what the walk does not: the identity
    /// (process, window handle, password flag), focus and focusability, the state, the value and the description. The
    /// first searches by control types (an Or of their ids) among the elements on screen (IsOffscreen false), the second by
    /// an automation id in other letter case (a condition that ignores case).
    /// </summary>
    private static IReadOnlyList<ElementQuery> Queries(nint window) =>
    [
        new(window, ControlTypes: SearchedControlTypes, MaxResults: 50, Detail: ElementDetail.Full, Timeout: Timeout,
            Scope: ElementSearchScope.Window),
        new(window, AutomationId: FixtureContract.Ids.CheckBox.ToUpperInvariant(), Detail: ElementDetail.Full, Timeout: Timeout,
            Scope: ElementSearchScope.Window)
    ];

    private static async Task<IReadOnlyList<FindElementsResult>> FindAsync(ServerHarness server, IEnumerable<ElementQuery> queries)
    {
        var search = server.Get<IElementSearchService>();
        var results = new List<FindElementsResult>();
        foreach (var query in queries)
        {
            results.Add(await search.FindAsync(query));
        }

        return results;
    }

    /// <summary>
    /// Whether an element is part of the window's non-client area: one of <see cref="NonClientControlTypes"/> that lies wholly
    /// outside the window's client area, as the fixture reports it in its ready event (every control of the fixture is inside
    /// it). Its place, not its automation id, says so: the automation ids are what the engines' proxies disagree on.
    /// </summary>
    internal static bool IsNonClient(string controlType, BoundsDto? bounds, ScreenRect client) =>
        bounds is { } b && NonClientControlTypes.Contains(controlType) &&
        (b.Y + b.Height <= client.Y + BoundsTolerance || b.Y >= client.Y + client.Height - BoundsTolerance ||
         b.X + b.Width <= client.X + BoundsTolerance || b.X >= client.X + client.Width - BoundsTolerance);

    /// <summary>
    /// What find_ui_elements reports about an element at detail full, but its ref (each server numbers its own) and its
    /// bounds and click point (compared with a tolerance). The patterns are by name, without LegacyIAccessible.
    /// </summary>
    private static string Snapshot(FoundElement e) =>
        $"{e.ControlType} '{e.Name}' aid={e.AutomationId} class={e.ClassName} fw={e.FrameworkId} pid={e.ProcessId} " +
        $"hwnd={e.NativeWindowHandle} password={e.IsPassword} patterns={string.Join(",", e.Patterns)} " +
        $"toggle={e.ToggleState} expandCollapse={e.ExpandCollapseState} selected={e.IsSelected} " +
        $"value='{e.Value}' valueTruncated={e.ValueTruncated} focusable={e.IsKeyboardFocusable} focused={e.HasKeyboardFocus} " +
        $"enabled={e.IsEnabled} offscreen={e.IsOffscreen} localizedType='{e.LocalizedControlType}' help='{e.HelpText}' " +
        $"accessKey='{e.AccessKey}' acceleratorKey='{e.AcceleratorKey}' ref={e.Ref != null}";
}
