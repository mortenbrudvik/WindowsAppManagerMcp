using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The pointer in captures (roadmap M7.1b), in the no-input tier: it only reads the screen around the pointer, wherever the
/// user left it. A capture reports where the pointer is, and with includeCursor its shape is drawn into the pixels, which then
/// differ from the same capture without it; nothing moves the pointer.
/// </summary>
public class FixturePointerCaptureTests
{
    private readonly ITestOutputHelper _output;

    public FixturePointerCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void IncludeCursor_DrawsThePointerIntoTheImage_WhereItIs()
    {
        // Arrange - a small region with the pointer in its middle
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var screenshots = e2e.Server.Get<IScreenshotService>();
        var pointer = e2e.Server.Get<IScreenCaptureWrapper>().GetPointer();
        pointer.Should().NotBeNull("the pointer's position can always be read");
        _output.WriteLine($"Pointer: {pointer}");
        if (pointer is not { Visible: true } at)
        {
            _output.WriteLine("The pointer is hidden (touch or pen input), so there is nothing to draw; only its position is checked.");
            return;
        }

        var options = new CaptureOptions { Format = "png", MaxDimension = 0 };

        // Act
        var without = screenshots.CaptureRegion(at.X - 32, at.Y - 32, 64, 64, options);
        var with = screenshots.CaptureRegion(at.X - 32, at.Y - 32, 64, 64, options with { IncludeCursor = true });

        // Assert - both report the pointer; only the second drew it, so their images differ
        without.Metadata.Cursor.Should().NotBeNull();
        without.Metadata.Cursor!.Drawn.Should().BeNull();
        with.Metadata.Cursor!.Drawn.Should().BeTrue("the pointer is shown and inside the region");
        with.Image!.Data.Should().NotEqual(without.Image!.Data, "the pointer's shape is drawn into the pixels");
    }
}
