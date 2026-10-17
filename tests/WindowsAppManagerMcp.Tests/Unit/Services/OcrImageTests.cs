using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>The pixel work before Windows OCR (roadmap M6.3): the 2x enlargement, the tiles and the crops.</summary>
public class OcrImageTests
{
    [Fact]
    public void Upscale2x_DoublesBothEdges_AndKeepsAFlatColor()
    {
        var bgra = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 3 * 2).SelectMany(p => p).ToArray();

        var result = OcrImage.Upscale2x(bgra, 3, 2);

        result.Should().HaveCount(6 * 4 * 4);
        result.Chunk(4).Should().OnlyContain(p => p[0] == 10 && p[1] == 20 && p[2] == 30);
    }

    [Fact]
    public void Upscale2x_InterpolatesBetweenNeighbours_WithAlignedCentres()
    {
        // One row: black, then white. Output pixel 0 lies on the black pixel's outer quarter, 1 a quarter towards the white
        // one, 2 three quarters, 3 on the white pixel's outer quarter
        byte[] bgra = [0, 0, 0, 0, 200, 200, 200, 0];

        var result = OcrImage.Upscale2x(bgra, 2, 1);

        result.Chunk(4).Take(4).Select(p => p[0]).Should().Equal(0, 50, 150, 200);
        result.Chunk(4).Skip(4).Select(p => p[0]).Should().Equal([0, 50, 150, 200], "the second output row repeats the only source row");
    }

    [Fact]
    public void Upscale2x_RefusesABufferShorterThanTheImage()
    {
        var act = () => OcrImage.Upscale2x(new byte[10], 2, 2);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tiles_AnImageWithinTheLimit_IsOneTileWhoseCoreIsTheWholeImage()
    {
        var tiles = OcrImage.Tiles(3840, 2160, 10_000);

        tiles.Should().ContainSingle().Which.Should().Be(new OcrTile(new Rectangle(0, 0, 3840, 2160), new Rectangle(0, 0, 3840, 2160)));
    }

    [Theory]
    [InlineData(12_000, 1_000, 10_000)]
    [InlineData(16_384, 3_000, 10_000)]
    [InlineData(25_000, 25_000, 10_000)]
    [InlineData(5_000, 900, 2_600)]
    public void Tiles_ALargerImage_OverlapsWithinTheLimit_AndTheCoresCoverItOnce(int width, int height, int max)
    {
        var tiles = OcrImage.Tiles(width, height, max);

        tiles.Count.Should().BeGreaterThan(1);
        tiles.Should().OnlyContain(t => t.Area.Width <= max && t.Area.Height <= max && t.Area.Contains(t.Core));
        tiles.Should().OnlyContain(t => t.Area.Left >= 0 && t.Area.Top >= 0 && t.Area.Right <= width && t.Area.Bottom <= height);

        // Every pixel lies in exactly one core: the cores' areas add up to the image, and no two overlap
        tiles.Sum(t => (long)t.Core.Width * t.Core.Height).Should().Be((long)width * height);
        foreach (var (a, i) in tiles.Select((t, i) => (t, i)))
        {
            tiles.Skip(i + 1).Where(b => a.Core.IntersectsWith(b.Core)).Should().BeEmpty();
        }

        // Neighbouring tiles overlap by the full overlap, so a word on a core's edge lies whole in one tile
        var columns = tiles.Select(t => t.Area.X).Distinct().Order().ToList();
        for (var c = 1; c < columns.Count; c++)
        {
            var before = tiles.First(t => t.Area.X == columns[c - 1]);
            (before.Area.Right - columns[c]).Should().Be(OcrImage.TileOverlap);
        }
    }

    [Theory]
    [InlineData(25_000, 3_000, 10_000)]
    [InlineData(12_000, 1_000, 10_000)]
    public void Tiles_AWordAsWideAsTheOverlap_WithItsCentreInACore_LiesWholeInThatTile(int width, int height, int max)
    {
        var tiles = OcrImage.Tiles(width, height, max);
        const int half = OcrImage.TileOverlap / 2;

        // A word of TileOverlap pixels centred on the first and the last column of each core (the worst cases)
        foreach (var tile in tiles)
        {
            foreach (var centre in new[] { tile.Core.Left, tile.Core.Right - 1 })
            {
                var (left, right) = (Math.Max(0, centre - half), Math.Min(width, centre + half));
                (left >= tile.Area.Left && right <= tile.Area.Right).Should().BeTrue(
                    $"a word at {left}-{right} centred in the core {tile.Core} must lie in the tile {tile.Area}");
            }
        }
    }

    [Fact]
    public void Crop_CopiesTheRowsOfTheArea()
    {
        // 3 x 2 image whose pixel (x, y) has blue = 10 * y + x
        var bgra = Enumerable.Range(0, 6).SelectMany(i => new[] { (byte)(10 * (i / 3) + i % 3), (byte)0, (byte)0, (byte)0 }).ToArray();

        var crop = OcrImage.Crop(bgra, 3, new Rectangle(1, 0, 2, 2));

        crop.Chunk(4).Select(p => p[0]).Should().Equal(1, 2, 11, 12);
    }
}
