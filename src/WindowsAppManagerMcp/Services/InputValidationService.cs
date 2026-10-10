using System.Text.RegularExpressions;
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
/// The real cmd.exe injection surface (arguments passed to batch files) is handled by
/// <see cref="ProcessService"/> once the launch target has been resolved.
/// </remarks>
public partial class InputValidationService : IInputValidationService
{
    // Shell metacharacters rejected in bare names and relative paths, where they only appear when
    // someone tries to smuggle a command line into the executable or working directory field.
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '$', '`', '"', '\'', '(', ')', '{', '}', '\n', '\r'];

    // Characters that can never appear in a Windows file system path (besides control characters).
    private static readonly char[] InvalidPathCharacters = ['"', '<', '>', '|', '*', '?'];

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

    // Safe protocols that are passed to their registered handler unchanged.
    // file:// is not listed here: it is converted to a local path and validated as one.
    private static readonly string[] SafeProtocols =
    [
        "http://",
        "https://",
        "mailto:",
        "tel:"
    ];

    private const string FileProtocol = "file:";

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
            return new ExecutableValidationResult(false, Error: "Executable path cannot be empty", ErrorCode: nameof(LaunchErrorCode.EmptyExecutablePath));
        }

        executable = executable.Trim();

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
        // Check for dangerous protocols
        foreach (var protocol in DangerousProtocols)
        {
            if (uri.StartsWith(protocol, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutableValidationResult(
                    false,
                    Error: $"Protocol '{protocol.TrimEnd(':')}' is blocked for security reasons",
                    ErrorCode: nameof(LaunchErrorCode.BlockedProtocol));
            }
        }

        // file:// URIs name a local or UNC path. ShellExecuteEx opens or executes that path, so it
        // gets exactly the same checks as a plain path instead of bypassing them.
        if (uri.StartsWith(FileProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateFileUri(uri);
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
                Error: $"Unknown protocol '{protocolName}' is blocked for security reasons. Allowed: http, https, file, mailto, tel",
                ErrorCode: nameof(LaunchErrorCode.UnknownProtocol));
        }

        return new ExecutableValidationResult(false, Error: "Invalid URI format", ErrorCode: nameof(LaunchErrorCode.InvalidUri));
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

    private static bool IsSuspiciousExecutable(string fileName)
    {
        return SuspiciousExecutables.Any(s =>
            fileName.Equals(s, StringComparison.OrdinalIgnoreCase));
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

    // Matches URIs with scheme:// or scheme: (but not single-letter drive like C:\)
    // Examples: https://example.com, ms-settings:display, mailto:test@example.com
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:", RegexOptions.Compiled)]
    private static partial Regex ProtocolUriRegex();
}
