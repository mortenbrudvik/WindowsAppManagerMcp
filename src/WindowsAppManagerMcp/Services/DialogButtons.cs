using System.Globalization;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The standard dialog buttons and how respond_to_dialog picks the one asked for (roadmap M3.5). The standard ids are the
/// same in every language: a Win32 dialog box (#32770, MessageBox) exposes a button's control id as its AutomationId
/// ("6" is IDYES), and a task dialog (TaskDialog, the replace prompt of a Save As dialog) as "CommandButton_6". So "yes"
/// presses the right button on a German or Japanese Windows too, where its text is "Ja" or "はい".
/// </summary>
public static class DialogButtons
{
    /// <summary>The prefix of a task dialog's common button AutomationIds ("CommandButton_6" is its Yes).</summary>
    public const string TaskDialogPrefix = "CommandButton_";

    /// <summary>The standard button names and their ids (winuser.h IDOK to IDCONTINUE).</summary>
    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = 1,
        ["cancel"] = 2,
        ["abort"] = 3,
        ["retry"] = 4,
        ["ignore"] = 5,
        ["yes"] = 6,
        ["no"] = 7,
        ["close"] = 8,
        ["help"] = 9,
        ["tryagain"] = 10,
        ["continue"] = 11
    };

    /// <summary>The standard names, for messages and the tool description.</summary>
    public static IReadOnlyList<string> StandardNames { get; } = Ids.OrderBy(p => p.Value).Select(p => p.Key).ToList();

    /// <summary>The id of a standard name ("yes", "Try again", "try_again"), or null.</summary>
    public static int? StandardId(string name)
    {
        var key = new string(name.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-')).ToArray());
        return Ids.TryGetValue(key, out var id) ? id : null;
    }

    /// <summary>The standard button an AutomationId names ("6" and "CommandButton_6" are yes), or null.</summary>
    public static string? StandardOf(string? automationId)
    {
        if (IdOf(automationId) is not { } id)
        {
            return null;
        }

        foreach (var (name, value) in Ids)
        {
            if (value == id)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The control id an AutomationId carries ("6", "CommandButton_6"), or null.</summary>
    private static int? IdOf(string? automationId)
    {
        if (string.IsNullOrEmpty(automationId))
        {
            return null;
        }

        var digits = automationId.StartsWith(TaskDialogPrefix, StringComparison.Ordinal) ? automationId[TaskDialogPrefix.Length..] : automationId;
        return digits.Length is > 0 and <= 5 && digits.All(char.IsAsciiDigit) &&
               int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>
    /// The button <paramref name="asked"/> names: (1) a standard name ("yes") or a control id ("6") by the AutomationId that
    /// carries the id; (2) an AutomationId, exactly; (3) the visible text, ignoring case, the access-key marker (&amp;) and a
    /// trailing ellipsis. The first rule with a match decides; two matches under it are ambiguous. As a last resort "ok"
    /// names the only button of a dialog that has one (a message box's lone button may carry the id of Cancel, so that
    /// Esc closes it).
    /// </summary>
    public static DialogButtonMatch Match(IReadOnlyList<DialogButton> buttons, string asked)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        var text = (asked ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return DialogButtonMatch.None;
        }

        var id = StandardId(text) ?? (text.All(char.IsAsciiDigit) && text.Length <= 5
            ? int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture)
            : null);
        if (id is { } wanted)
        {
            var byId = buttons.Where(b => IdOf(b.AutomationId) == wanted).ToList();
            if (byId.Count > 0)
            {
                return DialogButtonMatch.Of(byId, "id");
            }
        }

        var byAutomationId = buttons.Where(b => string.Equals(b.AutomationId, text, StringComparison.Ordinal)).ToList();
        if (byAutomationId.Count > 0)
        {
            return DialogButtonMatch.Of(byAutomationId, "automationId");
        }

        var caption = Caption(text);
        var byName = buttons.Where(b => b.Name != null && string.Equals(Caption(b.Name), caption, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0)
        {
            return DialogButtonMatch.Of(byName, "name");
        }

        if (id == 1 && buttons.Count == 1)
        {
            return DialogButtonMatch.Of(buttons, "onlyButton");
        }

        return DialogButtonMatch.None;
    }

    /// <summary>A caption without the access-key marker, surrounding blanks and a trailing ellipsis.</summary>
    private static string Caption(string name)
    {
        var text = name.Replace("&", string.Empty, StringComparison.Ordinal).Trim();
        if (text.EndsWith("...", StringComparison.Ordinal))
        {
            text = text[..^3];
        }
        else if (text.EndsWith('…'))
        {
            text = text[..^1];
        }

        return text.Trim();
    }
}

/// <summary>The buttons a name matched (<see cref="DialogButtons.Match"/>), and by which rule.</summary>
/// <param name="Buttons">The matches: none, one, or several (ambiguous).</param>
/// <param name="By">The rule that matched: id, automationId, name or onlyButton; null for no match.</param>
public sealed record DialogButtonMatch(IReadOnlyList<DialogButton> Buttons, string? By)
{
    public static DialogButtonMatch None { get; } = new([], null);

    public static DialogButtonMatch Of(IReadOnlyList<DialogButton> buttons, string by) => new(buttons, by);

    /// <summary>The one button matched, or null for none or several.</summary>
    public DialogButton? Single => Buttons.Count == 1 ? Buttons[0] : null;
}
