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

    // UIA_ButtonControlTypeId (50000) to UIA_AppBarControlTypeId (50040), in id order (UIAutomationClient.h). The UIA3
    // engine reads these ids directly, including the two the managed client cannot name.
    private static readonly string[] NamesById =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu",
        "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab",
        "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem",
        "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator",
        "SemanticZoom", "AppBar"
    ];

    /// <summary>The id of the first control type, UIA_ButtonControlTypeId.</summary>
    public const int FirstControlTypeId = 50000;

    private static readonly Dictionary<string, int> IdsByName = NamesById
        .Select((name, index) => (name, id: FirstControlTypeId + index))
        .ToDictionary(p => p.name, p => p.id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The control type id of a name of <see cref="Names"/> (any letter case), or null for <see cref="Unknown"/> and
    /// names that are not control types.
    /// </summary>
    public static int? GetId(string name) => IdsByName.TryGetValue(name.Trim(), out var id) ? id : null;

    /// <summary>
    /// The name of a raw control type id, as the UIA3 engine reads it: 50000-50040 by their names (50039 is
    /// SemanticZoom and 50040 AppBar), and any id a later Windows adds from <paramref name="localizedControlType"/> as
    /// <see cref="GetName(ControlType?, string?)"/> does, or <see cref="Unknown"/>.
    /// </summary>
    public static string GetName(int controlTypeId, string? localizedControlType = null)
    {
        var index = controlTypeId - FirstControlTypeId;
        return index >= 0 && index < NamesById.Length ? NamesById[index] : GetName(null, localizedControlType);
    }

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
