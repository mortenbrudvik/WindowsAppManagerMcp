using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WindowsAppManagerMcp.Services;

/// <summary>One mark of capture_with_elements: its box in pixels of the returned image, its color and the label of its tag.</summary>
/// <param name="Box">The element's visible part, in pixels of the returned (scaled) image.</param>
/// <param name="Color">The box's color (its alpha dims a disabled element); the tag is filled with it opaque.</param>
/// <param name="Label">The element's ref, drawn in the tag; null draws the box only.</param>
public sealed record ElementMark(Rectangle Box, Color Color, string? Label);

/// <summary>
/// Draws capture_with_elements' Set-of-Marks overlays (roadmap M3.11) on the image that is returned, after it was scaled, so
/// the 2-pixel boxes and the 11-pixel tags keep their size at any maxDimension. Each tag is filled with its box's color and
/// shows the element's ref in black or white, whichever contrasts more with the fill; it sits above the box's top-left
/// corner, or inside it when that place is outside the image or another tag's, so tags of neighbouring marks do not cover
/// each other where there is room.
/// </summary>
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
        var image = new Size(bitmap.Width, bitmap.Height);
        var placed = new List<Rectangle>();
        foreach (var mark in marks)
        {
            if (string.IsNullOrEmpty(mark.Label))
            {
                continue;
            }

            var text = graphics.MeasureString(mark.Label, font, PointF.Empty, format);
            var tagSize = new Size(
                (int)Math.Ceiling(text.Width) + 2 * TagPaddingX,
                (int)Math.Ceiling(Math.Max(text.Height, font.GetHeight(graphics))) + 2 * TagPaddingY);
            var tag = PlaceTag(tagSize, mark.Box, image, placed);
            placed.Add(tag);

            var fill = Color.FromArgb(255, mark.Color);
            using var background = new SolidBrush(fill);
            using var foreground = new SolidBrush(TextColorFor(fill));
            graphics.FillRectangle(background, tag);
            graphics.DrawString(mark.Label, font, foreground, tag.X + TagPaddingX, tag.Y + TagPaddingY, format);
        }
    }

    /// <summary>
    /// Where a tag of <paramref name="tag"/> size goes for <paramref name="box"/>: the first of above the box's top-left
    /// corner, inside it at the top, below the box and inside it at the bottom that lies in the image and covers no tag in
    /// <paramref name="placed"/>; when every one does, the first that lies in the image. The tag is moved left when it would
    /// pass the image's right edge.
    /// </summary>
    public static Rectangle PlaceTag(Size tag, Rectangle box, Size image, IReadOnlyList<Rectangle> placed)
    {
        var x = Math.Max(0, Math.Min(box.X, image.Width - tag.Width));
        int[] tops = [box.Y - tag.Height, box.Y, box.Bottom, box.Bottom - tag.Height];
        var candidates = tops
            .Where(y => y >= 0 && y + tag.Height <= image.Height)
            .Select(y => new Rectangle(x, y, tag.Width, tag.Height))
            .ToList();
        if (candidates.Count == 0)
        {
            return new Rectangle(x, Math.Max(0, Math.Min(box.Y, image.Height - tag.Height)), tag.Width, tag.Height);
        }

        return candidates.FirstOrDefault(c => !placed.Any(p => p.IntersectsWith(c)), candidates[0]);
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
}
