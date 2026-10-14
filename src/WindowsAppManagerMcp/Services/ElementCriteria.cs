using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What find_ui_elements looks for (roadmap M3.4), compiled into the condition the provider evaluates and the test that
/// is left to the server. The element registry keeps the identity part of it (<see cref="Identity"/>) to find an element
/// again.
/// </summary>
/// <remarks>
/// Provider side, in one condition: the whole name (ignoring case), the automation id and the class name (ignoring case),
/// the control types (by id; several are an OR), and not offscreen (<see cref="VisibleOnly"/>) and enabled
/// (<see cref="EnabledOnly"/>). Server side, on the properties each element came with: a name that contains a text or
/// matches a regular expression, and the control types the engine cannot search for (Unknown always; AppBar and
/// SemanticZoom under the managed client, which throws UiaConditionNotSupportedException for them: compile again with
/// <c>controlTypesOnServer</c>). A name that contains a text can also be narrowed in the provider (<see cref="NameNarrowing"/>,
/// roadmap M3.F12), which leaves the server's test in place.
/// </remarks>
/// <param name="ControlTypes">Canonical control type names (UiaControlTypes.Names), or null for any.</param>
/// <param name="NameRegex">The compiled name pattern for <see cref="ElementNameMatch.Regex"/>, with its match timeout.</param>
public sealed record ElementCriteria(
    string? Name = null,
    ElementNameMatch NameMatch = ElementNameMatch.Exact,
    Regex? NameRegex = null,
    string? AutomationId = null,
    IReadOnlyList<string>? ControlTypes = null,
    string? ClassName = null,
    bool VisibleOnly = false,
    bool EnabledOnly = false)
{
    /// <summary>How long a name pattern may take on one name, as titleRegex on a window title.</summary>
    public static TimeSpan RegexTimeout => WindowTitles.RegexTimeout;

    /// <summary>
    /// A name pattern compiled for matching, as titleRegex is: case-insensitive, culture-invariant, with
    /// <see cref="RegexTimeout"/>. Null with an error for a pattern that is not a valid .NET regular expression.
    /// </summary>
    public static Regex? CompileNameRegex(string pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            error = null;
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            error = $"name '{pattern}' is not a valid .NET regular expression (nameMatch regex): {ex.Message}";
            return null;
        }
    }

    /// <summary>The criteria that say which element it is, without the states (offscreen, enabled) it is in now.</summary>
    public ElementCriteria Identity => this with { VisibleOnly = false, EnabledOnly = false };

    /// <summary>True when there is something to search for besides the states.</summary>
    public bool HasIdentity =>
        !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(AutomationId) || ControlTypes is { Count: > 0 } ||
        !string.IsNullOrEmpty(ClassName);

    /// <summary>
    /// For a name that contains a text: a substring condition the provider can test to leave out the elements whose names
    /// cannot match (roadmap M3.F12), or null when there is none. It is never the test itself: <see cref="Compile"/> still
    /// gives the server's test, which <see cref="ElementSearch"/> runs on every element the provider returns, so a provider
    /// that ignores the condition or adds elements to it changes nothing.
    /// </summary>
    /// <remarks>
    /// The condition must also never leave out an element the server's test accepts, and providers do not all fold case as
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> does. So it is the longest run of the name's characters that are ASCII
    /// and not i or I, searched ignoring case: no character outside ASCII equals an ASCII one under OrdinalIgnoreCase, so a
    /// name the server accepts has the run in it, ASCII character for ASCII character; the case of an ASCII letter folds alike
    /// in every comparison, ordinal or by locale, except i and I, which a Turkish or Azerbaijani comparison folds to the
    /// dotted and dotless I. A name with no such character (one in Japanese, say) has no narrowing.
    /// </remarks>
    public UiaCondition? NameNarrowing
    {
        get
        {
            if (NameMatch != ElementNameMatch.Contains || string.IsNullOrEmpty(Name))
            {
                return null;
            }

            var (start, length) = (0, 0);
            for (var i = 0; i < Name.Length;)
            {
                if (!FoldsAlike(Name[i]))
                {
                    i++;
                    continue;
                }

                var end = i;
                while (end < Name.Length && FoldsAlike(Name[end]))
                {
                    end++;
                }

                if (end - i > length)
                {
                    (start, length) = (i, end - i);
                }

                i = end;
            }

            return length == 0 ? null : UiaCondition.PropertyContains(UiaProperty.Name, Name.Substring(start, length), ignoreCase: true);
        }
    }

    private static bool FoldsAlike(char c) => char.IsAscii(c) && c is not ('i' or 'I');

    /// <summary>A short description for messages, for example "name 'Save', controlType Button".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Name))
        {
            parts.Add(NameMatch switch
            {
                ElementNameMatch.Contains => $"name containing '{Name}'",
                ElementNameMatch.Regex => $"name matching /{Name}/",
                _ => $"name '{Name}'"
            });
        }

        if (!string.IsNullOrEmpty(AutomationId))
        {
            parts.Add($"automationId '{AutomationId}'");
        }

        if (ControlTypes is { Count: > 0 })
        {
            parts.Add($"controlType {string.Join(" or ", ControlTypes)}");
        }

        if (!string.IsNullOrEmpty(ClassName))
        {
            parts.Add($"className '{ClassName}'");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The provider's condition and the server's test.</summary>
    /// <param name="controlTypesOnServer">Test the control types on the server instead of in the provider.</param>
    /// <exception cref="RegexMatchTimeoutException">From the returned test: the name pattern took too long on a name.</exception>
    public (UiaCondition Condition, Func<UiaElement, bool>? Filter) Compile(bool controlTypesOnServer = false)
    {
        var conditions = new List<UiaCondition>();
        var filters = new List<Func<UiaElement, bool>>();

        if (!string.IsNullOrEmpty(Name))
        {
            var name = Name;
            switch (NameMatch)
            {
                case ElementNameMatch.Contains:
                    filters.Add(e => (e.Properties.Name ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase));
                    break;
                case ElementNameMatch.Regex:
                    var regex = NameRegex ?? CompileNameRegex(name, out _) ??
                        throw new ArgumentException($"'{name}' is not a valid .NET regular expression.", nameof(Name));
                    filters.Add(e => regex.IsMatch(e.Properties.Name ?? string.Empty));
                    break;
                default:
                    conditions.Add(UiaCondition.PropertyEquals(UiaProperty.Name, name, ignoreCase: true));
                    break;
            }
        }

        if (!string.IsNullOrEmpty(AutomationId))
        {
            conditions.Add(UiaCondition.PropertyEquals(UiaProperty.AutomationId, AutomationId, ignoreCase: true));
        }

        if (!string.IsNullOrEmpty(ClassName))
        {
            conditions.Add(UiaCondition.PropertyEquals(UiaProperty.ClassName, ClassName, ignoreCase: true));
        }

        if (ControlTypes is { Count: > 0 } types)
        {
            // Unknown has no id to search for, so a list with it is tested on the server as a whole
            if (controlTypesOnServer || types.Any(t => UiaControlTypes.GetId(t) == null))
            {
                var names = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
                filters.Add(e => names.Contains(e.Properties.ControlType));
            }
            else
            {
                var byType = types.Select(t => UiaCondition.PropertyEquals(UiaProperty.ControlType, t)).ToArray();
                conditions.Add(byType.Length == 1 ? byType[0] : UiaCondition.Or(byType));
            }
        }

        if (VisibleOnly)
        {
            conditions.Add(UiaCondition.PropertyEquals(UiaProperty.IsOffscreen, false));
        }

        if (EnabledOnly)
        {
            conditions.Add(UiaCondition.PropertyEquals(UiaProperty.IsEnabled, true));
        }

        var condition = conditions.Count switch
        {
            0 => UiaCondition.True,
            1 => conditions[0],
            _ => UiaCondition.And(conditions.ToArray())
        };
        Func<UiaElement, bool>? filter = filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => e => filters.All(f => f(e))
        };
        return (condition, filter);
    }
}
