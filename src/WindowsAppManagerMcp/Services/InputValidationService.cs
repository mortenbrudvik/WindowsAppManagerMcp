using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for validating and sanitizing user inputs for security.
/// Prevents path traversal attacks, command injection, and unsafe protocol execution.
/// </summary>
/// <remarks>
/// Applications are started with ShellExecuteEx (UseShellExecute=true), which never parses the
/// executable path or the working directory with a shell. Absolute paths are therefore only checked
/// for characters that cannot appear in a Windows path, so legitimate locations such as
/// "C:\Program Files (x86)\..." or "C:\Users\O'Brien\..." are accepted. Bare names and relative
/// paths never legitimately contain shell metacharacters, so they keep the stricter check.
/// The real cmd.exe injection surface (arguments passed to a target that may be run through cmd.exe) is handled by
/// <see cref="ProcessService"/> once the launch target has been resolved, and so are interpreters with arguments and
/// scripts (<see cref="InterpreterGuard"/>, roadmap M3.F8).
/// </remarks>
public partial class InputValidationService : IInputValidationService
{
    // Shell metacharacters rejected in bare names and relative paths, where they only appear when
    // someone tries to smuggle a command line into the executable or working directory field.
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '$', '`', '"', '\'', '(', ')', '{', '}', '\n', '\r'];

    // Characters that can never appear in a Windows file system path (besides control characters).
    private static readonly char[] InvalidPathCharacters = ['"', '<', '>', '|', '*', '?'];

    private const string FileProtocol = "file:";

    // Store and packaged apps start through the Applications folder by their AppUserModelID: a package family name and
    // an app ID ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"), or a plain ID that a desktop app registered
    // ("MSEdge"). Nothing else of shell: is allowed, since it opens any shell location.
    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    private readonly ProtocolAllowlist _protocols;

    /// <param name="protocols">
    /// The URI schemes that are handed to their app; default the built-in ones plus WAM_ALLOWED_PROTOCOLS
    /// (<see cref="ProtocolAllowlist.FromEnvironment"/>).
    /// </param>
    /// <param name="logger">Logs the schemes the user added, and as a warning the entries that were ignored, once.</param>
    public InputValidationService(ProtocolAllowlist? protocols = null, ILogger<InputValidationService>? logger = null)
    {
        _protocols = protocols ?? ProtocolAllowlist.FromEnvironment();

        if (_protocols.ExtraSchemes.Count > 0)
        {
            logger?.LogInformation("launch_application also opens these URI schemes ({Variable}): {Schemes}",
                ProtocolAllowlist.Variable, string.Join(", ", _protocols.ExtraSchemes));
        }

        if (_protocols.IgnoredEntries.Count > 0)
        {
            logger?.LogWarning("{Variable} entries that are blocked, file, or not a URI scheme name are ignored: {Entries}",
                ProtocolAllowlist.Variable, string.Join(", ", _protocols.IgnoredEntries));
        }
    }

    /// <summary>The URI schemes this service allows.</summary>
    public ProtocolAllowlist Protocols => _protocols;

    public ExecutableValidationResult ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new ExecutableValidationResult(false, Error: "Executable path cannot be empty", ErrorCode: nameof(LaunchErrorCode.EmptyExecutablePath));
        }

        executable = executable.Trim();

        // A packaged app's bare AppUserModelID (roadmap M6.5): the launch activates it, and nothing is resolved on disk
        if (PackagedAppId.TryParse(executable, out _))
        {
            return new ExecutableValidationResult(true, SanitizedExecutable: executable);
        }

        // Check if it's a protocol URI FIRST (before the character checks)
        // This ensures protocol URIs are handled properly even if they contain special chars
        if (IsProtocolUri(executable))
        {
            return ValidateProtocolUri(executable);
        }

        return ValidateExecutablePath(executable);
    }

    public PathValidationResult ValidateWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            // Null/empty is valid - will use current directory
            return new PathValidationResult(true, SanitizedPath: null);
        }

        workingDirectory = workingDirectory.Trim();

        if (ContainsInvalidCharacters(workingDirectory))
        {
            return new PathValidationResult(
                false,
                Error: "Working directory contains invalid characters",
                ErrorCode: nameof(LaunchErrorCode.InvalidWorkingDirectory));
        }

        // Check for path traversal
        var traversalResult = CheckPathTraversal(workingDirectory);
        if (!traversalResult.IsValid)
        {
            return new PathValidationResult(false, Error: traversalResult.Error, ErrorCode: traversalResult.ErrorCode);
        }

        string? warning = null;

        // Warn about network paths
        if (IsNetworkPath(workingDirectory))
        {
            warning = "Working directory is a network path - ensure you trust the network location";
        }

        return new PathValidationResult(
            true,
            SanitizedPath: traversalResult.NormalizedPath,
            Warning: warning);
    }

    private ExecutableValidationResult ValidateExecutablePath(string executable)
    {
        if (ContainsInvalidCharacters(executable))
        {
            return new ExecutableValidationResult(
                false,
                Error: Path.IsPathFullyQualified(executable)
                    ? "Executable path contains invalid characters (\", <, >, |, *, ? and control characters are not allowed in Windows paths)"
                    : "Executable contains invalid characters that could enable command injection",
                ErrorCode: nameof(LaunchErrorCode.InvalidCharacters));
        }

        // Validate as file path
        return ValidateFilePath(executable);
    }

    private ExecutableValidationResult ValidateProtocolUri(string uri)
    {
        var scheme = uri[..uri.IndexOf(':')];

        // file:// URIs name a local or UNC path. ShellExecuteEx opens or executes that path, so it
        // gets exactly the same checks as a plain path instead of bypassing them.
        if (uri.StartsWith(FileProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateFileUri(uri);
        }

        // A line break or other control character has no place in a URI, and the receiving app may split on it
        if (uri.Any(char.IsControl))
        {
            return new ExecutableValidationResult(false, Error: "The URI contains control characters (such as line breaks)",
                ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        if (scheme.Equals("shell", StringComparison.OrdinalIgnoreCase))
        {
            return AppsFolderApp().IsMatch(uri)
                ? new ExecutableValidationResult(true, SanitizedExecutable: uri)
                : new ExecutableValidationResult(
                    false,
                    Error: $"Only '{AppsFolderPrefix}<AppUserModelID>' is allowed of the shell: protocol (for example " +
                           $"'{AppsFolderPrefix}Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'); other shell locations are blocked for " +
                           "security reasons. Launch a desktop app by its .exe path.",
                    ErrorCode: nameof(LaunchErrorCode.BlockedProtocol));
        }

        if (ProtocolAllowlist.IsBlocked(scheme))
        {
            return new ExecutableValidationResult(
                false,
                Error: $"Protocol '{scheme}' is blocked for security reasons, and {ProtocolAllowlist.Variable} cannot allow it",
                ErrorCode: nameof(LaunchErrorCode.BlockedProtocol));
        }

        // Many handlers are registered as app.exe "%1" (or %1 unquoted): a raw quote would end the argument and let the
        // rest of the URI become more switches, and a space or another character a URI cannot hold may split it. Web
        // addresses may keep spaces (browsers take the whole URI as one argument), but never a quote.
        if (UnsafeUriCharacter(uri, allowSpaces: IsWebScheme(scheme)) is { } unsafeCharacter)
        {
            return new ExecutableValidationResult(false,
                Error: $"The URI contains {unsafeCharacter}, which a URI cannot hold and which could split it into more arguments " +
                       "for the app that opens it. Percent-encode it (for example %22 for a quote, %20 for a space).",
                ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        // http and https must name a host (http://...); "http:x" is not a web address
        if (IsWebScheme(scheme) && !uri.AsSpan(scheme.Length).StartsWith("://", StringComparison.Ordinal))
        {
            return new ExecutableValidationResult(false, Error: $"Invalid {scheme.ToLowerInvariant()} URI: expected {scheme.ToLowerInvariant()}://host/...",
                ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        if (_protocols.Allows(scheme))
        {
            return new ExecutableValidationResult(true, SanitizedExecutable: uri);
        }

        return new ExecutableValidationResult(
            false,
            Error: $"Unknown protocol '{scheme}' is blocked for security reasons. Allowed: {string.Join(", ", _protocols.AllowedSchemes)}, " +
                   $"file (a local path), and {AppsFolderPrefix}<AppUserModelID>. The user can allow more schemes with the " +
                   $"{ProtocolAllowlist.Variable} environment variable of the server.",
            ErrorCode: nameof(LaunchErrorCode.UnknownProtocol));
    }

    private static bool IsWebScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    /// <summary>Characters RFC 3986 does not allow in a URI, besides control characters (checked before) and whitespace.</summary>
    private const string CharactersNotInUris = "\"<>`{}|\\^";

    /// <summary>
    /// A description of the first character that has no place in a URI handed to its app (a quote, whitespace, or
    /// <see cref="CharactersNotInUris"/>), or null. With <paramref name="allowSpaces"/>, only the quote is refused.
    /// </summary>
    private static string? UnsafeUriCharacter(string uri, bool allowSpaces)
    {
        foreach (var c in uri)
        {
            if (c == '"')
                return "a quote (\")";

            if (allowSpaces)
                continue;

            if (char.IsWhiteSpace(c))
                return "whitespace";

            if (CharactersNotInUris.Contains(c))
                return $"'{c}'";
        }

        return null;
    }

    private ExecutableValidationResult ValidateFileUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return new ExecutableValidationResult(false, Error: "Invalid file URI format", ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        string localPath;
        try
        {
            localPath = parsed.LocalPath;
        }
        catch (InvalidOperationException)
        {
            return new ExecutableValidationResult(false, Error: "Invalid file URI format", ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath))
        {
            return new ExecutableValidationResult(
                false,
                Error: "File URI must name an absolute local path (file:///C:/...) or a UNC path (file://server/share/...)",
                ErrorCode: nameof(LaunchErrorCode.InvalidUri));
        }

        return ValidateExecutablePath(localPath);
    }

    private ExecutableValidationResult ValidateFilePath(string path)
    {
        string? warning = null;

        // Check for path traversal
        var traversalResult = CheckPathTraversal(path);
        if (!traversalResult.IsValid)
        {
            return new ExecutableValidationResult(false, Error: traversalResult.Error, ErrorCode: traversalResult.ErrorCode);
        }

        var normalizedPath = traversalResult.NormalizedPath ?? path;

        // Check if it's a simple executable name (will be resolved by PATH)
        var isSimpleName = !path.Contains('\\') && !path.Contains('/');

        // Interpreters and scripts are checked once the target is resolved (InterpreterGuard, roadmap M3.F8)

        // Warn about network paths
        if (IsNetworkPath(path))
        {
            warning = "Executable is on a network path - ensure you trust the source";
        }

        // Warn about relative paths (non-simple names)
        if (!isSimpleName && !Path.IsPathRooted(path))
        {
            warning = CombineWarnings(warning, "Using relative path - executable location depends on working directory");
        }

        return new ExecutableValidationResult(
            true,
            SanitizedExecutable: normalizedPath,
            Warning: warning);
    }

    /// <summary>
    /// Fully qualified paths (C:\..., \\server\share\...) are only checked for characters that cannot
    /// appear in a Windows path. Bare names and relative paths are also checked for shell metacharacters.
    /// </summary>
    private static bool ContainsInvalidCharacters(string input)
    {
        if (input.Any(char.IsControl) || input.IndexOfAny(InvalidPathCharacters) >= 0)
        {
            return true;
        }

        return !Path.IsPathFullyQualified(input) && input.IndexOfAny(ShellMetacharacters) >= 0;
    }

    private static bool IsProtocolUri(string input)
    {
        // Check if it looks like a URI (has scheme:// or scheme:)
        // This catches both standard URIs (https://...) and Windows protocols (ms-settings:...)
        return ProtocolUriRegex().IsMatch(input);
    }

    private static bool IsNetworkPath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static (bool IsValid, string? Error, string? ErrorCode, string? NormalizedPath) CheckPathTraversal(string path)
    {
        // Skip path validation for simple executable names (resolved by PATH)
        if (!path.Contains('\\') && !path.Contains('/') && !path.Contains(".."))
        {
            return (true, null, null, path);
        }

        // Detect explicit path traversal attempts
        if (path.Contains(".."))
        {
            return (false, "Path traversal sequences ('..') are not allowed for security reasons", nameof(LaunchErrorCode.PathTraversal), null);
        }

        try
        {
            // Normalize the path (resolves '.' segments and mixed separators)
            var normalized = Path.GetFullPath(path);
            return (true, null, null, normalized);
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}", nameof(LaunchErrorCode.InvalidPath), null);
        }
    }

    private static string? CombineWarnings(string? existing, string newWarning)
    {
        if (string.IsNullOrEmpty(existing))
            return newWarning;
        return $"{existing}; {newWarning}";
    }

    [GeneratedRegex(@"^shell:AppsFolder\\[A-Za-z0-9][A-Za-z0-9._-]*(![A-Za-z0-9][A-Za-z0-9._-]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AppsFolderApp();

    // Matches URIs with scheme:// or scheme: (but not single-letter drive like C:\)
    // Examples: https://example.com, ms-settings:display, mailto:test@example.com
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:", RegexOptions.Compiled)]
    private static partial Regex ProtocolUriRegex();
}
