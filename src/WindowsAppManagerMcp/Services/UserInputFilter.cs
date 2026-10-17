using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Which low-level input counts as the user taking over (roadmap M7.4): only input that was not injected, so neither this
/// server's SendInput nor another program's counts. A key, button or wheel going down counts at once; key and button
/// releases do not (the user lets go of the Enter that sent the prompt). The mouse counts once it moved in a burst of at
/// least <see cref="MovesToCount"/> events within <see cref="MoveBurst"/>, so a nudge of the desk does not stop a run.
/// Used on the one hook thread only.
/// </summary>
public sealed class UserInputFilter
{
    /// <summary>How many of the user's mouse moves within <see cref="MoveBurst"/> count as taking the mouse.</summary>
    public const int MovesToCount = 4;

    /// <summary>The time the mouse moves of one burst must fall within.</summary>
    public static readonly TimeSpan MoveBurst = TimeSpan.FromMilliseconds(300);

    private readonly TimeProvider _time;
    private int _moves;
    private long _burstStart;

    public UserInputFilter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True when <paramref name="input"/> is the user's and counts as taking over.</summary>
    public bool Counts(LowLevelInput input)
    {
        if (input.Injected)
        {
            return false;
        }

        switch (input.Kind)
        {
            case LowLevelInputKind.KeyDown or LowLevelInputKind.ButtonDown or LowLevelInputKind.Wheel:
                return true;
            case LowLevelInputKind.Move:
                var now = _time.GetTimestamp();
                if (_moves == 0 || _time.GetElapsedTime(_burstStart, now) > MoveBurst)
                {
                    _moves = 0;
                    _burstStart = now;
                }

                if (++_moves < MovesToCount)
                {
                    return false;
                }

                _moves = 0;
                return true;
            default:
                return false;
        }
    }
}
