using System.Globalization;
using System.Text;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The compact shapes of get_ui_elements' result (roadmap M3.1): format=flat lists every element once, and format=text
/// writes one line per element, for example <c>w4120035e17 Button 'Save' aid=SaveBtn @812,44 80x24 [disabled]</c>.
/// </summary>
/// <remarks>
/// <para>A text line is: the element's ref (roadmap M3.4) when it has one; the control type; the name in single quotes (a
/// quote or backslash inside is escaped with a backslash, control characters and line or paragraph separators become
/// spaces, and a name longer than <see cref="MaxNameLength"/> characters is cut with an ellipsis), left out when the element has none; <c>aid=</c> and the
/// automation id (quoted the same way when it holds a space, a quote, a backslash, '@', '[' or ']', so it cannot be read as
/// another token), when it has one; <c>fw=</c> and the UI framework where it changes (<see cref="UIElementInfo.FrameworkId"/>,
/// roadmap M3.3), quoted the same way; <c>@x,y wxh</c>, the bounds in physical screen pixels, when it has any; then the
/// flags that are set, <c>[disabled]</c> and <c>[offscreen]</c>. Each level below the window's children is indented by two
/// more spaces.</para>
/// <para>A ref has no space and is a 'w', digits, an 'e' and digits, which no control type name is: a line without one
/// starts with the type.</para>
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
        var line = new StringBuilder();
        if (!string.IsNullOrEmpty(element.Ref))
        {
            line.Append(element.Ref).Append(' ');
        }

        line.Append(element.ControlType);
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

        if (!string.IsNullOrEmpty(element.FrameworkId))
        {
            line.Append(" fw=").Append(NeedsQuotes(element.FrameworkId)
                ? Quote(element.FrameworkId, MaxNameLength)
                : element.FrameworkId);
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

    // '@' and '[' start the bounds and flag tokens of a line, so an id holding them is quoted too
    private static bool NeedsQuotes(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '\'' or '\\' or '@' or '[' or ']' || BreaksTheLine(c));

    /// <summary>A control character, or a line or paragraph separator, which many viewers show as a new line.</summary>
    private static bool BreaksTheLine(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// The value in single quotes, as a name is written: a quote or backslash escaped, a character that could break the line
    /// a space, and cut at <paramref name="maxLength"/> characters with an ellipsis. get_desktop_state's header lines use it too.
    /// </summary>
    internal static string Quote(string value, int maxLength = MaxNameLength)
    {
        var cut = value.Length > maxLength ? value[..maxLength] : value;
        var quoted = new StringBuilder("'", cut.Length + 3);
        foreach (var c in cut)
        {
            quoted.Append(c switch
            {
                '\'' => "\\'",
                '\\' => "\\\\",
                _ when BreaksTheLine(c) => " ",
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
