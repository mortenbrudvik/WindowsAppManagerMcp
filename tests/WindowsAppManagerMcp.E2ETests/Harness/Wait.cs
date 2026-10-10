using System.Diagnostics;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>Polling helpers: the desktop settles asynchronously, so E2E checks wait instead of sleeping.</summary>
public static class Wait
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Polls <paramref name="condition"/> until it is true or <paramref name="timeout"/> passes.</summary>
    /// <returns>Whether the condition became true.</returns>
    public static bool Until(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
            {
                return true;
            }
            if (stopwatch.Elapsed >= timeout)
            {
                return false;
            }
            Thread.Sleep(interval ?? DefaultInterval);
        }
    }

    /// <summary>
    /// Polls <paramref name="read"/> until it returns a value or <paramref name="timeout"/> passes.
    /// </summary>
    /// <returns>The first non-null value, or null on timeout.</returns>
    public static T? ForValue<T>(Func<T?> read, TimeSpan timeout, TimeSpan? interval = null) where T : class
    {
        T? value = null;
        Until(() => (value = read()) != null, timeout, interval);
        return value;
    }
}
