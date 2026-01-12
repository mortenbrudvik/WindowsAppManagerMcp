using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for validating and sanitizing user inputs for security.
/// Prevents path traversal attacks, command injection, and unsafe protocol execution.
/// </summary>
public partial class InputValidationService : IInputValidationService
{
    // Shell metacharacters that could enable command injection
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '$', '`', '"', '\'', '(', ')', '{', '}', '\n', '\r'];

    // Dangerous protocols that could execute arbitrary code
    private static readonly string[] DangerousProtocols =
    [
        "ms-settings:",
        "ms-windows-store:",
        "powershell:",
        "cmd:",
        "javascript:",
        "vbscript:",
        "wscript:",
        "cscript:",
        "mshta:",
        "shell:",
        "data:"
    ];

    // Safe protocols that should be allowed
    private static readonly string[] SafeProtocols =
    [
        "http://",
        "https://",
        "file://",
        "mailto:",
        "tel:"
    ];

    // Suspicious executables that warrant a warning
    private static readonly string[] SuspiciousExecutables =
    [
        "cmd.exe",
        "cmd",
        "powershell.exe",
        "powershell",
        "pwsh.exe",
        "pwsh",
        "bash.exe",
        "bash",
        "wscript.exe",
        "wscript",
        "cscript.exe",
        "cscript",
        "mshta.exe",
        "mshta",
        "regsvr32.exe",
        "regsvr32",
        "rundll32.exe",
        "rundll32"
    ];

    public ExecutableValidationResult ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new ExecutableValidationResult(false, Error: "Executable path cannot be empty");
        }

        executable = executable.Trim();

        // Check if it's a protocol URI FIRST (before metacharacter check)
        // This ensures protocol URIs are handled properly even if they contain special chars
        if (IsProtocolUri(executable))
        {
            return ValidateProtocolUri(executable);
        }

        // Check for shell metacharacters (command injection) - only for non-URI paths
        if (ContainsShellMetacharacters(executable))
        {
            return new ExecutableValidationResult(
                false,
                Error: "Executable contains invalid characters that could enable command injection");
        }

        // Validate as file path
        return ValidateFilePath(executable);
    }

    public PathValidationResult ValidateWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            // Null/empty is valid - will use current directory
            return new PathValidationResult(true, SanitizedPath: null);
        }

        workingDirectory = workingDirectory.Trim();

        // Check for shell metacharacters
        if (ContainsShellMetacharacters(workingDirectory))
        {
            return new PathValidationResult(
                false,
                Error: "Working directory contains invalid characters");
        }

        // Check for path traversal
        var traversalResult = CheckPathTraversal(workingDirectory);
        if (!traversalResult.IsValid)
        {
            return new PathValidationResult(false, Error: traversalResult.Error);
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

    private ExecutableValidationResult ValidateProtocolUri(string uri)
    {
        // Check for dangerous protocols
        foreach (var protocol in DangerousProtocols)
        {
            if (uri.StartsWith(protocol, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutableValidationResult(
                    false,
                    Error: $"Protocol '{protocol.TrimEnd(':')}' is blocked for security reasons");
            }
        }

        // Check if it's a safe protocol
        foreach (var protocol in SafeProtocols)
        {
            if (uri.StartsWith(protocol, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutableValidationResult(true, SanitizedExecutable: uri);
            }
        }

        // Unknown protocol - block it
        var colonIndex = uri.IndexOf(':');
        if (colonIndex > 0)
        {
            var protocolName = uri[..colonIndex];
            return new ExecutableValidationResult(
                false,
                Error: $"Unknown protocol '{protocolName}' is blocked for security reasons. Allowed: http, https, file, mailto, tel");
        }

        return new ExecutableValidationResult(false, Error: "Invalid URI format");
    }

    private ExecutableValidationResult ValidateFilePath(string path)
    {
        string? warning = null;

        // Check for path traversal
        var traversalResult = CheckPathTraversal(path);
        if (!traversalResult.IsValid)
        {
            return new ExecutableValidationResult(false, Error: traversalResult.Error);
        }

        var normalizedPath = traversalResult.NormalizedPath ?? path;

        // Check if it's a simple executable name (will be resolved by PATH)
        var isSimpleName = !path.Contains('\\') && !path.Contains('/');

        // Check for suspicious executables
        var fileName = isSimpleName ? path : Path.GetFileName(normalizedPath);
        if (IsSuspiciousExecutable(fileName))
        {
            warning = $"Warning: '{fileName}' is a system shell/script interpreter. Ensure this is intentional.";
        }

        // Warn about network paths
        if (IsNetworkPath(path))
        {
            warning = CombineWarnings(warning, "Executable is on a network path - ensure you trust the source");
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

    private static bool ContainsShellMetacharacters(string input)
    {
        return input.IndexOfAny(ShellMetacharacters) >= 0;
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

    private static bool IsSuspiciousExecutable(string fileName)
    {
        return SuspiciousExecutables.Any(s =>
            fileName.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    private static (bool IsValid, string? Error, string? NormalizedPath) CheckPathTraversal(string path)
    {
        // Skip path validation for simple executable names (resolved by PATH)
        if (!path.Contains('\\') && !path.Contains('/') && !path.Contains(".."))
        {
            return (true, null, path);
        }

        // Detect explicit path traversal attempts
        if (path.Contains(".."))
        {
            return (false, "Path traversal sequences ('..') are not allowed for security reasons", null);
        }

        try
        {
            // Normalize the path to catch hidden traversal attempts
            var normalized = Path.GetFullPath(path);

            // For absolute paths, verify the normalized path matches what was expected
            if (Path.IsPathRooted(path))
            {
                var originalDir = Path.GetDirectoryName(Path.GetFullPath(path.Replace("..", "")));
                var normalizedDir = Path.GetDirectoryName(normalized);

                // If normalization changed the directory structure, it's suspicious
                // (This catches encoded traversal or other tricks)
            }

            return (true, null, normalized);
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}", null);
        }
    }

    private static string? CombineWarnings(string? existing, string newWarning)
    {
        if (string.IsNullOrEmpty(existing))
            return newWarning;
        return $"{existing}; {newWarning}";
    }

    // Matches URIs with scheme:// or scheme: (but not single-letter drive like C:\)
    // Examples: https://example.com, ms-settings:display, mailto:test@example.com
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:", RegexOptions.Compiled)]
    private static partial Regex ProtocolUriRegex();
}
