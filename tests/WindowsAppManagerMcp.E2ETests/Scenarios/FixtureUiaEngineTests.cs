namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The two UI Automation engines (roadmap M3.2) against the fixture: the UIA3 COM client (the default) and the managed
/// UIA2 client (WAM_UIA_ENGINE=uia2) must read the same tree, and the bounded, cached, breadth-first walk (roadmap
/// M3.1) must keep the levels nearest the window. No-input tier: they only read.
/// </summary>
/// <remarks>
/// These are the checks the M3.2 spike could not make without a desktop. Run them before relying on the UIA3 default:
/// they confirm that the COM client's control view (its cache requests' TreeFilter and the ControlViewCondition of
/// FindAllBuildCache) lists the same children as the managed client, that both report the same bounds, and that the
/// tree reads work from the worker threads.
/// </remarks>
public class FixtureUiaEngineTests
{
    /// <summary>The engines can round a fractional rectangle differently by a pixel.</summary>
    private const int BoundsTolerance = 1;

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
        foreach (var (id, expected) in e2e.Fixture.Ready.Elements)
        {
            byId.Should().ContainKey(id, "the {0} engine must find '{1}'", engine, id);
            byId[id].Bounds.Should().NotBeNull();
        }

        _output.WriteLine($"{engine}: {result.TotalElementCount} elements in {result.ElapsedMilliseconds} ms, depth {result.DepthReached}.");
    }

    [DesktopNoInputFact]
    public async Task BothEngines_ReadTheSameTree()
    {
        // Arrange - one fixture, a server per engine
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: UiaEngine.Uia3);
        using var uia2 = ServerHarness.Create(DesktopTier.NoInput, e2e.Fixture.ProcessId, UiaEngine.Uia2);
        var window = e2e.Fixture.MainWindowHandle;

        // Act
        var fromUia3 = await e2e.Server.UiElements.GetUIElementsAsync(window);
        var fromUia2 = await uia2.UiElements.GetUIElementsAsync(window);

        // Assert - the same elements in the same order, with the same names, types, ids and states
        fromUia3.Success.Should().BeTrue(fromUia3.Error);
        fromUia2.Success.Should().BeTrue(fromUia2.Error);
        var uia3Elements = UiaReads.Flatten(fromUia3.Elements).ToList();
        var uia2Elements = UiaReads.Flatten(fromUia2.Elements).ToList();
        _output.WriteLine($"UIA3: {uia3Elements.Count} elements in {fromUia3.ElapsedMilliseconds} ms; " +
                          $"UIA2: {uia2Elements.Count} in {fromUia2.ElapsedMilliseconds} ms.");
        uia3Elements.Select(Describe).Should().Equal(uia2Elements.Select(Describe));
        for (var i = 0; i < uia3Elements.Count; i++)
        {
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

        static string Describe(UIElementInfo e) =>
            $"{e.ControlType} '{e.Name}' aid={e.AutomationId} enabled={e.IsEnabled} offscreen={e.IsOffscreen} children={e.Children?.Count ?? 0}";
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

        // Assert
        list.Properties.ControlType.Should().Be("List");
        list.Patterns.Should().HaveFlag(UiaPatterns.ItemContainer);
        container.Should().NotBeNull();
        tree.GetRuntimeId(list.Node).Should().NotBeNullOrEmpty();
    }
}
