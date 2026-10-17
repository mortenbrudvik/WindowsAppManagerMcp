namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The visual signature (roadmap M6.4): a hash per 16 x 16 tile that changes with any pixel's blue, green or red, and never
/// with the undefined 4th byte. Pure; no screen is read.
/// </summary>
public class VisualSignatureTests
{
    [Fact]
    public void Of_HasOneHashPerTile_WithPartTilesAtTheEdges_AndTheImagesPlace()
    {
        var signature = new VisualFrames(40, 17, originX: -1920, originY: 300).Signature();

        signature.Columns.Should().Be(3);
        signature.Rows.Should().Be(2);
        signature.Hashes.Length.Should().Be(6);
        signature.Region.Should().Be(new CapturedRegion(-1920, 300, 40, 17));
    }

    [Fact]
    public void Of_TheSamePixels_GivesTheSameHashes_WhateverTheFourthByte()
    {
        // Arrange - painting again in the same color leaves new random 4th bytes
        var first = new VisualFrames(64, 48, color: (10, 20, 30));
        var second = new VisualFrames(64, 48, color: (10, 20, 30));

        // Act / Assert
        first.Signature().Hashes.ToArray().Should().Equal(second.Signature().Hashes.ToArray());
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(15, 15, 0)]
    [InlineData(16, 0, 1)]
    [InlineData(39, 16, 5)]
    public void Of_OnePixelChanged_ChangesOnlyItsTile(int x, int y, int tile)
    {
        // Arrange - 3 x 2 tiles, the last column 8 pixels wide and the last row 1 pixel high
        var before = new VisualFrames(40, 17);
        var after = before.Copy().Paint(x, y, 1, 1, (0xF0, 0xF0, 0xF1));

        // Act
        var a = before.Signature().Hashes.ToArray();
        var b = after.Signature().Hashes.ToArray();

        // Assert
        Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).Should().Equal(tile);
    }

    [Fact]
    public void Of_AHueChangeThatKeepsTheBrightness_ChangesTheTile()
    {
        // Red and blue swapped: about the same luma, which a mean-luma signature would miss
        var before = new VisualFrames(16, 16).Paint(4, 4, 8, 8, (200, 60, 90));
        var after = before.Copy().Paint(4, 4, 8, 8, (90, 60, 200));

        before.Signature().Hashes[0].Should().NotBe(after.Signature().Hashes[0]);
    }

    [Fact]
    public void Of_PixelsSwappedWithinATile_ChangesIt()
    {
        var before = new VisualFrames(16, 16).Paint(0, 0, 1, 1, (1, 2, 3)).Paint(1, 0, 1, 1, (4, 5, 6));
        var after = new VisualFrames(16, 16).Paint(0, 0, 1, 1, (4, 5, 6)).Paint(1, 0, 1, 1, (1, 2, 3));

        before.Signature().Hashes[0].Should().NotBe(after.Signature().Hashes[0]);
    }

    [Fact]
    public void Of_ALonePixelAtAnOddEdge_IsNotTheSameAsABlackOne()
    {
        // A 17-pixel row ends in a tile of one pixel: black there must still differ from another color
        var black = new VisualFrames(17, 1, color: (0, 0, 0));
        var colored = black.Copy().Paint(16, 0, 1, 1, (0, 0, 1));

        black.Signature().Hashes[1].Should().NotBe(colored.Signature().Hashes[1]);
    }

    [Fact]
    public void Of_ACropWithTheParentsStride_IsTheSignatureOfTheCroppedPixels()
    {
        // Arrange - a 20 x 20 crop at (8, 8) of a 48 x 40 frame, as a crop to a window's visible frame lends it
        var parent = new VisualFrames(48, 40, originX: 100, originY: 200).Paint(10, 12, 5, 5, (1, 200, 3));
        var cropped = new VisualFrames(20, 20, originX: 108, originY: 208).Paint(2, 4, 5, 5, (1, 200, 3));

        // Act
        var parentCapture = parent.Capture();
        var fromCrop = VisualSignature.Of(PixelView.Of(parentCapture).Crop(108, 208, 20, 20));

        // Assert
        fromCrop.Region.Should().Be(new CapturedRegion(108, 208, 20, 20));
        fromCrop.Hashes.ToArray().Should().Equal(cropped.Signature().Hashes.ToArray());
    }

    [Fact]
    public void Uniform_IsTrueOnlyWhenEveryPixelHasOneColor()
    {
        var black = new VisualFrames(33, 33, color: (0, 0, 0));

        black.Signature().Uniform.Should().BeTrue();
        black.Copy().Paint(32, 32, 1, 1, (0, 0, 1)).Signature().Uniform.Should().BeFalse();
        black.Copy().Paint(1, 0, 1, 1, (0, 1, 0)).Signature().Uniform.Should().BeFalse();
    }

    [Fact]
    public void ByteSize_GrowsWithTheTiles()
    {
        var small = new VisualFrames(16, 16).Signature();
        var large = new VisualFrames(160, 160).Signature();

        large.ByteSize.Should().Be(small.ByteSize + 99 * sizeof(ulong));
    }
}
