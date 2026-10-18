using System.Diagnostics.CodeAnalysis;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Outcome of a CDP (Chrome DevTools Protocol) operation.
/// CDP failures are returned as values with an error code and a message the agent can act on,
/// instead of being thrown or swallowed.
/// </summary>
/// <remarks>
/// Only the factories make one, so <see cref="Success"/> and <see cref="Value"/> cannot disagree: <see cref="Ok"/> needs a
/// value, <see cref="Fail"/> an error code other than <see cref="BrowserErrorCode.None"/>, and <see cref="FailAs{TOther}"/>
/// copies only a failure. The compiler then knows
/// that a successful result has its value (<see cref="Success"/>'s <see cref="MemberNotNullWhenAttribute"/>).
/// </remarks>
public record CdpResult<T>
{
    private CdpResult(T? value, BrowserErrorCode errorCode, string? error)
    {
        Value = value;
        ErrorCode = errorCode;
        Error = error;
    }

    /// <summary>The value of a successful result; never null when <see cref="Success"/> is true.</summary>
    public T? Value { get; }

    /// <summary>The error code; <see cref="BrowserErrorCode.None"/> for a success.</summary>
    public BrowserErrorCode ErrorCode { get; }

    /// <summary>What went wrong and what to do, for a failure; a caller may add to it (with).</summary>
    public string? Error { get; init; }

    /// <summary>True when the operation succeeded and <see cref="Value"/> is set.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Success => ErrorCode == BrowserErrorCode.None && Value is not null;

    /// <summary>A success with <paramref name="value"/>, which must not be null.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null: a success always has its value.</exception>
    public static CdpResult<T> Ok(T value) =>
        value is null
            ? throw new ArgumentNullException(nameof(value), "A successful CDP result needs its value.")
            : new(value, BrowserErrorCode.None, null);

    /// <summary>A failure with <paramref name="errorCode"/> and what to do (<paramref name="error"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is <see cref="BrowserErrorCode.None"/>: a failure coded
    /// as a success would read as one, with a default value, when <typeparamref name="T"/> is a value type.</exception>
    public static CdpResult<T> Fail(BrowserErrorCode errorCode, string error) =>
        errorCode == BrowserErrorCode.None
            ? throw new ArgumentException("A failed CDP result needs an error code other than None.", nameof(errorCode))
            : new(default, errorCode, error);

    /// <summary>Copies the error of this failed result into a result of another type.</summary>
    /// <exception cref="InvalidOperationException">This result is a success: its copy would read as a success without a
    /// value.</exception>
    public CdpResult<TOther> FailAs<TOther>() => Success
        ? throw new InvalidOperationException($"FailAs was called on a successful CdpResult<{typeof(T).Name}>; only a failure can be copied.")
        : new(default, ErrorCode, Error);

    /// <summary>Formats the error as "Code: message" for result fields that carry a CDP error.</summary>
    public string? DescribeError() => Success ? null : $"{ErrorCode}: {Error}";
}
