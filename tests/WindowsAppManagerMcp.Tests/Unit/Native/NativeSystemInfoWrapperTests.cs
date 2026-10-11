using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The real <see cref="NativeSystemInfoWrapper"/>, against the test process. Every member only reads (the DPI awareness
/// context of the calling thread, SPI_GETFOREGROUNDLOCKTIMEOUT, SPI_GETMOUSEWHEELROUTING and the session ID), so these run in the default suite.
/// The test host has no app manifest of the server's, so its awareness is whatever its own manifest says.
/// </summary>
public class NativeSystemInfoWrapperTests
{
    private readonly NativeSystemInfoWrapper _sut = new();

    [Fact]
    public void GetDpiAwareness_NamesTheTestHostsAwareness()
    {
        // Windows 10 1607 and later always have a context for the calling thread
        _sut.GetDpiAwareness().Should().NotBe(DpiAwareness.Unknown).And.BeDefined();
    }

    [Fact]
    public void GetForegroundLockTimeout_ReadsTheSetting()
    {
        _sut.GetForegroundLockTimeout().Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetMouseWheelRouting_ReadsTheSetting_OrNull()
    {
        // Windows 10 and later have the setting (SPI_GETMOUSEWHEELROUTING); reading it changes nothing. A build or session
        // without it reads null, which scroll treats as focus routing
        if (_sut.GetMouseWheelRouting() is { } routing)
        {
            routing.Should().BeDefined();
        }
    }

    [Fact]
    public void GetSessionId_IsTheTestProcessSession()
    {
        using var current = Process.GetCurrentProcess();

        _sut.GetSessionId().Should().Be(current.SessionId);
    }
}
