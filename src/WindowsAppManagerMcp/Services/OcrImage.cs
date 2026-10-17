using System.Drawing;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The pixel work before Windows OCR (roadmap M6.3), on top-down BGRA buffers of 4 bytes per pixel: enlarging small text,
/// and cutting an image larger than OCR reads at once into overlapping tiles. Managed code only, so it is tested without
/// GDI or WinRT.
/// </summary>
public static class OcrImage
{
    /// <summary>
    /// How far tiles overlap, in pixels of the image OCR reads: more than the height of a large line of text, so every
    /// word lies whole in the tile whose core holds its centre.
    /// </summary>
    public const int TileOverlap = 128;

    /// <summary>
    /// The image twice as wide and twice as high, each new pixel interpolated from its four nearest source pixels
    /// (bilinear, pixel centres aligned). Windows OCR reads 9-point text at 96 DPI much better this way.
    /// </summary>
    public static byte[] Upscale2x(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"A {width}x{height} BGRA image needs {(long)width * height * 4} bytes.", nameof(bgra));
        }

        var outWidth = width * 2;
        var result = new byte[(long)outWidth * height * 2 * 4];
        for (var outY = 0; outY < height * 2; outY++)
        {
            // The centre of output pixel outY lies at source coordinate (outY + 0.5) / 2 - 0.5: a quarter of the way from one
            // source row to the next, before or after it
            var (y0, y1, wy) = Neighbours(outY, height);
            for (var outX = 0; outX < outWidth; outX++)
            {
                var (x0, x1, wx) = Neighbours(outX, width);
                var target = ((long)outY * outWidth + outX) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var top = bgra[(y0 * width + x0) * 4 + channel] * (1 - wx) + bgra[(y0 * width + x1) * 4 + channel] * wx;
                    var bottom = bgra[(y1 * width + x0) * 4 + channel] * (1 - wx) + bgra[(y1 * width + x1) * 4 + channel] * wx;
                    result[target + channel] = (byte)Math.Round(top * (1 - wy) + bottom * wy);
                }
            }
        }

        return result;
    }

    /// <summary>The two source pixels an output pixel of a 2x enlargement lies between, and the weight of the second.</summary>
    private static (int First, int Second, double Weight) Neighbours(int output, int sourceLength)
    {
        var position = (output + 0.5) / 2 - 0.5;
        var first = (int)Math.Floor(position);
        var weight = position - first;
        return (Math.Clamp(first, 0, sourceLength - 1), Math.Clamp(first + 1, 0, sourceLength - 1), weight);
    }

    /// <summary>
    /// The tiles OCR reads an image in: the whole image when both edges are at most <paramref name="maxDimension"/>, otherwise
    /// a grid of tiles at most that large that overlap by <see cref="TileOverlap"/>. The cores (each tile less half the
    /// overlap on every inner edge) cover the image exactly once, so a word kept only by the tile whose core holds its centre
    /// is kept once.
    /// </summary>
    public static IReadOnlyList<OcrTile> Tiles(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"An image of {width}x{height} pixels has nothing to read.");
        }

        if (maxDimension <= 2 * TileOverlap)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension), maxDimension, $"Tiles must be larger than {2 * TileOverlap} pixels.");
        }

        var columns = Spans(width, maxDimension);
        var rows = Spans(height, maxDimension);
        return rows.SelectMany(row => columns.Select(column => new OcrTile(
                new Rectangle(column.Start, row.Start, column.Length, row.Length),
                Rectangle.FromLTRB(column.CoreStart, row.CoreStart, column.CoreEnd, row.CoreEnd))))
            .ToArray();
    }

    private sealed record Span(int Start, int Length, int CoreStart, int CoreEnd);

    /// <summary>Overlapping spans of at most <paramref name="max"/> along one edge, and their cores.</summary>
    private static List<Span> Spans(int length, int max)
    {
        if (length <= max)
        {
            return [new Span(0, length, 0, length)];
        }

        // Each tile after the first starts TileOverlap before the end of the one before it
        var count = (int)Math.Ceiling((double)(length - TileOverlap) / (max - TileOverlap));
        var step = (int)Math.Ceiling((double)(length - TileOverlap) / count);
        var spans = new List<Span>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * step;
            var end = i == count - 1 ? length : Math.Min(length, start + step + TileOverlap);
            var coreStart = i == 0 ? 0 : start + TileOverlap / 2;
            var coreEnd = i == count - 1 ? length : end - TileOverlap / 2;
            spans.Add(new Span(start, end - start, coreStart, coreEnd));
        }

        return spans;
    }

    /// <summary>The pixels of <paramref name="area"/> of a <paramref name="width"/> pixels wide image, as an image of its own.</summary>
    public static byte[] Crop(byte[] bgra, int width, Rectangle area)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        var result = new byte[(long)area.Width * area.Height * 4];
        for (var row = 0; row < area.Height; row++)
        {
            Array.Copy(bgra, ((long)(area.Y + row) * width + area.X) * 4, result, (long)row * area.Width * 4, area.Width * 4);
        }

        return result;
    }
}

/// <summary>
/// A tile of an image OCR reads in parts, in pixels of the image: the part it reads, and its core, the part whose words it
/// keeps.
/// </summary>
public sealed record OcrTile(Rectangle Area, Rectangle Core);
