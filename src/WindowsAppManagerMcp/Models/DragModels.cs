namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A drag (roadmap M4.3): press <see cref="Button"/> at the from point, hold it, move to the to point in
/// <see cref="Steps"/> absolute moves and release it there, with <see cref="Modifiers"/> held throughout.
/// </summary>
/// <param name="FromX">Where the button goes down, in screen pixels.</param>
/// <param name="FromY">See <paramref name="FromX"/>.</param>
/// <param name="ToX">Where it comes up (the drop), in screen pixels.</param>
/// <param name="ToY">See <paramref name="ToX"/>.</param>
/// <param name="Button">The button held down.</param>
/// <param name="Modifiers">The modifier keys held from before the press until after the release.</param>
/// <param name="Steps">How many moves take the pointer from the press to the drop (1 to <see cref="MaxSteps"/>).</param>
/// <param name="StepDelayMs">The wait after each move (0 to <see cref="MaxStepDelayMs"/>), the last one too, before the release.</param>
/// <param name="HoldBeforeMoveMs">The wait between the press and the first move (0 to <see cref="MaxHoldBeforeMoveMs"/>).</param>
public sealed record DragRequest(
    int FromX,
    int FromY,
    int ToX,
    int ToY,
    MouseButton Button,
    IReadOnlyList<MouseModifier> Modifiers,
    int Steps = DragRequest.DefaultSteps,
    int StepDelayMs = DragRequest.DefaultStepDelayMs,
    int HoldBeforeMoveMs = DragRequest.DefaultHoldBeforeMoveMs)
{
    public const int DefaultSteps = 10;
    public const int MaxSteps = 100;
    public const int DefaultStepDelayMs = 20;
    public const int MaxStepDelayMs = 1000;
    public const int DefaultHoldBeforeMoveMs = 100;
    public const int MaxHoldBeforeMoveMs = 2000;

    /// <summary>
    /// The longest a drag may hold the button: Steps x StepDelayMs + HoldBeforeMoveMs. The input gate is held throughout, so
    /// every other input and window call waits for it.
    /// </summary>
    public const int MaxDurationMs = 10_000;

    /// <summary>How long the button is held at most, by the request's waits (the moves themselves take next to no time).</summary>
    public int DurationMs => Steps * StepDelayMs + HoldBeforeMoveMs;

    /// <summary>Why the request cannot be sent (a range or the total duration), or null.</summary>
    public string? Invalid() =>
        Steps is < 1 or > MaxSteps ? $"steps must be 1 to {MaxSteps} (got {Steps})."
        : StepDelayMs is < 0 or > MaxStepDelayMs ? $"stepDelayMs must be 0 to {MaxStepDelayMs} (got {StepDelayMs})."
        : HoldBeforeMoveMs is < 0 or > MaxHoldBeforeMoveMs ? $"holdBeforeMoveMs must be 0 to {MaxHoldBeforeMoveMs} (got {HoldBeforeMoveMs})."
        : DurationMs > MaxDurationMs
            ? $"steps x stepDelayMs + holdBeforeMoveMs must be at most {MaxDurationMs} ms (got {DurationMs} ms): the button is held, and " +
              "every other input call waits, for that long."
            : null;
}

/// <summary>Result of drag (roadmap M4.3).</summary>
/// <param name="Success">True when the button was released at the to point.</param>
/// <param name="FromX">The screen point the button went (or would have gone) down at.</param>
/// <param name="FromY">See <paramref name="FromX"/>.</param>
/// <param name="ToX">The screen point of the drop.</param>
/// <param name="ToY">See <paramref name="ToX"/>.</param>
/// <param name="Error">What went wrong, what was sent and how to recover; null on success.</param>
/// <param name="ErrorCode">Why the drag was refused or stopped; None on success.</param>
public record DragResult(
    bool Success,
    int FromX,
    int FromY,
    int ToX,
    int ToY,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None)
{
    /// <summary>The button: "primary", "secondary", "middle", "x1" or "x2".</summary>
    public string? Button { get; init; }

    /// <summary>The modifier keys held throughout ("ctrl", "shift", "alt", "win"); null without any.</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>
    /// True once the button went down. False: nothing was sent. True without success: the drag was stopped after the press,
    /// and the error says where the button was released (back at the from point, or, after the user's pause, where the
    /// pointer was).
    /// </summary>
    public bool Pressed { get; init; }

    /// <summary>The top-level window at the from point: the drag's source, or the window that refused it there.</summary>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <summary>The top-level window at the to point: where the drop went, or the window that refused it.</summary>
    public WindowSummary? DropWindow { get; init; }

    /// <inheritdoc cref="ClickResult.ForegroundAfter"/>
    public WindowSummary? ForegroundAfter { get; init; }

    /// <inheritdoc cref="ClickResult.ActualX"/>
    public int? ActualX { get; init; }

    /// <inheritdoc cref="ClickResult.ActualX"/>
    public int? ActualY { get; init; }

    /// <inheritdoc cref="ClickResult.BlockingWindow"/>
    public WindowSummary? BlockingWindow { get; init; }

    /// <inheritdoc cref="ClickResult.HeldModifiers"/>
    public IReadOnlyList<string>? HeldModifiers { get; init; }

    /// <inheritdoc cref="ClickResult.ReleasedModifiers"/>
    public IReadOnlyList<string>? ReleasedModifiers { get; init; }

    /// <summary>The from point as the call gave it, when not in screen coordinates (as <see cref="ClickResult.Requested"/>).</summary>
    public RequestedPoint? RequestedFrom { get; init; }

    /// <summary>The to point as the call gave it, when not in screen coordinates.</summary>
    public RequestedPoint? RequestedTo { get; init; }

    /// <inheritdoc cref="ClickResult.Observed"/>
    public DesktopState? Observed { get; init; }

    /// <summary>
    /// With WAM_TAKEOVER_DETECTION=warn: the user used the keyboard or mouse while the drag ran (roadmap M7.4), so it may
    /// have gone elsewhere. Null otherwise.
    /// </summary>
    public string? Warning { get; init; }
}
