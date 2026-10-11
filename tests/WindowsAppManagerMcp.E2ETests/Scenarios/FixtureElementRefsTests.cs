namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// find_ui_elements and element refs (roadmap M3.4) against the fixture: the provider-side search finds the fixture's
/// controls by automation id and control type, each with a click point inside its bounds, and a ref that the tree walk
/// shares and that resolves to the live element. No-input tier: it only reads.
/// </summary>
public class FixtureElementRefsTests
{
    private readonly ITestOutputHelper _output;

    public FixtureElementRefsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ElementQuery Query(nint window, string? automationId = null, string[]? controlTypes = null, string? withinRef = null) =>
        new(window, withinRef, AutomationId: automationId, ControlTypes: controlTypes, Detail: ElementDetail.Standard,
            Timeout: TimeSpan.FromSeconds(10));

    [DesktopNoInputFact]
    public async Task FindUiElements_ByAutomationId_GivesTheRefTheTreeWalkGivesTheSameElement()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = e2e.Fixture.MainWindowHandle;
        var search = e2e.Server.Get<IElementSearchService>();

        // Act
        var found = await search.FindAsync(Query(window, automationId: FixtureContract.Ids.OpenModalButton));
        var tree = await e2e.Server.UiElements.GetUIElementsAsync(window);

        // Assert
        found.Success.Should().BeTrue(found.Error);
        var button = found.Elements.Should().ContainSingle().Which;
        _output.WriteLine($"{button.Ref} {button.ControlType} '{button.Name}' click at ({button.ClickPoint?.X}, {button.ClickPoint?.Y})");
        button.ControlType.Should().Be("Button");
        button.Patterns.Should().Contain("Invoke");
        UiaReads.Flatten(tree.Elements).Single(e => e.AutomationId == FixtureContract.Ids.OpenModalButton).Ref
            .Should().Be(button.Ref, "the same element keeps its ref in every read");
        var resolution = await e2e.Server.Get<IElementRegistry>().ResolveAsync(button.Ref!, TimeSpan.FromSeconds(10), CancellationToken.None);
        resolution.Success.Should().BeTrue(resolution.Error);
        resolution.Element!.Via.Should().Be(ElementResolutionPath.Live);
    }

    [DesktopNoInputFact]
    public async Task FindUiElements_ClickPoints_AreInsideTheElementsBounds()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var search = e2e.Server.Get<IElementSearchService>();

        // Act
        var found = await search.FindAsync(Query(e2e.Fixture.MainWindowHandle, controlTypes: ["Edit", "CheckBox", "Button"]));

        // Assert
        found.Success.Should().BeTrue(found.Error);
        found.Elements.Should().NotBeEmpty();
        foreach (var element in found.Elements!)
        {
            _output.WriteLine($"{element.Ref} {element.ControlType} aid={element.AutomationId} bounds={element.Bounds} click={element.ClickPoint}");
            element.Ref.Should().NotBeNull();
            if (element.ClickPoint is { } point && element.Bounds is { } b)
            {
                point.X.Should().BeInRange(b.X, b.X + b.Width, "the click point of {0}", element.Ref);
                point.Y.Should().BeInRange(b.Y, b.Y + b.Height, "the click point of {0}", element.Ref);
            }
        }

        found.Elements.Should().Contain(e => e.AutomationId == FixtureContract.Ids.CheckBox && e.ToggleState != null,
            "detail standard reads a check box's state");
    }
}
