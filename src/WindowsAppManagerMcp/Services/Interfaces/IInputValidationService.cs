namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for validating and sanitizing user inputs for security.
/// </summary>
public interface IInputValidationService
{
    /// <summary>
    /// Validates an executable path for security issues.
    /// </summary>
    /// <param name="executable">The executable path, name, or URI to validate.</param>
    /// <returns>Validation result with sanitized path if valid.</returns>
    ExecutableValidationResult ValidateExecutable(string executable);

    /// <summary>
    /// Validates a working directory path.
    /// </summary>
    /// <param name="workingDirectory">The working directory to validate.</param>
    /// <returns>Validation result with sanitized path if valid.</returns>
    PathValidationResult ValidateWorkingDirectory(string? workingDirectory);
}

/// <summary>
/// Result of validating an executable path.
/// </summary>
public record ExecutableValidationResult(
    bool IsValid,
    string? SanitizedExecutable = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Warning = null
);

/// <summary>
/// Result of validating a file path.
/// </summary>
public record PathValidationResult(
    bool IsValid,
    string? SanitizedPath = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Warning = null
);
