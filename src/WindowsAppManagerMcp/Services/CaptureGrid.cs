using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The coordinate grid a capture can carry (roadmap M7.1, gridSpacing): lines at every multiple of the spacing in screen
/// pixels, labeled with their screen coordinate, drawn on the returned image after it was scaled, so lines and labels keep
/// their size. A model reads a point's screen coordinates off the nearest lines instead of converting image pixels.
/// </summary>
public static class CaptureGrid
{
    /// <summary>The fewest image pixels between two lines; a closer spacing is doubled until it fits.</summary>
    public const int MinLineGap = 16;

    /// <summary>The fewest image pixels between two labels on an edge; lines between them go unlabeled.</summary>
    public const int MinLabelGap = 56;

    /// <summary>The smallest and largest spacing a capture takes, in screen pixels.</summary>
    public const int MinSpacing = 20, MaxSpacing = 10_000;

    /// <summary>
    /// The spacing in screen pixels the grid is drawn with: <paramref name="spacing"/>, doubled while its lines would be
    /// closer than <see cref="MinLineGap"/> image pixels at <paramref name="screenPerImagePixel"/>.
    /// </summary>
    public static int EffectiveSpacing(int spacing, double screenPerImagePixel)
    {
        var effective = spacing;
        while (effective / screenPerImagePixel < MinLineGap && effective < int.MaxValue / 2)
        {
            effective *= 2;
        }

        return effective;
    }

    /// <summary>
    /// Draws the grid on <paramref name="image"/>, which shows the screen rectangle starting at
    /// (<paramref name="originX"/>, <paramref name="originY"/>) at <paramref name="scaleX"/> and <paramref name="scaleY"/>
    /// screen pixels per image pixel, every <paramref name="spacing"/> screen pixels.
    /// </summary>
    public static void Draw(Bitmap image, int originX, int originY, double scaleX, double scaleY, int spacing)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 1);

        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var line = new Pen(Color.FromArgb(150, 255, 0, 255), 1);
        using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelBack = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
        using var labelText = new SolidBrush(Color.White);

        var labelEveryX = Math.Max(1, (int)Math.Ceiling(MinLabelGap / (spacing / scaleX)));
        var labelEveryY = Math.Max(1, (int)Math.Ceiling(MinLabelGap / 2.0 / (spacing / scaleY)));

        // Vertical lines at x = k * spacing, labeled along the top edge
        var first = (int)Math.Ceiling((double)originX / spacing);
        for (var k = first; ; k++)
        {
            var screenX = (long)k * spacing;
            var imageX = (int)Math.Round((screenX - originX) / scaleX);
            if (imageX >= image.Width)
            {
                break;
            }

            graphics.DrawLine(line, imageX, 0, imageX, image.Height - 1);
            if ((k - first) % labelEveryX == 0)
            {
                Label(graphics, font, labelBack, labelText, screenX.ToString(System.Globalization.CultureInfo.InvariantCulture), imageX + 2, 1);
            }
        }

        // Horizontal lines at y = k * spacing, labeled along the left edge
        first = (int)Math.Ceiling((double)originY / spacing);
        for (var k = first; ; k++)
        {
            var screenY = (long)k * spacing;
            var imageY = (int)Math.Round((screenY - originY) / scaleY);
            if (imageY >= image.Height)
            {
                break;
            }

            graphics.DrawLine(line, 0, imageY, image.Width - 1, imageY);
            if ((k - first) % labelEveryY == 0)
            {
                Label(graphics, font, labelBack, labelText, screenY.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, imageY + 2);
            }
        }
    }

    private static void Label(Graphics graphics, Font font, Brush back, Brush text, string value, int x, int y)
    {
        var size = graphics.MeasureString(value, font);
        graphics.FillRectangle(back, x, y, size.Width, size.Height);
        graphics.DrawString(value, font, text, x, y);
    }
}
