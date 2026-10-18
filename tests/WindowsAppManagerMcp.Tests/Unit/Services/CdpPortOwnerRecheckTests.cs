namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// CdpService reads the DevTools port's listener again after its request (review F4): the owner is checked before a
/// request, but a browser that exits meanwhile frees its port, and another program on this computer that takes it would
/// answer the request or accept the connection. An answer is used, and a command sent, only while the listener is still
/// the process that was checked; a listener table that cannot be read fails closed. The browser is the in-process fake
/// behind the real CdpTransport; the listener is a fake that changes between reads.
/// </summary>
public class CdpPortOwnerRecheckTests
{
    private const int Port = 9222;
    private const int BrowserPid = 4242;
    private const int OtherPid = 999;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The port's listener as each read finds it, in order; the last answer repeats.</summary>
    private sealed class ChangingPortOwner(params (bool Readable, int? Owner)[] answers) : ITcpPortOwnerResolver
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public int? GetListeningProcessId(int port) => TryGetListeningProcessId(port, out var owner) ? owner : null;

        public bool TryGetListeningProcessId(int port, out int? processId)
        {
            var answer = answers[Math.Min(Interlocked.Increment(ref _reads) - 1, answers.Length - 1)];
            processId = answer.Readable ? answer.Owner : null;
            return answer.Readable;
        }
    }

    private static CdpService CreateSut(FakeCdpHttpHandler http, ICdpWebSocketFactory sockets, ITcpPortOwnerResolver owners) =>
        new(new CdpTransport(http, sockets), owners);

    /// <summary>What the second read finds: another process, nothing, or a table that cannot be read.</summary>
    public static TheoryData<bool, int?> ListenerAfterTheRequest() => new()
    {
        { true, OtherPid },
        { true, null },
        { false, null }
    };

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task FindTargetForWindow_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed(bool readable, int? owner)
    {
        // Arrange
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        // Act
        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetForWindowAsync(Port, new CdpWindow(BrowserPid, "Inbox - Google Chrome"), LongTimeout);

        // Assert - the list may be another program's: its tabs (and their WebSocket URLs) are not used
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain($"CDP port {Port}").And.Contain("Nothing was read");
        owners.Reads.Should().Be(2);
    }

    [Fact]
    public async Task FindTargetForWindow_ABrowserStillOnItsPort_IsRead()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetForWindowAsync(Port, new CdpWindow(BrowserPid, "Inbox - Google Chrome"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be("A");
        owners.Reads.Should().Be(2, "once before the request and once after it");
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task FindTargetById_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed(bool readable, int? owner)
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Nothing was read");
    }

    [Fact]
    public async Task FindTargetById_ABrowserStillOnItsPort_IsRead()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), new ChangingPortOwner((true, BrowserPid)))
            .FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.Success.Should().BeTrue(result.Error);
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task CloseBrowser_AConnectionOpenedAfterTheBrowserLeftItsPort_SendsNothing(bool readable, int? owner)
    {
        // Arrange - the connection may be another program's: Browser.close is not sent on it
        var browser = new FakeCdpBrowser();
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        // Act
        var result = await CreateSut(browser.Http, browser.Sockets, owners).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Nothing was sent");
        browser.CloseCommands.Should().Be(0);
        browser.Socket.WasDisposed.Should().BeTrue("the connection is closed");
    }

    [Fact]
    public async Task CloseBrowser_ABrowserStillOnItsPort_IsSentBrowserClose()
    {
        var browser = new FakeCdpBrowser();
        var owners = new ChangingPortOwner((true, BrowserPid));

        var result = await CreateSut(browser.Http, browser.Sockets, owners).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        browser.CloseCommands.Should().Be(1);
        owners.Reads.Should().Be(2);
    }
}
