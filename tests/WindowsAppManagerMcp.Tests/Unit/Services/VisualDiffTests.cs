using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Comparing two visual signatures (roadmap M6.4): the changed tiles as a share, merged into at most eight rectangles in
/// screen pixels, with excluded rectangles left out. Pure.
/// </summary>
public class VisualDiffTests
{
    [Fact]
    public void Compare_TheSameFrame_ChangesNothing()
    {
        var frame = new VisualFrames(160, 90, 100, 200);

        var change = VisualDiff.Compare(frame.Signature(), frame.Copy().Signature())!;

        change.ChangedTiles.Should().Be(0);
        change.ComparedTiles.Should().Be(10 * 6);
        change.ChangedPercent.Should().Be(0);
        change.Regions.Should().BeEmpty();
    }

    [Fact]
    public void Compare_DifferentSizes_IsNull()
    {
        VisualDiff.Compare(new VisualFrames(160, 90).Signature(), new VisualFrames(161, 90).Signature()).Should().BeNull();
    }

    [Fact]
    public void Compare_OneChangedSpot_IsItsTilesOnTheScreen()
    {
        // Arrange - a 40 x 40 panel at (300, 20) of a 640 x 480 window at (100, 200): tiles 18-21 by 1-3
        var before = new VisualFrames(640, 480, 100, 200);
        var after = before.Copy().Paint(300, 20, 40, 40, (0x60, 0x20, 0xA0));

        // Act
        var change = VisualDiff.Compare(before.Signature(), after.Signature())!;

        // Assert
        change.ChangedTiles.Should().Be(4 * 3);
        change.ChangedPercent.Should().Be(1);
        change.Regions.Should().Equal(new BoundsDto(100 + 288, 200 + 16, 64, 48));
    }

    [Fact]
    public void Compare_TilesThatTouchAtACorner_AreOneRegion_AndSeparateSpotsAreTwo_LargestFirst()
    {
        var before = new VisualFrames(160, 160);
        var after = before.Copy()
            .Paint(0, 0, 1, 1, (0, 0, 0)) // tile (0, 0)
            .Paint(16, 16, 1, 1, (0, 0, 0)) // tile (1, 1): diagonal to (0, 0)
            .Paint(128, 128, 32, 32, (0, 0, 0)); // tiles (8..9, 8..9)

        var change = VisualDiff.Compare(before.Signature(), after.Signature())!;

        change.Regions.Should().Equal(new BoundsDto(128, 128, 32, 32), new BoundsDto(0, 0, 32, 32));
    }

    [Fact]
    public void Compare_ManySpots_AreAtMostEightRegions_TheLastTheUnionOfTheSmallest()
    {
        // Arrange - one 2-tile spot and ten 1-tile spots, two tiles apart
        var before = new VisualFrames(400, 64);
        var after = before.Copy().Paint(0, 0, 32, 16, (0, 0, 0));
        for (var i = 0; i < 10; i++)
        {
            after.Paint(64 + i * 32, 32, 1, 1, (0, 0, 0));
        }

        // Act
        var regions = VisualDiff.Compare(before.Signature(), after.Signature())!.Regions;

        // Assert
        regions.Should().HaveCount(VisualChange.MaxRegions);
        regions[0].Should().Be(new BoundsDto(0, 0, 32, 16));
        regions[1].Should().Be(new BoundsDto(64, 32, 16, 16));
        regions[^1].Should().Be(new BoundsDto(64 + 6 * 32, 32, 3 * 32 + 16, 16), "the last four spots joined");
    }

    [Fact]
    public void Compare_TheEdgeTiles_AreClippedToTheImage()
    {
        var before = new VisualFrames(40, 17, 10, 10);
        var after = before.Copy().Paint(39, 16, 1, 1, (0, 0, 0));

        VisualDiff.Compare(before.Signature(), after.Signature())!.Regions.Should().Equal(new BoundsDto(10 + 32, 10 + 16, 8, 1));
    }

    [Fact]
    public void Compare_LeavesOutTheTilesAnExcludedRectangleTouches()
    {
        // Arrange - a caret 2 x 18 at screen (133, 210) spans tiles (2, 0) and (2, 1) of a frame at (100, 200)
        var before = new VisualFrames(160, 64, 100, 200);
        var after = before.Copy().Paint(33, 10, 2, 18, (0, 0, 0));

        // Act
        var change = VisualDiff.Compare(before.Signature(), after.Signature(), [new Rectangle(133, 210, 2, 18)])!;

        // Assert
        change.ChangedTiles.Should().Be(0);
        change.ComparedTiles.Should().Be(10 * 4 - 2);
    }

    [Fact]
    public void Compare_AWindowThatMoved_ComparesByContent_AndReportsWhereItIsNow()
    {
        var before = new VisualFrames(160, 64, 100, 200);
        var after = before.Copy(originX: 400, originY: 50).Paint(0, 0, 1, 1, (0, 0, 0));

        var change = VisualDiff.Compare(before.Signature(), after.Signature())!;

        change.ChangedTiles.Should().Be(1);
        change.Regions.Should().Equal(new BoundsDto(400, 50, 16, 16));
    }

    [Fact]
    public void Compare_AnExcludedRectangle_CountsWhereEitherFrameHadIt()
    {
        // The window moved by 100 pixels; the caret's screen rectangle covers tile 0 of the old place and nothing of the new
        var before = new VisualFrames(160, 64, 0, 0);
        var after = before.Copy(originX: 100).Paint(0, 0, 1, 1, (0, 0, 0));

        var change = VisualDiff.Compare(before.Signature(), after.Signature(), [new Rectangle(0, 0, 4, 4)])!;

        change.ChangedTiles.Should().Be(0);
    }

    [Fact]
    public void Compare_OneTileOfAHugeImage_StillReadsAsAChange()
    {
        var before = new VisualFrames(3840, 2160);
        var after = before.Copy().Paint(0, 0, 1, 1, (0, 0, 0));

        VisualDiff.Compare(before.Signature(), after.Signature())!.ChangedPercent.Should().Be(0.01);
    }
}
