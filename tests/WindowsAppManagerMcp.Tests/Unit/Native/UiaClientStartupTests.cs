using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for <see cref="UiaClientStartup"/>, the gate around the UI Automation client's first
/// call. The first call here is a fake that fails the way the real client does when two threads
/// make their first call at once; nothing touches UI Automation.
/// </summary>
public class UiaClientStartupTests
{
    [Fact]
    public void EnsureStarted_MakesTheFirstCallOnlyOnce()
    {
        // Arrange
        var calls = 0;
        var sut = new UiaClientStartup(() => calls++);

        // Act
        sut.EnsureStarted();
        sut.EnsureStarted();
        sut.EnsureStarted();

        // Assert
        calls.Should().Be(1);
        sut.IsStarted.Should().BeTrue();
    }

    [Fact]
    public void EnsureStarted_FromManyThreadsAtOnce_NeverOverlapsTheFirstCall()
    {
        // Arrange - like the real client: a first call fails while another one is in progress
        var inside = 0;
        var calls = 0;
        var sut = new UiaClientStartup(() =>
        {
            Interlocked.Increment(ref calls);
            if (Interlocked.Increment(ref inside) > 1)
            {
                Interlocked.Decrement(ref inside);
                throw new COMException("Error HRESULT E_FAIL has been returned from a call to a COM component.");
            }

            Thread.Sleep(50);
            Interlocked.Decrement(ref inside);
        });
        using var go = new ManualResetEventSlim();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            go.Wait();
            try
            {
                sut.EnsureStarted();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToList();

        // Act
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());

        // Assert - the others waited for the first call instead of making their own
        failures.Should().BeEmpty();
        calls.Should().Be(1);
        sut.IsStarted.Should().BeTrue();
    }

    [Fact]
    public void EnsureStarted_WhenTheFirstCallFails_ThrowsAndTriesAgainNextTime()
    {
        // Arrange
        var calls = 0;
        var sut = new UiaClientStartup(() =>
        {
            if (++calls == 1)
            {
                throw new COMException("E_FAIL");
            }
        });

        // Act
        var first = () => sut.EnsureStarted();

        // Assert
        first.Should().Throw<COMException>();
        sut.IsStarted.Should().BeFalse();

        sut.EnsureStarted();
        sut.IsStarted.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public void Constructor_RejectsANullFirstCall()
    {
        var act = () => new UiaClientStartup(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
