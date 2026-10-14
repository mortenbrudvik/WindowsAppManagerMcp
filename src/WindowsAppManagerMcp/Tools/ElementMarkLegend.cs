using System.Text;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// capture_with_elements' legend (roadmap M3.11): one compact line per mark, in the order of the marks, for example
/// <c>[w4120035e17] Button 'Save' #btnSave center=(392,196)</c>.
/// </summary>
/// <remarks>
/// <para>A line is: the mark's whole ref in square brackets (<c>[-]</c> for an element without a ref, which element_action
/// cannot name), also when its tag leaves out the window part the marks share (the capture's markPrefix, roadmap M3.F38);
/// the control type; the name in single quotes, escaped and cut as in get_ui_elements' text
/// format (<see cref="UIElementTextFormat"/>) but at <see cref="MaxNameLength"/> characters, left out when there is none;
/// <c>#</c> and the automation id (quoted the same way when it holds a space, a quote, a backslash, '@', '[' or ']'), when
/// there is one; <c>center=(x,y)</c>; and <c>[disabled]</c> for a disabled element.</para>
/// <para>The centre is the middle of the element's part inside the capture, in pixels of the returned image: a mouse tool
/// given that point with the capture's captureId converts it back to the element's middle on the screen (within the
/// rounding of a scaled image) and refuses it when the window moved since. element_action with the ref acts on the
/// element itself and needs no point.</para>
/// </remarks>
public static class ElementMarkLegend
{
    /// <summary>Longer names are cut to this many characters and an ellipsis.</summary>
    public const int MaxNameLength = 60;

    /// <summary>The legend of a capture's marks, lines joined by '\n'; null when it has none.</summary>
    public static string? Build(AnnotatedScreenshotResult metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Elements is not { Count: > 0 } elements ||
            metadata.ImageToScreen is not { } transform ||
            metadata.CapturedRegion is not { } region)
        {
            return null;
        }

        return string.Join('\n', elements.Select(e => Line(e, Center(e, transform, region, metadata.Width, metadata.Height))));
    }

    /// <summary>
    /// The middle of the mark's element inside the captured region, in pixels of the returned image
    /// (<paramref name="imageWidth"/> x <paramref name="imageHeight"/>).
    /// </summary>
    public static (int X, int Y) Center(
        AnnotatedElement element, ImageToScreenTransform transform, CapturedRegion region, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(region);

        var bounds = element.ScreenBounds;
        var left = Math.Max(bounds.X, region.X);
        var top = Math.Max(bounds.Y, region.Y);
        var right = Math.Min(bounds.X + bounds.Width, region.X + region.Width);
        var bottom = Math.Min(bounds.Y + bounds.Height, region.Y + region.Height);
        if (right <= left || bottom <= top)
        {
            // Not inside the region (a mark always is): the middle of its box in the image
            var box = element.ImageBounds;
            return (box.X + box.Width / 2, box.Y + box.Height / 2);
        }

        var screenX = left + (right - left) / 2;
        var screenY = top + (bottom - top) / 2;
        return (ToImage(screenX, transform.OffsetX, transform.ScaleX, imageWidth),
                ToImage(screenY, transform.OffsetY, transform.ScaleY, imageHeight));
    }

    /// <summary>One mark's line (see the remarks).</summary>
    public static string Line(AnnotatedElement element, (int X, int Y) center)
    {
        ArgumentNullException.ThrowIfNull(element);

        var line = new StringBuilder();
        line.Append('[').Append(string.IsNullOrEmpty(element.Ref) ? "-" : element.Ref).Append("] ").Append(element.ControlType);
        if (!string.IsNullOrEmpty(element.Name))
        {
            line.Append(' ').Append(UIElementTextFormat.Quote(element.Name, MaxNameLength));
        }

        if (!string.IsNullOrEmpty(element.AutomationId))
        {
            line.Append(" #").Append(UIElementTextFormat.NeedsQuotes(element.AutomationId)
                ? UIElementTextFormat.Quote(element.AutomationId, MaxNameLength)
                : element.AutomationId);
        }

        line.Append(" center=(").Append(center.X).Append(',').Append(center.Y).Append(')');
        if (!element.IsEnabled)
        {
            line.Append(" [disabled]");
        }

        return line.ToString();
    }

    private static int ToImage(int screen, int offset, double scale, int size)
    {
        var image = scale > 0 ? (int)Math.Round((screen - offset) / scale, MidpointRounding.AwayFromZero) : 0;
        return Math.Clamp(image, 0, Math.Max(0, size - 1));
    }
}
