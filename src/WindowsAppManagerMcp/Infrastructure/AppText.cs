using System.Diagnostics.CodeAnalysis;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The cap on the text an application chooses that the tools return as it reads: window titles and AutomationIds (roadmap
/// M3.F14). An application can make either as long as it likes, and every character of it is put in front of the agent, so
/// the results cut it to <see cref="MaxLength"/> characters, the ellipsis that marks a cut included. Only what is returned is
/// cut: the window selectors and the element searches compare their criteria with the whole value.
/// </summary>
public static class AppText
{
    /// <summary>The longest window title or AutomationId a result returns: 200 characters, the ellipsis of a cut one included.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// <paramref name="text"/> when it has at most <paramref name="maxLength"/> characters, else its start and an ellipsis (…),
    /// <paramref name="maxLength"/> characters in all (one fewer where the cut would split a surrogate pair).
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Cut(string? text, int maxLength = MaxLength)
    {
        if (text == null || text.Length <= maxLength)
        {
            return text;
        }

        var length = maxLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length] + "…";
    }
}
