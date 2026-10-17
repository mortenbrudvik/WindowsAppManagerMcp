using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The pages a workspace keeps (roadmap M7.5): http and https only, without a user name or password in the address, and
/// without query values or fragments whose names look like secrets (a session's token, an OAuth code, an API key), which a
/// preset file must not hold and which would not work again anyway. The page itself stays, so it opens where it was.
/// </summary>
public static partial class WorkspaceUrls
{
    /// <summary>The most pages a browser window keeps in a workspace; more are left out, with a warning.</summary>
    public const int MaxUrlsPerWindow = 50;

    /// <summary>The longest address kept; a longer one is left out.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// <paramref name="url"/> as a workspace keeps it, or null when it is not one to keep (not http or https, too long, or not
    /// an address at all).
    /// </summary>
    public static string? Clean(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        builder.Query = CleanPairs(uri.Query.TrimStart('?'));

        // A fragment that carries name=value pairs is cleaned the same way (an OAuth implicit grant's #access_token=...), and
        // so is the query of a hash route (#/callback?token=...); a plain fragment (#section-2) is kept. A secret in the
        // path itself (/reset/<token>) cannot be told from a page name and stays
        builder.Fragment = CleanFragment(uri.Fragment.TrimStart('#'));
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Whether a query or fragment name looks like a secret's: it contains a word that only secrets use (token, secret,
    /// password, session, credential, ...), or it is, or ends with, a short one (code, key, auth, sig, state, ...).
    /// </summary>
    public static bool IsSecretName(string name)
    {
        var unescaped = Uri.UnescapeDataString(name);
        return SecretWord().IsMatch(unescaped) || SecretName().IsMatch(unescaped);
    }

    private static string CleanFragment(string fragment)
    {
        var query = fragment.IndexOf('?');
        if (query >= 0)
        {
            var pairs = CleanPairs(fragment[(query + 1)..]);
            return pairs.Length > 0 ? fragment[..(query + 1)] + pairs : fragment[..query];
        }

        return fragment.Contains('=') ? CleanPairs(fragment) : fragment;
    }

    private static string CleanPairs(string pairs)
    {
        if (pairs.Length == 0)
        {
            return string.Empty;
        }

        var kept = new StringBuilder();
        foreach (var pair in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (IsSecretName(name))
            {
                continue;
            }

            if (kept.Length > 0)
            {
                kept.Append('&');
            }

            kept.Append(pair);
        }

        return kept.ToString();
    }

    [GeneratedRegex(
        @"token|secret|passw|pwd|credential|bearer|verifier|session|sessid|apikey|api[_\-.]?key|signature|jwt|saml|nonce|csrf|xsrf|oauth",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretWord();

    [GeneratedRegex(@"(^|[_\-.])(code|key|auth|authorization|sig|sid|state|ticket|pass|otp|hmac)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}
