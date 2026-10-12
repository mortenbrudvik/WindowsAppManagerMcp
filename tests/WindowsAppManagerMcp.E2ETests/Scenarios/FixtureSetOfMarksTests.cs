using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Roadmap M3.11, part 2, against the fixture: capture_with_elements marks the fixture's actionable controls with the
/// refs find_ui_elements gives them, each legend centre converts back through the captureId to a point inside its control,
/// and capture_window's ref crop cuts the image to one control plus padding. No-input tier: it only captures and reads.
/// </summary>
public class FixtureSetOfMarksTests
{
    private readonly ITestOutputHelper _output;

    public FixtureSetOfMarksTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static JsonElement Metadata(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Last().Text).RootElement;

    [DesktopNoInputFact]
    public async Task CaptureWithElements_MarksTheButtonWithItsRef_AndItsCenterConvertsIntoIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = e2e.Fixture.MainWindowHandle;
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(new ElementQuery(window, null,
            AutomationId: FixtureContract.Ids.OpenModalButton, Timeout: TimeSpan.FromSeconds(10)));
        found.Success.Should().BeTrue(found.Error);
        var button = found.Elements.Should().ContainSingle().Which;

        // Act
        var result = await e2e.Server.Get<ScreenshotTools>().CaptureWithElements(handle: (long)window, maxDimension: 600);

        // Assert
        result.IsError.Should().NotBe(true);
        var metadata = Metadata(result);
        var legend = metadata.GetProperty("legend").GetString()!.Split('\n');
        legend.ToList().ForEach(_output.WriteLine);
        var line = legend.Single(l => l.StartsWith($"[{button.Ref}] ", StringComparison.Ordinal));
        line.Should().Contain($"#{FixtureContract.Ids.OpenModalButton}");
        legend.Should().NotContain(l => l.Contains($"#{FixtureContract.Ids.StatusText}"), "a label has no action pattern");

        var center = line[(line.IndexOf("center=(", StringComparison.Ordinal) + 8)..line.LastIndexOf(')')].Split(',').Select(int.Parse).ToArray();
        var point = e2e.Server.Get<ICoordinateSpaceResolver>().FromImage(metadata.GetProperty("captureId").GetString()!, center[0], center[1]);
        point.Resolved.Should().BeTrue(point.Error);
        var bounds = button.Bounds!;
        point.X.Should().BeInRange(bounds.X, bounds.X + bounds.Width - 1);
        point.Y.Should().BeInRange(bounds.Y, bounds.Y + bounds.Height - 1);
    }

    [DesktopNoInputFact]
    public async Task CaptureWindow_WithARef_IsCroppedToTheElementAndPadding()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = e2e.Fixture.MainWindowHandle;
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(new ElementQuery(window, null,
            AutomationId: FixtureContract.Ids.EditBox, Timeout: TimeSpan.FromSeconds(10)));
        found.Success.Should().BeTrue(found.Error);
        var edit = found.Elements.Should().ContainSingle().Which;

        // Act
        var result = await e2e.Server.Get<ScreenshotTools>().CaptureWindow(@ref: edit.Ref, padding: 4, maxDimension: 0);

        // Assert - the edit box is inside the window, so nothing is clipped
        result.IsError.Should().NotBe(true);
        var metadata = Metadata(result);
        _output.WriteLine(metadata.ToString());
        var region = metadata.GetProperty("capturedRegion");
        var bounds = edit.Bounds!;
        region.GetProperty("x").GetInt32().Should().Be(bounds.X - 4);
        region.GetProperty("y").GetInt32().Should().Be(bounds.Y - 4);
        region.GetProperty("width").GetInt32().Should().Be(bounds.Width + 8);
        region.GetProperty("height").GetInt32().Should().Be(bounds.Height + 8);
        metadata.GetProperty("ref").GetString().Should().Be(edit.Ref);
    }
}
