namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Outcome of a CDP (Chrome DevTools Protocol) operation.
/// CDP failures are returned as values with an error code and a message the agent can act on,
/// instead of being thrown or swallowed.
/// </summary>
public record CdpResult<T>(T? Value, BrowserErrorCode ErrorCode, string? Error)
{
    /// <summary>True when the operation succeeded and <see cref="Value"/> is set.</summary>
    public bool Success => ErrorCode == BrowserErrorCode.None;

    public static CdpResult<T> Ok(T value) => new(value, BrowserErrorCode.None, null);

    public static CdpResult<T> Fail(BrowserErrorCode errorCode, string error) => new(default, errorCode, error);

    /// <summary>Copies the error of this failed result into a result of another type.</summary>
    public CdpResult<TOther> FailAs<TOther>() => new(default, ErrorCode, Error);

    /// <summary>Formats the error as "Code: message" for result fields that carry a CDP error.</summary>
    public string? DescribeError() => Success ? null : $"{ErrorCode}: {Error}";
}
