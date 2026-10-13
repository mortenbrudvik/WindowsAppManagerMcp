namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Roadmap M3.11 against the fixture: a capture's captureId converts points of its (scaled) image back to the screen
/// points they show, and client points convert from the real client area. No-input tier: the conversion only reads
/// windows; nothing is clicked.
/// </summary>
public class FixtureCaptureIdTests
{
    private readonly ITestOutputHelper _output;

    public FixtureCaptureIdTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void ACaptureId_ConvertsImagePointsToTheSwatchesTheyShow()
    {
        // Arrange - a scaled client capture, so the conversion has to undo the scale and the client origin
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;
        var metadata = e2e.Server.Screenshots.CaptureWindow(
            e2e.Fixture.MainWindowHandle, includeFrame: false, new CaptureOptions { Format = "png", MaxDimension = 400 }).Metadata;
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.CaptureId.Should().NotBeNullOrEmpty();
        var transform = metadata.ImageToScreen!;
        var resolver = e2e.Server.Get<ICoordinateSpaceResolver>();

        foreach (var swatch in FixtureContract.Swatches)
        {
            // Act - the image point that shows the swatch's centre
            var center = ready.Element(swatch.Id);
            var imageX = (int)Math.Round((center.CenterX - transform.OffsetX) / transform.ScaleX);
            var imageY = (int)Math.Round((center.CenterY - transform.OffsetY) / transform.ScaleY);
            var point = resolver.FromImage(metadata.CaptureId!, imageX, imageY);

            // Assert - back within one image pixel of the centre, on the captured window, and not stale
            _output.WriteLine($"{swatch.Id}: centre ({center.CenterX}, {center.CenterY}), image ({imageX}, {imageY}), " +
                              $"converted ({point.X}, {point.Y}) {point.ErrorCode}");
            point.Resolved.Should().BeTrue(point.Error);
            point.X.Should().BeCloseTo(center.CenterX, (uint)Math.Ceiling(transform.ScaleX));
            point.Y.Should().BeCloseTo(center.CenterY, (uint)Math.Ceiling(transform.ScaleY));
            point.CapturedWindow.Should().Be(e2e.Fixture.MainWindowHandle);
            point.Recheck!().Should().BeNull();
        }
    }

    [DesktopNoInputFact]
    public void ClientPoints_AreRelativeToTheClientArea()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;
        var swatch = ready.Element(FixtureContract.Swatches[0].Id);

        // Act
        var point = e2e.Server.Get<ICoordinateSpaceResolver>().FromWindow(
            CoordinateSpace.Client, e2e.Fixture.MainWindowHandle, swatch.CenterX - ready.ClientRect.X, swatch.CenterY - ready.ClientRect.Y);

        // Assert
        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((swatch.CenterX, swatch.CenterY));
    }
}
