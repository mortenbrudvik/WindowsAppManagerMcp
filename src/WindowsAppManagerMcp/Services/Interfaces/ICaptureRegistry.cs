using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The captures a mouse tool can convert image points through (roadmap M3.11): every successful capture is registered
/// under a captureId that is never reused while the server runs, and found again by it until it expires or newer
/// captures push it out.
/// </summary>
public interface ICaptureRegistry
{
    /// <summary>How many captures are kept: registering one more drops the one used least recently.</summary>
    int Capacity { get; }

    /// <summary>How long a capture can be found after it was taken; an older image is too old to click on.</summary>
    TimeSpan TimeToLive { get; }

    /// <summary>Registers a capture under a new id, now.</summary>
    CaptureRecord Register(CaptureGeometry geometry);

    /// <summary>
    /// The capture with <paramref name="captureId"/>, which then counts as used most recently; null when no capture has
    /// that id, it expired (<see cref="TimeToLive"/>) or newer captures pushed it out (<see cref="Capacity"/>).
    /// </summary>
    CaptureRecord? Find(string captureId);
}
