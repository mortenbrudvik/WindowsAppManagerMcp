namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Where Windows sends mouse wheel input (SystemParametersInfo SPI_GETMOUSEWHEELROUTING): the "Scroll inactive windows
/// when I hover over them" setting. The values are Windows' MOUSEWHEEL_ROUTING_* values.
/// </summary>
public enum MouseWheelRouting
{
    /// <summary>The window with the keyboard focus gets the wheel (the setting is off).</summary>
    Focus = 0,

    /// <summary>Desktop apps: the window with the keyboard focus; Store apps: the window under the pointer.</summary>
    Hybrid = 1,

    /// <summary>The window under the pointer gets the wheel (the setting is on, the default since Windows 10).</summary>
    MousePosition = 2
}

/// <summary>
/// Result of scroll (roadmap M2.15): the point the wheel was turned at, the notches sent, and, like every mouse
/// result, where the input went.
/// </summary>
/// <param name="Success">True when every wheel notch was sent.</param>
/// <param name="X">The point the pointer was moved to, in physical virtual-desktop pixels (the client centre of
/// windowHandle when no point was given).</param>
/// <param name="Y">See <paramref name="X"/>.</param>
/// <param name="DeltaY">Vertical notches asked for: positive scrolls down, negative up.</param>
/// <param name="DeltaX">Horizontal notches asked for: positive scrolls right, negative left.</param>
/// <param name="Error">What went wrong and how to recover; null on success.</param>
/// <param name="ErrorCode">Why nothing (more) was sent; None on success.</param>
public record ScrollResult(
    bool Success,
    int X,
    int Y,
    int DeltaY,
    int DeltaX,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None)
{
    /// <summary>
    /// The top-level window under the pointer: where the wheel went (Windows sends it to the window under the
    /// pointer), or would have gone when the call was refused. Null when nothing was checked.
    /// </summary>
    public WindowSummary? ReceivingWindow { get; init; }

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
}
