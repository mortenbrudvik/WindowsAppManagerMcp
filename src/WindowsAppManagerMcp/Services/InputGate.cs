namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The process-wide input gate. An input call holds it from its first guard check to its last SendInput
/// batch, so the input of two calls never interleaves. The tools that can change which window is in front
/// or at a point (move, focus, restore, close, launch, kill, apply a layout) hold it for their whole call
/// (<see cref="Infrastructure.InputGateToolFilter"/>), so a parallel call cannot bring another window, such
/// as the agent's own terminal, to the front between an input call's check and its input.
/// </summary>
/// <remarks>
/// <para>A semaphore rather than a lock, because the tool filter holds it across awaits. It is reentrant only for a run of
/// steps (run_steps, roadmap M6.2). Code that holds it through <see cref="Enter"/>, <see cref="EnterAsync"/>,
/// <see cref="TryEnter"/> or <see cref="TryEnterAsync"/> must not call into an input tool or a filtered tool: that call
/// would wait for the gate its own caller holds.</para>
/// <para>A run takes the gate once with <see cref="TryHoldAsync"/> and binds the <see cref="Lease"/> to its own flow
/// (<see cref="Lease.Bind"/>). From then on every way in passes at once, and releases nothing, on that flow and on
/// everything it awaits or starts: the steps' input services, the gate filter, the self-gated tools. Every other flow
/// waits as for any holder, or gets InputBusy. Ending the lease deactivates it before it releases the gate, so work a
/// step started that outlives the run, and still carries the lease, cannot pass the gate afterwards.</para>
/// <para>Code that holds the gate must not wait without a bound, or every input and window tool waits with it: window
/// calls that can block on another thread's window go through <see cref="Interfaces.IWindowCallRunner"/>, and a run is
/// bounded by its maxDurationMs (60 seconds at most).</para>
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

    /// <summary>
    /// Waits for the gate; disposing the result releases it. On the flow that holds the gate through a bound
    /// <see cref="Lease"/>, it passes at once and disposing the result releases nothing.
    /// </summary>
    public static IDisposable Enter()
    {
        if (HeldByThisFlow())
        {
            return NoScope.Instance;
        }

        Gate.Wait();
        return new Scope();
    }

    /// <summary>Waits for the gate without blocking a thread; disposing the result releases it. As <see cref="Enter"/> under a lease.</summary>
    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (HeldByThisFlow())
        {
            return NoScope.Instance;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Scope();
    }

    /// <summary>
    /// Waits at most <paramref name="timeout"/> for the gate; null when it is still held then. Disposing the
    /// result releases the gate. As <see cref="Enter"/> under a lease.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public static IDisposable? TryEnter(TimeSpan timeout, CancellationToken cancellationToken) =>
        HeldByThisFlow() ? NoScope.Instance
        : Gate.Wait(timeout, cancellationToken) ? new Scope() : null;

    /// <inheritdoc cref="TryEnter"/>
    public static async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        HeldByThisFlow() ? NoScope.Instance
        : await Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Scope() : null;

    /// <summary>
    /// Waits at most <paramref name="timeout"/> for the gate and holds it for a run of steps (run_steps, roadmap M6.2);
    /// null when it is still held then. The lease makes the gate reentrant only once the holder calls
    /// <see cref="Lease.Bind"/> on its own flow, synchronously: an AsyncLocal set inside this method would not flow back
    /// to the caller. Disposing the lease ends it and releases the gate.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public static async Task<Lease?> TryHoldAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var lease = new Lease();
        Volatile.Write(ref _holderTag, FlowTag.Value);
        Volatile.Write(ref _holderLease, lease);
        return lease;
    }

    /// <summary>
    /// True when this flow, or the flow it was started from, bound the lease that holds the gate now, and that lease has
    /// not ended: the gate passes this flow at once.
    /// </summary>
    public static bool HeldByThisFlow() =>
        CurrentLease.Value is { IsActive: true } lease && ReferenceEquals(lease, Volatile.Read(ref _holderLease));

    /// <summary>
    /// True while a call holds the gate, any call in the process. Tests that run in parallel take the gate too, so a test
    /// that asks whether the code it calls holds the gate uses <see cref="IsHeldBy"/>.
    /// </summary>
    public static bool IsHeld => Gate.CurrentCount == 0;

    /// <summary>
    /// Tags the current flow and everything it starts from now on (its awaits, its tasks, an in-process server started
    /// after it), and returns the tag, for <see cref="IsHeldBy"/> (tests). The server never tags a flow.
    /// </summary>
    public static object TagFlow()
    {
        var tag = new object();
        FlowTag.Value = tag;
        return tag;
    }

    /// <summary>
    /// True while the gate is held by code that runs under <paramref name="tag"/> (<see cref="TagFlow"/>): the call a test
    /// made, not a call another test made in parallel.
    /// </summary>
    public static bool IsHeldBy(object tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return IsHeld && ReferenceEquals(Volatile.Read(ref _holderTag), tag);
    }

    // The tag of the flow that holds the gate (tests); null when it is free or its holder's flow is not tagged
    private static readonly AsyncLocal<object?> FlowTag = new();
    private static object? _holderTag;

    // The lease a flow bound (it flows into everything that flow awaits or starts), and the lease that holds the gate now
    private static readonly AsyncLocal<Lease?> CurrentLease = new();
    private static Lease? _holderLease;

    /// <summary>
    /// The gate held for a whole run of steps (<see cref="TryHoldAsync"/>). <see cref="Bind"/> makes it reentrant for the
    /// holder's flow; <see cref="Dispose"/> ends it: it stops passing anyone first, then releases the gate, once.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private int _ended;

        internal Lease()
        {
        }

        /// <summary>False once the lease has ended: it passes nobody, even a flow that still carries it.</summary>
        public bool IsActive => Volatile.Read(ref _ended) == 0;

        /// <summary>
        /// Makes the gate reentrant for the calling flow and everything it awaits or starts from now on. Synchronous on
        /// purpose: the holder calls it on its own flow after <see cref="TryHoldAsync"/> returned.
        /// </summary>
        public void Bind()
        {
            if (IsActive)
            {
                CurrentLease.Value = this;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                // Inactive before the release: a task that inherited the lease and outlives the run cannot pass the gate
                // once another call holds it
                Volatile.Write(ref _holderLease, null);
                Volatile.Write(ref _holderTag, null);
                Gate.Release();
            }
        }
    }

    /// <summary>What a nested enter on the lease holder's flow gets: disposing it releases nothing.</summary>
    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _released;

        /// <summary>Created once the gate is taken, on the taker's flow.</summary>
        public Scope()
        {
            Volatile.Write(ref _holderTag, FlowTag.Value);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Volatile.Write(ref _holderTag, null);
                Gate.Release();
            }
        }
    }
}
