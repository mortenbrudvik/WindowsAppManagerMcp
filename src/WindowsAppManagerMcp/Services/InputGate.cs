namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The process-wide input gate. An input call holds it from its first guard check to its last SendInput
/// batch, so the input of two calls never interleaves. The tools that can change which window is in front
/// or at a point (move, focus, restore, close, launch, kill, apply a layout) hold it for their whole call
/// (<see cref="Infrastructure.InputGateToolFilter"/>), so a parallel call cannot bring another window, such
/// as the agent's own terminal, to the front between an input call's check and its input.
/// </summary>
/// <remarks>
/// A semaphore rather than a lock, because the tool filter holds it across awaits. It is not reentrant:
/// code that holds it must not call into an input tool or a filtered tool.
/// </remarks>
public static class InputGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Waits for the gate; disposing the result releases it.</summary>
    public static IDisposable Enter()
    {
        Gate.Wait();
        return new Scope();
    }

    /// <summary>Waits for the gate without blocking a thread; disposing the result releases it.</summary>
    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Scope();
    }

    /// <summary>True while a call holds the gate (for tests).</summary>
    public static bool IsHeld => Gate.CurrentCount == 0;

    private sealed class Scope : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}
