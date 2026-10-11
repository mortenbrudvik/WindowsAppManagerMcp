using System.Text;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The compact shapes of get_ui_elements' result (roadmap M3.1): format=flat lists every element once, and format=text
/// writes one line per element, for example <c>Button 'Save' aid=SaveBtn @812,44 80x24 [disabled]</c>.
/// </summary>
/// <remarks>
/// <para>A text line is: the control type; the name in single quotes (a quote or backslash inside is escaped with a
/// backslash, control characters become spaces, and a name longer than <see cref="MaxNameLength"/> characters is cut
/// with an ellipsis), left out when the element has none; <c>aid=</c> and the automation id (quoted the same way when it
/// holds a space or a quote), when it has one; <c>@x,y wxh</c>, the bounds in physical screen pixels, when it has any;
/// then the flags that are set, <c>[disabled]</c> and <c>[offscreen]</c>. Each level below the window's children is
/// indented by two more spaces.</para>
/// <para>Element refs (roadmap M3.4) are to go in front of the control type (<c>w1e12 Button 'Save' ...</c>), so the line
/// starts with the type until then.</para>
/// </remarks>
public static class UIElementTextFormat
{
    /// <summary>Longer names are cut to this many characters and an ellipsis.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The elements as text: one line per element, in reading order, joined by '\n'.</summary>
    public static string ToText(IReadOnlyList<UIElementInfo> elements)
    {
        var text = new StringBuilder();
        Append(elements, depth: 0);
        return text.ToString();

        void Append(IReadOnlyList<UIElementInfo> level, int depth)
        {
            foreach (var element in level)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(' ', depth * 2).Append(Line(element));
                Append(element.Children ?? [], depth + 1);
            }
        }
    }

    /// <summary>One element's line, without indentation.</summary>
    public static string Line(UIElementInfo element)
    {
        var line = new StringBuilder(element.ControlType);
        if (!string.IsNullOrEmpty(element.Name))
        {
            line.Append(' ').Append(Quote(element.Name, MaxNameLength));
        }

        if (!string.IsNullOrEmpty(element.AutomationId))
        {
            line.Append(" aid=").Append(NeedsQuotes(element.AutomationId)
                ? Quote(element.AutomationId, MaxNameLength)
                : element.AutomationId);
        }

        if (element.Bounds is { } b)
        {
            line.Append(" @").Append(b.X).Append(',').Append(b.Y).Append(' ').Append(b.Width).Append('x').Append(b.Height);
        }

        if (!element.IsEnabled)
        {
            line.Append(" [disabled]");
        }

        if (element.IsOffscreen)
        {
            line.Append(" [offscreen]");
        }

        return line.ToString();
    }

    /// <summary>
    /// Every element once, in reading order (an element, then the elements inside it), without children, with its
    /// depth (1 for the window's children) and the index of the element it is inside (null under the window).
    /// </summary>
    public static IReadOnlyList<UIElementInfo> ToFlat(IReadOnlyList<UIElementInfo> elements)
    {
        var flat = new List<UIElementInfo>();
        Append(elements, depth: 1, parent: null);
        return flat;

        void Append(IReadOnlyList<UIElementInfo> level, int depth, int? parent)
        {
            foreach (var element in level)
            {
                var index = flat.Count;
                flat.Add(element with { Children = null, Depth = depth, Parent = parent });
                Append(element.Children ?? [], depth + 1, index);
            }
        }
    }

    private static bool NeedsQuotes(string value) => value.Any(c => char.IsWhiteSpace(c) || c is '\'' or '\\' || char.IsControl(c));

    private static string Quote(string value, int maxLength)
    {
        var cut = value.Length > maxLength ? value[..maxLength] : value;
        var quoted = new StringBuilder("'", cut.Length + 3);
        foreach (var c in cut)
        {
            quoted.Append(c switch
            {
                '\'' => "\\'",
                '\\' => "\\\\",
                _ when char.IsControl(c) => " ",
                _ => c.ToString()
            });
        }

        if (cut.Length < value.Length)
        {
            quoted.Append('…');
        }

        return quoted.Append('\'').ToString();
    }
}
