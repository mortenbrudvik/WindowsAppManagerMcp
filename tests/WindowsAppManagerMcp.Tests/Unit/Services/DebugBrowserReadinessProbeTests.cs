using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// DebugBrowserReadinessProbe (roadmap M5.1): launch_browser_with_debug's wait for the started browser's DevTools
/// endpoint to answer /json/version. The CDP service is a scripted fake, except where the real CdpService and transport
/// run over the in-memory HTTP fake to show which address is asked; nothing reaches the network.
/// </summary>
public class DebugBrowserReadinessProbeTests
{
    private const int Port = 13538;
    private const int BrowserPid = 4321;
    private const string BrowserWs = "ws://127.0.0.1:13538/devtools/browser/6f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f";

    private static readonly DebugBrowserEndpoint Endpoint = new("edge", BrowserPid, Port, @"C:\Temp\browser-debug-1");
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // Generous bound so slow CI machines do not flake; a wait that ignored its timeout would never finish
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private readonly Mock<ICdpService> _cdp = new(MockBehavior.Strict);
    private readonly List<(int Port, TimeSpan Timeout)> _asked = [];

    private static CdpResult<CdpBrowserVersion> Version() =>
        CdpResult<CdpBrowserVersion>.Ok(new CdpBrowserVersion("Edg/141.0.3537.57", "1.3", new Uri(BrowserWs), Port));

    private static CdpResult<CdpBrowserVersion> Refused() =>
        CdpResult<CdpBrowserVersion>.Fail(BrowserErrorCode.CdpUnavailable, "Could not connect to CDP on 127.0.0.1:13538 (refused).");

    /// <summary>The endpoint answers each GET /json/version with the next of <paramref name="answers"/>, then the last one.</summary>
    private void EndpointAnswers(params CdpResult<CdpBrowserVersion>[] answers)
    {
        var queue = new Queue<CdpResult<CdpBrowserVersion>>(answers);
        _cdp.Setup(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, TimeSpan, CancellationToken>((port, timeout, _) =>
            {
                _asked.Add((port, timeout));
                return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
            });
    }

    private DebugBrowserReadinessProbe CreateSut(int? portOwner = BrowserPid) =>
        new(_cdp.Object, new FakePortOwnerResolver(portOwner))
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            AttemptTimeout = TimeSpan.FromMilliseconds(500)
        };

    [Fact]
    public async Task AnEndpointThatAnswers_IsReady_WithTheBrowserAndItsEndpoints()
    {
        // Arrange
        EndpointAnswers(Version());

        // Act
        var readiness = await CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeTrue();
        readiness.Error.Should().BeNull();
        readiness.BrowserVersion.Should().Be("Edg/141.0.3537.57");
        readiness.CdpEndpoint.Should().Be("http://127.0.0.1:13538");
        readiness.BrowserWebSocketUrl.Should().Be(BrowserWs);
        _asked.Should().ContainSingle().Which.Port.Should().Be(Port);
    }

    [Fact]
    public async Task AnEndpointThatIsNotListeningYet_IsAskedAgain_UntilItAnswers()
    {
        // Arrange - a browser may report its port a moment before its DevTools server answers
        EndpointAnswers(Refused(), Refused(), Version());

        // Act
        var readiness = await CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeTrue();
        _asked.Should().HaveCount(3).And.OnlyContain(a => a.Port == Port);
    }

    [Fact]
    public async Task ATimedOutRequest_IsAskedAgain()
    {
        // Arrange
        EndpointAnswers(CdpResult<CdpBrowserVersion>.Fail(BrowserErrorCode.CdpTimeout, "did not answer"), Version());

        // Act
        var readiness = await CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeTrue();
        _asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnEndpointThatNeverAnswers_IsNotReady_WithinTheTimeout_AndSaysWhy()
    {
        // Arrange
        EndpointAnswers(Refused());
        var timeout = TimeSpan.FromMilliseconds(300);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var readiness = await CreateSut().WaitUntilReadyAsync(Endpoint, timeout, CancellationToken.None);

        // Assert - reported, not thrown; every request bounded by what was left of the wait
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        readiness.Ready.Should().BeFalse();
        readiness.Error.Should().Contain("127.0.0.1:13538").And.Contain("/json/version").And.Contain("0.3 seconds")
            .And.Contain("CdpUnavailable");
        _asked.Should().NotBeEmpty().And.OnlyContain(a => a.Port == Port && a.Timeout <= timeout);
    }

    [Fact]
    public async Task EachRequest_IsBoundedByTheAttemptTimeout()
    {
        // Arrange
        EndpointAnswers(Refused(), Version());

        // Act
        await CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        _asked.Should().OnlyContain(a => a.Timeout <= TimeSpan.FromMilliseconds(500) && a.Timeout > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(BrowserErrorCode.CdpProtocolError)]
    [InlineData(BrowserErrorCode.CdpResponseTooLarge)]
    public async Task AnAnswerThatIsNotADevToolsEndpoint_IsNotReady_AtOnce(BrowserErrorCode code)
    {
        // Arrange - a program that answers, but not as a browser does, will not start answering like one
        EndpointAnswers(CdpResult<CdpBrowserVersion>.Fail(code, "Refusing to connect to WebSocket URL 'ws://10.0.0.5/'."));

        // Act
        var readiness = await CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeFalse();
        readiness.Error.Should().Contain(code.ToString()).And.Contain("Refusing to connect");
        _asked.Should().ContainSingle();
    }

    [Fact]
    public async Task APortHeldByAnotherProcess_IsNotReady_AndIsNeverAsked()
    {
        // Arrange - an explicit port another program took after the server checked it was free
        EndpointAnswers(Version());

        // Act
        var readiness = await CreateSut(portOwner: 999).WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeFalse();
        readiness.Error.Should().Contain("process 999").And.Contain($"processId {BrowserPid}").And.Contain("port 0");
        _asked.Should().BeEmpty();
    }

    [Fact]
    public async Task APortWhoseOwnerCannotBeRead_IsStillChecked()
    {
        // Arrange - the TCP table could not be read; the endpoint's answer still decides
        EndpointAnswers(Version());

        // Act
        var readiness = await CreateSut(portOwner: null).WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert
        readiness.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task TheCallersCancellation_IsThrown()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _cdp.Setup(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, TimeSpan, CancellationToken>((_, _, _) =>
            {
                cancel.Cancel();
                return Task.FromResult(Refused());
            });

        // Act
        var act = () => CreateSut().WaitUntilReadyAsync(Endpoint, LongTimeout, cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ThroughTheRealTransport_OnlyTheLoopbackPortIsAsked()
    {
        // Arrange - the real CdpService and transport over the in-memory HTTP fake
        var http = FakeCdpHttpHandler.Json($$"""{ "Browser": "Chrome/141.0.7390.55", "webSocketDebuggerUrl": "{{BrowserWs}}" }""");
        var cdp = new CdpService(new CdpTransport(http, FakeCdpWebSocketFactory.NeverConnects()), new FakePortOwnerResolver(BrowserPid));
        var sut = new DebugBrowserReadinessProbe(cdp, new FakePortOwnerResolver(BrowserPid));

        // Act
        var readiness = await sut.WaitUntilReadyAsync(Endpoint, LongTimeout, CancellationToken.None);

        // Assert - one GET, and no WebSocket: the factory would never connect
        readiness.Ready.Should().BeTrue();
        readiness.BrowserVersion.Should().Be("Chrome/141.0.7390.55");
        http.Requests.Should().Equal(new Uri("http://127.0.0.1:13538/json/version"));
    }
}
