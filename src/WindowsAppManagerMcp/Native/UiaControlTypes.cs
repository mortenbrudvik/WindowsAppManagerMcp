using System.Windows.Automation;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// The control type names the tools report and accept, for example "Button".
/// </summary>
public static class UiaControlTypes
{
    /// <summary>The name reported for an element whose control type cannot be determined.</summary>
    public const string Unknown = "Unknown";

    private static readonly Dictionary<ControlType, string> NamesByControlType = new()
    {
        { ControlType.Button, "Button" },
        { ControlType.Calendar, "Calendar" },
        { ControlType.CheckBox, "CheckBox" },
        { ControlType.ComboBox, "ComboBox" },
        { ControlType.Custom, "Custom" },
        { ControlType.DataGrid, "DataGrid" },
        { ControlType.DataItem, "DataItem" },
        { ControlType.Document, "Document" },
        { ControlType.Edit, "Edit" },
        { ControlType.Group, "Group" },
        { ControlType.Header, "Header" },
        { ControlType.HeaderItem, "HeaderItem" },
        { ControlType.Hyperlink, "Hyperlink" },
        { ControlType.Image, "Image" },
        { ControlType.List, "List" },
        { ControlType.ListItem, "ListItem" },
        { ControlType.Menu, "Menu" },
        { ControlType.MenuBar, "MenuBar" },
        { ControlType.MenuItem, "MenuItem" },
        { ControlType.Pane, "Pane" },
        { ControlType.ProgressBar, "ProgressBar" },
        { ControlType.RadioButton, "RadioButton" },
        { ControlType.ScrollBar, "ScrollBar" },
        { ControlType.Separator, "Separator" },
        { ControlType.Slider, "Slider" },
        { ControlType.Spinner, "Spinner" },
        { ControlType.SplitButton, "SplitButton" },
        { ControlType.StatusBar, "StatusBar" },
        { ControlType.Tab, "Tab" },
        { ControlType.TabItem, "TabItem" },
        { ControlType.Table, "Table" },
        { ControlType.Text, "Text" },
        { ControlType.Thumb, "Thumb" },
        { ControlType.TitleBar, "TitleBar" },
        { ControlType.ToolBar, "ToolBar" },
        { ControlType.ToolTip, "ToolTip" },
        { ControlType.Tree, "Tree" },
        { ControlType.TreeItem, "TreeItem" },
        { ControlType.Window, "Window" }
    };

    // Control types added in Windows 8 (SemanticZoom 50039, AppBar 50040). The managed UI Automation
    // client has no ControlType for them and returns null, and it cannot read the raw id either, so
    // they are recognized by their LocalizedControlType ("app bar" and "semantic zoom" in English).
    private static readonly string[] UnregisteredNames = ["AppBar", "SemanticZoom"];

    /// <summary>
    /// Every name the controlTypes filter accepts, in alphabetical order: the standard control types,
    /// AppBar, SemanticZoom and <see cref="Unknown"/>.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = NamesByControlType.Values
        .Concat(UnregisteredNames)
        .Append(Unknown)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<string, string> CanonicalNames =
        Names.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The canonical spelling of a control type name, ignoring case ("button" gives "Button"), or
    /// null when it is not one of <see cref="Names"/>.
    /// </summary>
    public static string? Canonicalize(string name) =>
        CanonicalNames.TryGetValue(name.Trim(), out var canonical) ? canonical : null;

    /// <summary>
    /// Gets a readable name for an element's control type.
    /// </summary>
    /// <param name="controlType">The element's control type. The managed UI Automation client returns
    /// null for control types it does not know, such as AppBar (50040) and SemanticZoom (50039).</param>
    /// <param name="localizedControlType">The element's LocalizedControlType, used when
    /// <paramref name="controlType"/> is null. It depends on the Windows display language.</param>
    /// <returns>The control type name, a name derived from the localized control type, or "Unknown".</returns>
    public static string GetName(ControlType? controlType, string? localizedControlType = null)
    {
        if (controlType != null)
        {
            if (NamesByControlType.TryGetValue(controlType, out var name))
            {
                return name;
            }

            // Fallback: extract name from ControlType's ProgrammaticName
            var programmaticName = controlType.ProgrammaticName;
            if (!string.IsNullOrEmpty(programmaticName))
            {
                var lastDot = programmaticName.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    return programmaticName[(lastDot + 1)..];
                }
                return programmaticName;
            }
        }

        if (string.IsNullOrWhiteSpace(localizedControlType))
        {
            return Unknown;
        }

        // "app bar" -> "AppBar"; split on any whitespace
        var words = localizedControlType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var pascalCased = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

        return UnregisteredNames.FirstOrDefault(
            n => n.Equals(pascalCased, StringComparison.OrdinalIgnoreCase)) ?? pascalCased;
    }
}
