using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A snap_window position in the monitor's work area. As a tool argument (roadmap M2.2) the input schema lists the
/// names; unknown positions fail with InvalidSnapPosition. 'full' is still accepted for fullscreen.
/// </summary>
[ToolEnum(nameof(WindowErrorCode.InvalidSnapPosition), "snap position")]
public enum SnapPosition
{
    [JsonStringEnumMemberName("left_half")]
    LeftHalf,

    [JsonStringEnumMemberName("right_half")]
    RightHalf,

    [JsonStringEnumMemberName("top_half")]
    TopHalf,

    [JsonStringEnumMemberName("bottom_half")]
    BottomHalf,

    [JsonStringEnumMemberName("top_left_quarter")]
    TopLeftQuarter,

    [JsonStringEnumMemberName("top_right_quarter")]
    TopRightQuarter,

    [JsonStringEnumMemberName("bottom_left_quarter")]
    BottomLeftQuarter,

    [JsonStringEnumMemberName("bottom_right_quarter")]
    BottomRightQuarter,

    [JsonStringEnumMemberName("left_third")]
    LeftThird,

    [JsonStringEnumMemberName("center_third")]
    CenterThird,

    [JsonStringEnumMemberName("right_third")]
    RightThird,

    [JsonStringEnumMemberName("left_two_thirds")]
    LeftTwoThirds,

    [JsonStringEnumMemberName("right_two_thirds")]
    RightTwoThirds,

    [JsonStringEnumMemberName("fullscreen")]
    [ToolValueAlias("full", Listed = false)]
    Fullscreen
}
