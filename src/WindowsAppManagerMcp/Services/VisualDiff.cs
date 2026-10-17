using System.Drawing;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// How two signatures of the same size differ (roadmap M6.4): the tiles whose hashes differ, as a share of the tiles
/// compared, and where they are on the screen, merged into at most <see cref="MaxRegions"/> rectangles.
/// </summary>
/// <param name="ChangedTiles">Tiles whose hash differs.</param>
/// <param name="ComparedTiles">Tiles compared: all of them but the excluded ones.</param>
/// <param name="ChangedPercent">ChangedTiles of ComparedTiles, in percent; 0 when nothing was compared.</param>
/// <param name="Regions">The changed tiles merged into rectangles of touching tiles (diagonals touch), in screen pixels of
/// the newer signature, clipped to it, largest first; when there are more than <see cref="MaxRegions"/>, the last one is
/// the union of the smallest.</param>
public sealed record VisualChange(int ChangedTiles, int ComparedTiles, double ChangedPercent, IReadOnlyList<BoundsDto> Regions)
{
    /// <summary>The most rectangles a change is reported as.</summary>
    public const int MaxRegions = 8;
}

/// <summary>Compares two <see cref="VisualSignature"/>s tile by tile (roadmap M6.4). Pure; no pixels are needed.</summary>
public static class VisualDiff
{
    /// <summary>
    /// The tiles of <paramref name="after"/> that differ from <paramref name="before"/>, leaving out every tile that touches
    /// an <paramref name="excluded"/> screen rectangle (a text caret, a window the policy denies) where either signature
    /// had it. Tiles are compared by their place in the image, so a window that moved without resizing compares by content.
    /// Null when the sizes differ: the images do not compare tile by tile.
    /// </summary>
    public static VisualChange? Compare(VisualSignature before, VisualSignature after, IReadOnlyList<Rectangle>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!before.SameSizeAs(after))
        {
            return null;
        }

        var columns = after.Columns;
        var rows = after.Rows;
        var skipped = ExcludedTiles(before, excluded);
        if (before.OriginX != after.OriginX || before.OriginY != after.OriginY)
        {
            var moved = ExcludedTiles(after, excluded);
            if (moved != null)
            {
                skipped = skipped ?? new bool[moved.Length];
                for (var i = 0; i < moved.Length; i++)
                {
                    skipped[i] |= moved[i];
                }
            }
        }

        var changed = new bool[columns * rows];
        int changedTiles = 0, comparedTiles = 0;
        var a = before.Hashes;
        var b = after.Hashes;
        for (var i = 0; i < changed.Length; i++)
        {
            if (skipped?[i] == true)
            {
                continue;
            }

            comparedTiles++;
            if (a[i] != b[i])
            {
                changed[i] = true;
                changedTiles++;
            }
        }

        var percent = comparedTiles == 0 ? 0 : Math.Round(changedTiles * 100.0 / comparedTiles, 2);
        if (changedTiles > 0 && percent == 0)
        {
            // One tile of a very large image still reads as a change
            percent = 0.01;
        }

        return new VisualChange(changedTiles, comparedTiles, percent, changedTiles == 0 ? [] : Regions(after, changed));
    }

    /// <summary>The whole image of <paramref name="signature"/> as one region: what a resize changed.</summary>
    public static IReadOnlyList<BoundsDto> Whole(VisualSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return [new BoundsDto(signature.OriginX, signature.OriginY, signature.Width, signature.Height)];
    }

    /// <summary>The tiles of <paramref name="signature"/> that touch an excluded rectangle where it was; null when none do.</summary>
    private static bool[]? ExcludedTiles(VisualSignature signature, IReadOnlyList<Rectangle>? excluded)
    {
        if (excluded is not { Count: > 0 })
        {
            return null;
        }

        bool[]? tiles = null;
        var image = new Rectangle(signature.OriginX, signature.OriginY, signature.Width, signature.Height);
        foreach (var rectangle in excluded)
        {
            var overlap = Rectangle.Intersect(rectangle, image);
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                continue;
            }

            tiles ??= new bool[signature.Columns * signature.Rows];
            var firstColumn = (overlap.Left - image.Left) / VisualSignature.TileSize;
            var lastColumn = (overlap.Right - 1 - image.Left) / VisualSignature.TileSize;
            var firstRow = (overlap.Top - image.Top) / VisualSignature.TileSize;
            var lastRow = (overlap.Bottom - 1 - image.Top) / VisualSignature.TileSize;
            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    tiles[row * signature.Columns + column] = true;
                }
            }
        }

        return tiles;
    }

    /// <summary>
    /// The changed tiles grouped into touching groups (8-connected), each as its bounding rectangle in screen pixels, largest
    /// group first; groups past <see cref="VisualChange.MaxRegions"/> - 1 are joined into one last rectangle.
    /// </summary>
    private static IReadOnlyList<BoundsDto> Regions(VisualSignature signature, bool[] changed)
    {
        var columns = signature.Columns;
        var rows = signature.Rows;
        var seen = new bool[changed.Length];
        var groups = new List<(int Tiles, int Left, int Top, int Right, int Bottom)>();
        var pending = new Stack<int>();
        for (var start = 0; start < changed.Length; start++)
        {
            if (!changed[start] || seen[start])
            {
                continue;
            }

            seen[start] = true;
            pending.Push(start);
            int tiles = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            while (pending.TryPop(out var tile))
            {
                var column = tile % columns;
                var row = tile / columns;
                tiles++;
                left = Math.Min(left, column);
                right = Math.Max(right, column);
                top = Math.Min(top, row);
                bottom = Math.Max(bottom, row);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var c = column + dx;
                        var r = row + dy;
                        if (c < 0 || r < 0 || c >= columns || r >= rows)
                        {
                            continue;
                        }

                        var next = r * columns + c;
                        if (changed[next] && !seen[next])
                        {
                            seen[next] = true;
                            pending.Push(next);
                        }
                    }
                }
            }

            groups.Add((tiles, left, top, right, bottom));
        }

        // Largest first; ties in reading order, so the same change always reads the same
        var ordered = groups.OrderByDescending(g => g.Tiles).ThenBy(g => g.Top).ThenBy(g => g.Left).ToList();
        if (ordered.Count > VisualChange.MaxRegions)
        {
            var rest = ordered.Skip(VisualChange.MaxRegions - 1).ToList();
            ordered = ordered.Take(VisualChange.MaxRegions - 1).ToList();
            ordered.Add((rest.Sum(g => g.Tiles), rest.Min(g => g.Left), rest.Min(g => g.Top), rest.Max(g => g.Right), rest.Max(g => g.Bottom)));
        }

        return ordered.Select(g => ToScreen(signature, g.Left, g.Top, g.Right, g.Bottom)).ToList();
    }

    /// <summary>Tiles (left..right, top..bottom) as a screen rectangle, clipped to the image.</summary>
    private static BoundsDto ToScreen(VisualSignature signature, int left, int top, int right, int bottom)
    {
        var x = left * VisualSignature.TileSize;
        var y = top * VisualSignature.TileSize;
        var width = Math.Min((right + 1) * VisualSignature.TileSize, signature.Width) - x;
        var height = Math.Min((bottom + 1) * VisualSignature.TileSize, signature.Height) - y;
        return new BoundsDto(signature.OriginX + x, signature.OriginY + y, width, height);
    }
}
