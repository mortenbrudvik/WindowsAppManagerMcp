using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace WindowsAppManagerMcp.Services;

/// <summary>One mark of capture_with_elements: its box in pixels of the returned image, its color and the label of its tag.</summary>
/// <param name="Box">The element's visible part, in pixels of the returned (scaled) image.</param>
/// <param name="Color">The box's color (its alpha dims a disabled element); the tag is filled with it opaque.</param>
/// <param name="Label">The text of its tag: the element's ref, without the window part the capture's marks share
/// (<see cref="ElementMarkRenderer.TagText"/>); null draws the box only.</param>
public sealed record ElementMark(Rectangle Box, Color Color, string? Label);

/// <summary>
/// Draws capture_with_elements' Set-of-Marks overlays (roadmap M3.11) on the image that is returned, after it was scaled, so
/// the 2-pixel boxes and the 11-pixel tags keep their size at any maxDimension. Each tag is filled with its box's color and
/// shows its label in black or white, whichever contrasts more with the fill.
/// </summary>
/// <remarks>
/// Tags stay readable on a crowded window (roadmap M3.F38). When every mark's ref has the same window part, the tags leave
/// it out (e44 for w1399829e44) and the capture reports it as markPrefix, so a tag is still the exact ref with the prefix
/// before it. A tag goes outside its box where there is room, so it does not cover the element's own text, and never over
/// an earlier tag where any of its places is free (see <see cref="PlaceTags"/>).
/// </remarks>
public static class ElementMarkRenderer
{
    /// <summary>The width of a box's line in the returned image, in pixels.</summary>
    public const float LineWidth = 2f;

    /// <summary>The tag's font size in pixels of the returned image (Segoe UI bold).</summary>
    public const float LabelFontSize = 11f;

    /// <summary>Pixels between a tag's edge and its text, left and right.</summary>
    private const int TagPaddingX = 3;

    /// <summary>Pixels between a tag's edge and its text, above and below.</summary>
    private const int TagPaddingY = 1;

    /// <summary>
    /// A box at least this many tags wide gets its tag inside at its right end: an element's icon and text start at its
    /// left.
    /// </summary>
    private const int WideBoxTags = 3;

    /// <summary>
    /// Draws every mark's box, then every tag (when <paramref name="labels"/>), so no box crosses a tag. A mark without a
    /// label gets no tag.
    /// </summary>
    public static void Draw(Bitmap bitmap, IReadOnlyList<ElementMark> marks, bool labels)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(marks);
        if (marks.Count == 0)
        {
            return;
        }

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        foreach (var mark in marks)
        {
            using var pen = new Pen(mark.Color, LineWidth);
            graphics.DrawRectangle(pen, mark.Box);
        }

        if (!labels)
        {
            return;
        }

        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", LabelFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = StringFormat.GenericTypographic;
        var lineHeight = font.GetHeight(graphics);
        var sizes = marks
            .Select(mark => string.IsNullOrEmpty(mark.Label) ? (Size?)null : TagSize(graphics.MeasureString(mark.Label, font, PointF.Empty, format)))
            .ToList();
        var tags = PlaceTags(marks.Select(mark => mark.Box).ToList(), sizes, new Size(bitmap.Width, bitmap.Height));
        for (var i = 0; i < marks.Count; i++)
        {
            if (tags[i] is not { } tag)
            {
                continue;
            }

            var fill = Color.FromArgb(255, marks[i].Color);
            using var background = new SolidBrush(fill);
            using var foreground = new SolidBrush(TextColorFor(fill));
            graphics.FillRectangle(background, tag);
            graphics.DrawString(marks[i].Label, font, foreground, tag.X + TagPaddingX, tag.Y + TagPaddingY, format);
        }

        Size TagSize(SizeF text) => new(
            (int)Math.Ceiling(text.Width) + 2 * TagPaddingX,
            (int)Math.Ceiling(Math.Max(text.Height, lineHeight)) + 2 * TagPaddingY);
    }

    /// <summary>
    /// The window part every ref in <paramref name="refs"/> has, such as "w1399829" (refs are "w&lt;window&gt;e&lt;element&gt;",
    /// see <see cref="ElementRefs"/>), which the tags then leave out (roadmap M3.F38); null when there is no ref, or when the
    /// refs are of more than one window or one is not well formed, so the tags show whole refs. Nulls (elements without a
    /// ref) are skipped.
    /// </summary>
    public static string? SharedWindowPart(IEnumerable<string?> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);
        int? shared = null;
        foreach (var elementRef in refs)
        {
            if (elementRef == null)
            {
                continue;
            }

            if (!ElementRefs.TryParse(elementRef, out var window, out _) || (shared != null && shared != window))
            {
                return null;
            }

            shared = window;
        }

        return shared == null ? null : string.Create(CultureInfo.InvariantCulture, $"w{shared}");
    }

    /// <summary>
    /// The tag of <paramref name="elementRef"/>: its element part ("e44") when the ref starts with
    /// <paramref name="windowPart"/>, else the whole ref.
    /// </summary>
    public static string TagText(string elementRef, string? windowPart)
    {
        ArgumentNullException.ThrowIfNull(elementRef);
        return windowPart != null && elementRef.Length > windowPart.Length &&
               elementRef.StartsWith(windowPart, StringComparison.OrdinalIgnoreCase) &&
               elementRef[windowPart.Length] is 'e' or 'E'
            ? elementRef[windowPart.Length..]
            : elementRef;
    }

    /// <summary>
    /// Where each mark's tag goes, in the order of the marks (the first marks get the first choice): null for a mark whose
    /// <paramref name="tags"/> size is null. A tag goes to the first of these places that lies in the image:
    /// <list type="number">
    /// <item>outside its box, where it covers no earlier tag and no other mark's box (one that holds its box, such as a list
    /// around its item, does not count): above the box's top-left corner, left of the box at its top, or below the box;</item>
    /// <item>inside its box, where it covers no earlier tag: at the top right of a box at least three tags wide, at the top
    /// left, then at the bottom right and bottom left;</item>
    /// <item>where it covers the least of the earlier tags, inside places first.</item>
    /// </list>
    /// A tag above or below a box is moved left when it would pass the image's right edge. The boxes and tags are looked up
    /// in a grid of the image, so thousands of marks are placed in milliseconds.
    /// </summary>
    public static IReadOnlyList<Rectangle?> PlaceTags(IReadOnlyList<Rectangle> boxes, IReadOnlyList<Size?> tags, Size image)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(tags);
        if (boxes.Count != tags.Count)
        {
            throw new ArgumentException("There must be one tag size (or null) per box.", nameof(tags));
        }

        var boxGrid = new RectangleGrid();
        foreach (var box in boxes)
        {
            boxGrid.Add(box);
        }

        var placed = new RectangleGrid();
        var result = new Rectangle?[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            if (tags[i] is { } size)
            {
                var tag = PlaceTag(size, boxes[i], image, boxGrid, placed);
                placed.Add(tag);
                result[i] = tag;
            }
        }

        return result;
    }

    /// <summary>Where one tag goes (see <see cref="PlaceTags"/>).</summary>
    private static Rectangle PlaceTag(Size tag, Rectangle box, Size image, RectangleGrid boxes, RectangleGrid placed)
    {
        var bounds = new Rectangle(Point.Empty, image);
        var left = Math.Max(0, Math.Min(box.X, image.Width - tag.Width));
        Rectangle[] outside =
        [
            new(left, box.Y - tag.Height, tag.Width, tag.Height),
            new(box.X - tag.Width, box.Y, tag.Width, tag.Height),
            new(left, box.Bottom, tag.Width, tag.Height)
        ];
        foreach (var place in outside)
        {
            if (bounds.Contains(place) && !placed.Any(place) && !boxes.Any(place, other => !other.Contains(box)))
            {
                return place;
            }
        }

        var inside = Inside(tag, box, image, left);
        foreach (var place in inside)
        {
            if (bounds.Contains(place) && !placed.Any(place))
            {
                return place;
            }
        }

        var anywhere = inside.Concat(outside).Where(bounds.Contains).ToList();
        return anywhere.Count > 0
            ? anywhere.MinBy(placed.OverlapArea)
            : new Rectangle(left, Math.Max(0, Math.Min(box.Y, image.Height - tag.Height)), tag.Width, tag.Height);
    }

    /// <summary>
    /// The places inside <paramref name="box"/>, in order: the top right first for a box at least <see cref="WideBoxTags"/>
    /// tags wide, then the top left, then the same at the bottom (only for a box at least a tag wide and high).
    /// </summary>
    private static List<Rectangle> Inside(Size tag, Rectangle box, Size image, int left)
    {
        var top = Math.Max(0, Math.Min(box.Y, image.Height - tag.Height));
        var right = box.Right - tag.Width;
        var bottom = box.Bottom - tag.Height;
        var fitsAcross = box.Width >= tag.Width;
        var fitsDown = box.Height >= tag.Height;

        var places = new List<Rectangle>();
        if (box.Width >= WideBoxTags * tag.Width)
        {
            places.Add(new Rectangle(right, top, tag.Width, tag.Height));
        }

        places.Add(new Rectangle(left, top, tag.Width, tag.Height));
        if (fitsAcross && box.Width < WideBoxTags * tag.Width)
        {
            places.Add(new Rectangle(right, top, tag.Width, tag.Height));
        }

        if (fitsDown && bottom > top)
        {
            if (fitsAcross)
            {
                places.Add(new Rectangle(right, bottom, tag.Width, tag.Height));
            }

            places.Add(new Rectangle(left, bottom, tag.Width, tag.Height));
        }

        return places;
    }

    /// <summary>
    /// Black or white, whichever contrasts more with <paramref name="fill"/> (WCAG relative luminance: black on a fill
    /// lighter than about 0.18).
    /// </summary>
    public static Color TextColorFor(Color fill)
    {
        var luminance = 0.2126 * Linear(fill.R) + 0.7152 * Linear(fill.G) + 0.0722 * Linear(fill.B);
        return luminance > 0.179 ? Color.Black : Color.White;

        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>
    /// Rectangles filed under the cells of a 64-pixel grid that they cover, so a tag's place is checked only against the
    /// boxes and tags near it.
    /// </summary>
    private sealed class RectangleGrid
    {
        private const int CellSize = 64;

        private readonly List<Rectangle> _rectangles = [];
        private readonly Dictionary<(int X, int Y), List<int>> _cells = [];

        public void Add(Rectangle rectangle)
        {
            var index = _rectangles.Count;
            _rectangles.Add(rectangle);
            foreach (var cell in Cells(rectangle))
            {
                if (!_cells.TryGetValue(cell, out var indexes))
                {
                    _cells[cell] = indexes = [];
                }

                indexes.Add(index);
            }
        }

        /// <summary>Whether a rectangle that <paramref name="counts"/> (any, without it) intersects <paramref name="area"/>.</summary>
        public bool Any(Rectangle area, Func<Rectangle, bool>? counts = null) =>
            Near(area).Any(r => r.IntersectsWith(area) && (counts == null || counts(r)));

        /// <summary>How many pixels of <paramref name="area"/> the rectangles cover, counting a pixel once per rectangle.</summary>
        public long OverlapArea(Rectangle area) => Near(area).Sum(r =>
        {
            var overlap = Rectangle.Intersect(r, area);
            return (long)overlap.Width * overlap.Height;
        });

        /// <summary>The rectangles filed under the cells <paramref name="area"/> covers, each once.</summary>
        private IEnumerable<Rectangle> Near(Rectangle area)
        {
            var seen = new HashSet<int>();
            foreach (var cell in Cells(area))
            {
                if (_cells.TryGetValue(cell, out var indexes))
                {
                    foreach (var index in indexes)
                    {
                        if (seen.Add(index))
                        {
                            yield return _rectangles[index];
                        }
                    }
                }
            }
        }

        private static IEnumerable<(int X, int Y)> Cells(Rectangle rectangle)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                yield break;
            }

            for (var y = Cell(rectangle.Top); y <= Cell(rectangle.Bottom - 1); y++)
            {
                for (var x = Cell(rectangle.Left); x <= Cell(rectangle.Right - 1); x++)
                {
                    yield return (x, y);
                }
            }
        }

        private static int Cell(int coordinate) => (int)Math.Floor(coordinate / (double)CellSize);
    }
}
