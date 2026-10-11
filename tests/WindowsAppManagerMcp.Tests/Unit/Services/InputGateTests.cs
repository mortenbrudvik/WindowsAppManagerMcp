namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The input gate's test hook (the final review's TEST-1): IsHeld says that some call holds the process-wide gate, which
/// parallel tests do all the time; IsHeldBy says that the code a test runs holds it, so an assertion that a tool acts
/// inside the gate cannot pass because another test held it at that moment.
/// </summary>
public class InputGateTests
{
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
}
