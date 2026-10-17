namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The input gate's test hook (the final review's TEST-1): IsHeld says that some call holds the process-wide gate, which
/// parallel tests do all the time; IsHeldBy says that the code a test runs holds it, so an assertion that a tool acts
/// inside the gate cannot pass because another test held it at that moment. And the gate's one reentrant use (roadmap
/// M6.2): run_steps holds it for its whole run through a lease, and only the flow that bound the lease passes it again.
/// </summary>
public class InputGateTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task IsHeldBy_IsTrue_WhileCodeOnTheTaggedFlowHoldsTheGate_AndFalseOnceItIsReleased()
    {
        var flow = InputGate.TagFlow();
        bool heldInside;

        using (await InputGate.EnterAsync(CancellationToken.None))
        {
            heldInside = await Task.Run(() => InputGate.IsHeldBy(flow)); // read from another thread, as a mock's callback may be
        }

        heldInside.Should().BeTrue();
        InputGate.IsHeldBy(flow).Should().BeFalse();
    }

    [Fact]
    public async Task IsHeldBy_IsFalse_WhileAnotherFlowHoldsTheGate()
    {
        // Arrange - this test's flow is tagged; the gate is taken by code that does not run under the tag
        var flow = InputGate.TagFlow();
        using var taken = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using (InputGate.Enter())
            {
                taken.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        ExecutionContext.SuppressFlow();
        try
        {
            other.Start();
        }
        finally
        {
            ExecutionContext.RestoreFlow();
        }

        try
        {
            // Act
            await Task.Run(() => taken.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue());

            // Assert
            InputGate.IsHeld.Should().BeTrue();
            InputGate.IsHeldBy(flow).Should().BeFalse("another call holds the gate, not this test's");
        }
        finally
        {
            release.Set();
            other.Join(TimeSpan.FromSeconds(30));
        }
    }

    // ---- The run lease (roadmap M6.2) ----

    [Fact]
    public async Task UnderABoundLease_EveryWayIn_PassesAtOnceOnTheSameFlow_AndReleasesNothing()
    {
        // Arrange
        using var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease.Should().NotBeNull();
        lease!.Bind();

        // Act - each nested enter is what a step's input service or self-gated tool does; none may wait for the run's own gate
        using (var nested = InputGate.TryEnter(Short, CancellationToken.None))
        using (var nestedAsync = await InputGate.TryEnterAsync(Short, CancellationToken.None))
        using (InputGate.Enter())
        using (await InputGate.EnterAsync(CancellationToken.None))
        {
            nested.Should().NotBeNull();
            nestedAsync.Should().NotBeNull();
        }

        // Assert - leaving the nested scopes released nothing: the run still holds the gate
        InputGate.IsHeld.Should().BeTrue();
        InputGate.HeldByThisFlow().Should().BeTrue();
        OtherFlowGetsTheGate(Short).Should().BeFalse("the run's lease still holds the gate");
    }

    [Fact]
    public async Task AStepTheRunAwaits_SeesTheLease_ThroughItsOwnAwaits()
    {
        using var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease!.Bind();

        // A step: called and awaited by the run, with awaits of its own that resume on other threads
        static async Task<bool> Step()
        {
            await Task.Yield();
            using var gate = await InputGate.TryEnterAsync(Short, CancellationToken.None);
            return gate != null;
        }

        (await Step()).Should().BeTrue();
    }

    [Fact]
    public async Task AnotherFlow_WaitsForTheLease_AndGetsTheGateOnceItEnds()
    {
        // Arrange
        var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease!.Bind();

        // Act and assert - a parallel call is a flow that never bound the lease
        OtherFlowGetsTheGate(Short).Should().BeFalse("a parallel call waits for the run's gate like any other");
        lease.Dispose();
        OtherFlowGetsTheGate(Long).Should().BeTrue("ending the run releases the gate");
    }

    [Fact]
    public async Task ALeaseThatIsNotBound_IsNotReentrant()
    {
        // The holder binds it on its own flow: an AsyncLocal set inside TryHoldAsync would not flow back to the caller
        using var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);

        InputGate.HeldByThisFlow().Should().BeFalse();
        using var nested = InputGate.TryEnter(Short, CancellationToken.None);
        nested.Should().BeNull("only the flow that bound the lease passes the gate again");
    }

    [Fact]
    public async Task WorkTheRunStartedThatOutlivesIt_CannotPassTheGateAfterTheRun()
    {
        // Arrange - fire-and-forget work a step started inherits the run's flow, and with it the lease
        var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease!.Bind();
        var runEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = Task.Run(async () =>
        {
            sawLease.SetResult(InputGate.HeldByThisFlow());
            await runEnded.Task;
            using var gate = InputGate.TryEnter(Short, CancellationToken.None);
            return gate != null;
        });

        // Act - the run ends, and another call takes the gate before the work tries it
        var during = await sawLease.Task.WaitAsync(Long);
        lease.Dispose();
        using var someoneElse = await OtherFlowTakesTheGateAsync();
        runEnded.SetResult();
        var passedAfter = await leaked.WaitAsync(Long);

        // Assert
        during.Should().BeTrue("the work ran inside the run");
        passedAfter.Should().BeFalse("the run ended: its lease passes nobody, and another call holds the gate");
    }

    [Fact]
    public async Task TheRunsOwnFlow_AfterTheLeaseEnded_WaitsLikeAnyOtherCall()
    {
        // The run's flow itself after the run ended (code after the run's finally): the gate is someone else's now
        var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease!.Bind();
        lease.Dispose();
        using var someoneElse = await OtherFlowTakesTheGateAsync();

        using var nested = InputGate.TryEnter(Short, CancellationToken.None);

        nested.Should().BeNull();
        InputGate.HeldByThisFlow().Should().BeFalse();
    }

    [Fact]
    public async Task TryHoldAsync_IsNull_WhileAnotherCallHoldsTheGate()
    {
        using var someoneElse = await OtherFlowTakesTheGateAsync();

        using var lease = await InputGate.TryHoldAsync(Short, CancellationToken.None);

        lease.Should().BeNull();
    }

    [Fact]
    public async Task ALeaseTakenOnATaggedFlow_IsHeldByThatFlow()
    {
        var flow = InputGate.TagFlow();

        using (var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None))
        {
            lease!.Bind();
            InputGate.IsHeldBy(flow).Should().BeTrue();
        }

        InputGate.IsHeldBy(flow).Should().BeFalse();
    }

    [Fact]
    public async Task DisposingALeaseTwice_ReleasesTheGateOnce()
    {
        var lease = await InputGate.TryHoldAsync(Long, CancellationToken.None);
        lease!.Dispose();
        using var someoneElse = await OtherFlowTakesTheGateAsync();

        lease.Dispose();

        OtherFlowGetsTheGate(Short).Should().BeFalse("a second Dispose must not release the gate another call holds");
    }

    /// <summary>
    /// True when a flow that did not inherit this one (a thread started with the execution context suppressed, as a parallel
    /// call is) gets the gate within <paramref name="wait"/>; it releases it at once.
    /// </summary>
    private static bool OtherFlowGetsTheGate(TimeSpan wait)
    {
        var got = false;
        Thread other;
        using (ExecutionContext.SuppressFlow())
        {
            other = new Thread(() =>
            {
                using var gate = InputGate.TryEnter(wait, CancellationToken.None);
                got = gate != null;
            });
            other.Start();
        }

        other.Join();
        return got;
    }

    /// <summary>Takes the gate on a flow that did not inherit this one, and holds it until the result is disposed.</summary>
    private static async Task<IDisposable> OtherFlowTakesTheGateAsync()
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        Thread other;
        using (ExecutionContext.SuppressFlow())
        {
            other = new Thread(() =>
            {
                using var gate = InputGate.TryEnter(Long, CancellationToken.None);
                taken.SetResult();
                release.Wait(Long);
            });
            other.Start();
        }

        await taken.Task.WaitAsync(Long);
        return new Releaser(() =>
        {
            release.Set();
            other.Join(Long);
            release.Dispose();
        });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
