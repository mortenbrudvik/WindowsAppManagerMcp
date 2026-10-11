namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The invisible resize borders of a window (roadmap M2.3): how far its GetWindowRect reaches beyond its visible frame
/// (DWMWA_EXTENDED_FRAME_BOUNDS) on each side. On Windows 10 and 11 a normal window has about 7 px on the left, right
/// and bottom at 100% scale, more at higher scales. The geometry tools place and report the visible frame, so a window
/// meets the work area's edge and its neighbours without a gap; they set GetWindowRect to the visible target grown by
/// these insets.
/// </summary>
public readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom)
{
    /// <summary>
    /// The largest inset that is taken as a border. A read taken while the window moves can pair a rect and a frame of
    /// two different positions; such a difference is not a border, and the window is placed without insets instead.
    /// </summary>
    public const int MaxInset = 64;

    /// <summary>No borders: the window rect is the visible frame.</summary>
    public static FrameInsets None { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// The insets between a window rect and its visible frame. None when there is no visible frame (DWM cannot report
    /// it), and none for a side where the frame reaches beyond the rect; when any inset is above <see cref="MaxInset"/>
    /// the read is not trusted and the result is None.
    /// </summary>
    public static FrameInsets Between(
        (int Left, int Top, int Right, int Bottom) outer,
        (int Left, int Top, int Right, int Bottom)? visible)
    {
        if (visible is not { } frame)
            return None;

        var insets = new FrameInsets(
            Math.Max(0, frame.Left - outer.Left),
            Math.Max(0, frame.Top - outer.Top),
            Math.Max(0, outer.Right - frame.Right),
            Math.Max(0, outer.Bottom - frame.Bottom));

        return insets.Left > MaxInset || insets.Top > MaxInset || insets.Right > MaxInset || insets.Bottom > MaxInset
            ? None
            : insets;
    }

    /// <summary>The window rect whose visible frame is <paramref name="visible"/>.</summary>
    public WindowRect Outer(WindowRect visible) => new(
        visible.X - Left,
        visible.Y - Top,
        visible.Width + Left + Right,
        visible.Height + Top + Bottom);
}
