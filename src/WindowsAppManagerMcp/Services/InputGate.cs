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
/// code that holds it must not call into an input tool or a filtered tool. Code that holds it must not wait
/// without a bound either, or every input and window tool waits with it: window calls that can block on another
/// thread's window go through <see cref="Interfaces.IWindowCallRunner"/>.
/// </remarks>
public static class InputGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// How long an input call waits for the gate before it gives up with InputBusy (roadmap M1.10): an input
    /// call must not wait forever behind a long window-changing call or another input call. A window-changing
    /// tool waits as long (<see cref="Infrastructure.InputGateToolFilter.GateWaitTimeout"/>).
    /// </summary>
    public static readonly TimeSpan InputWaitTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// Waits at most <paramref name="timeout"/> for the gate; null when it is still held then. Disposing the
    /// result releases the gate.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public static IDisposable? TryEnter(TimeSpan timeout, CancellationToken cancellationToken) =>
        Gate.Wait(timeout, cancellationToken) ? new Scope() : null;

    /// <inheritdoc cref="TryEnter"/>
    public static async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Scope() : null;

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
