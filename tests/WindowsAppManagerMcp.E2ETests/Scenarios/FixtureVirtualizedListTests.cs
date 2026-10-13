using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Virtualized list items (roadmap M3.10) against the fixture's 10,000-item WPF ListBox, which realizes only the rows in view
/// and a small cache: find_ui_elements' searchVirtualized finds a row far down through the list's ItemContainer pattern without
/// realizing it (no-input tier: it only reads), and element_action realize makes it real and scrolls it into view, by its ref,
/// by criteria, and by scrolling page by page when the criteria cannot be asked of the list (input tier: they change what the
/// list shows). The harness's guard lets the server act only on the fixture's elements.
/// </summary>
public class FixtureVirtualizedListTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A row far below the realized ones.</summary>
    private static readonly string FarItem = FixtureContract.VirtualListItem(7500);

    private readonly ITestOutputHelper _output;

    public FixtureVirtualizedListTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ElementQuery FarItemQuery(nint window, bool searchVirtualized) =>
        new(window, Name: FarItem, ControlTypes: ["ListItem"], Timeout: Timeout) { SearchVirtualized = searchVirtualized };

    private void Log(FindElementsResult result)
    {
        _output.WriteLine($"matchCount={result.MatchCount} truncated={result.TruncationReason} error={result.ErrorCode}: {result.Error}");
        foreach (var element in result.Elements ?? [])
        {
            _output.WriteLine($"  {element.Ref} {element.ControlType} '{element.Name}' offscreen={element.IsOffscreen} " +
                              $"virtualized={element.Virtualized} bounds={element.Bounds}");
        }

        if (result.Virtualized is { } info)
        {
            _output.WriteLine($"  virtualized: itemsFound={info.ItemsFound} note={info.Note} lists=" +
                              string.Join(", ", info.Containers.Select(c => $"{c.Ref} {c.ControlType} '{c.Name}' itemContainer={c.ItemContainer}")));
        }
    }

    /// <summary>element_action's result, read back from the tool result's JSON text block.</summary>
    private static ElementActionResult Payload(CallToolResult result) =>
        JsonSerializer.Deserialize<ElementActionResult>(result.Content.OfType<TextContentBlock>().Single().Text, ToolJson.SerializerOptions)!;

    private void Log(ElementActionResult result) =>
        _output.WriteLine($"  success={result.Success} method={result.Method} verified={result.OutcomeVerified} before={result.Before} " +
                          $"after={result.After} warnings={string.Join(" | ", result.Warnings ?? [])} error={result.ErrorCode}: {result.Error}");

    [DesktopNoInputFact]
    public async Task SearchVirtualized_FindsARowTheListHasNotRealized_WithARef_AndRealizesNothing()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var search = e2e.Server.Get<IElementSearchService>();
        var window = e2e.Fixture.MainWindowHandle;

        // Act
        var plain = await search.FindAsync(FarItemQuery(window, searchVirtualized: false));
        var virtualized = await search.FindAsync(FarItemQuery(window, searchVirtualized: true));
        Log(virtualized);
        var after = await search.FindAsync(FarItemQuery(window, searchVirtualized: false));

        // Assert
        plain.MatchCount.Should().Be(0, "the row is not realized, so it is not in the tree");
        virtualized.Success.Should().BeTrue(virtualized.Error);
        var row = virtualized.Elements.Should().ContainSingle().Subject;
        row.Virtualized.Should().BeTrue();
        row.Ref.Should().NotBeNull();
        virtualized.Virtualized!.Containers.Should().Contain(c => c.ItemContainer && c.ControlType == "List");
        after.MatchCount.Should().Be(0, "searchVirtualized only reads: the row is still not realized");
    }

    [DesktopFact]
    public async Task Realize_ARowFoundBySearchVirtualized_PutsItOnScreen_WhereAPlainSearchFindsIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var search = e2e.Server.Get<IElementSearchService>();
        var window = e2e.Fixture.MainWindowHandle;
        var found = await search.FindAsync(FarItemQuery(window, searchVirtualized: true));
        Log(found);
        var rowRef = found.Elements.Should().ContainSingle().Subject.Ref!;

        // Act
        var result = await e2e.Server.Get<IElementActionService>().ActAsync(new ElementActionRequest(rowRef, ElementActionKind.Realize, Timeout: Timeout));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.OutcomeVerified.Should().BeTrue();
        var plain = await search.FindAsync(FarItemQuery(window, searchVirtualized: false));
        Log(plain);
        plain.Elements.Should().ContainSingle().Which.ClickPoint.Should().NotBeNull("the row is real and in view now");
    }

    [DesktopFact]
    public async Task Realize_ByCriteria_FindsTheRowThroughTheList_AndPutsItOnScreen()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var tools = e2e.Server.Get<ElementActionTools>();

        // Act
        var result = Payload(await tools.ElementAction(ElementActionKind.Realize, handle: e2e.Fixture.MainWindowHandle,
            name: FixtureContract.VirtualListItem(9000), controlType: "ListItem", timeoutMs: 10_000));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.OutcomeVerified.Should().BeTrue();
    }

    [DesktopFact]
    public async Task Realize_ByANameTheListCannotBeAskedFor_WithMaxScrolls_ScrollsPageByPage_UntilTheRowIsFound()
    {
        // Arrange - nameMatch contains cannot be asked of ItemContainer, so only scrolling finds a row beyond the realized ones
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var tools = e2e.Server.Get<ElementActionTools>();

        // Act
        var result = Payload(await tools.ElementAction(ElementActionKind.Realize, handle: e2e.Fixture.MainWindowHandle,
            name: "Item 00150", nameMatch: ElementNameMatch.Contains, controlType: "ListItem", maxScrolls: 40, timeoutMs: 30_000));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().Contain(w => w.Contains("pages"));
        result.OutcomeVerified.Should().BeTrue();
    }
}
