using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CdpTransport (roadmap M5.2): the /json requests and the WebSocket connect, over an in-process fake
/// HTTP handler and WebSocket factory. No network is involved.
/// </summary>
public class CdpTransportTests
{
    private const int Port = 9222;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // Generous bound so slow CI machines do not flake; a hang would never finish.
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private static readonly Uri TabUrl = new("ws://127.0.0.1:9222/devtools/page/TAB1");

    private static CdpTransport CreateSut(
        FakeCdpHttpHandler? http = null,
        ICdpWebSocketFactory? sockets = null,
        CdpTransportLimits? limits = null) =>
        new(http ?? FakeCdpHttpHandler.Json("[]"), sockets ?? FakeCdpWebSocketFactory.NeverConnects(), limits);

    #region GetJsonAsync

    [Fact]
    public async Task GetJsonAsync_GetsThePageFromTheLoopbackAddress()
    {
        var http = FakeCdpHttpHandler.Json("""{ "Browser": "Edg/141.0" }""");
        using var sut = CreateSut(http);

        var result = await sut.GetJsonAsync(Port, "/json/version", LongTimeout, CancellationToken.None);

        result.Success.Should().BeTrue();
        using var document = result.Value!;
        document.RootElement.GetProperty("Browser").GetString().Should().Be("Edg/141.0");
        http.Requests.Should().ContainSingle().Which.Should().Be(new Uri("http://127.0.0.1:9222/json/version"));
    }

    [Fact]
    public async Task GetJsonAsync_OnlyRequestsJsonPages()
    {
        using var sut = CreateSut();

        var act = () => sut.GetJsonAsync(Port, "/devtools/page/TAB1", LongTimeout, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsCdpUnavailable_WhenNothingListens()
    {
        using var sut = CreateSut(FakeCdpHttpHandler.ConnectionRefused());

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("--remote-debugging-port=9222");
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    public async Task GetJsonAsync_ReturnsCdpProtocolError_WhenSomethingAnswersButNotAsDevTools(HttpRequestError error)
    {
        using var sut = CreateSut(FakeCdpHttpHandler.Fails(error));

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("answered but not like a DevTools endpoint");
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsCdpResponseTooLarge_FromTheContentLength_WithoutReadingTheBody()
    {
        var http = FakeCdpHttpHandler.Body(() => new ByteArrayContent(new byte[2048]));
        using var sut = CreateSut(http, limits: new CdpTransportLimits(MaxHttpBodyBytes: 1024));

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        result.Error.Should().Contain("1024 bytes").And.NotContain("--remote-debugging-port");
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsCdpResponseTooLarge_ForABodyWithoutContentLength()
    {
        var body = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"id\":\"x\"}", 500)) + "]");
        var http = FakeCdpHttpHandler.Body(() => FakeCdpHttpHandler.UnsizedBody(body));
        using var sut = CreateSut(http, limits: new CdpTransportLimits(MaxHttpBodyBytes: 1024));

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
    }

    [Fact]
    public async Task GetJsonAsync_ReadsABodyWithoutContentLength_UnderTheLimit()
    {
        var http = FakeCdpHttpHandler.Body(() => FakeCdpHttpHandler.UnsizedBody(Encoding.UTF8.GetBytes("[1,2,3]")));
        using var sut = CreateSut(http);

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.Value!.RootElement.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsCdpTimeout_WhenTheEndpointNeverAnswers()
    {
        using var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetJsonAsync(Port, "/json/list", ShortTimeout, CancellationToken.None);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("200 ms").And.Contain("/json/list");
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsCdpProtocolError_ForABodyThatIsNotJson()
    {
        using var sut = CreateSut(FakeCdpHttpHandler.Json("<html>hello</html>"));

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetJsonAsync_ReturnsCdpProtocolError_ForAnHttpErrorOrRedirectStatus(HttpStatusCode status)
    {
        using var sut = CreateSut(FakeCdpHttpHandler.Json("{}", status));

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain($"HTTP {(int)status}").And.Contain("not like a DevTools endpoint");
    }

    [Fact]
    public async Task GetJsonAsync_RefusesAnAnswerFromAnotherAddressThanTheOneItAsked()
    {
        // A handler that followed a redirect reports the final request; that answer is not 127.0.0.1:port's
        var http = new FakeCdpHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9333/json/list")
        }));
        using var sut = CreateSut(http);

        var result = await sut.GetJsonAsync(Port, "/json/list", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("127.0.0.1:9333");
    }

    [Fact]
    public void CreateHttpHandler_FollowsNoRedirect_AndUsesNoProxyOrCookies()
    {
        // The server's own transport (ServiceRegistration) is built with this handler
        using var handler = CdpTransport.CreateHttpHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }

    [Fact]
    public void Dispose_DisposesTheHttpHandler()
    {
        var http = FakeCdpHttpHandler.Json("[]");
        var sut = CreateSut(http);

        sut.Dispose();

        http.WasDisposed.Should().BeTrue();
    }

    #endregion

    #region ConnectAsync

    [Fact]
    public async Task ConnectAsync_OpensAConnectionToTheUrl()
    {
        var socket = new FakeCdpWebSocket((s, command) => s.ReplyEmpty(command));
        var factory = new FakeCdpWebSocketFactory(socket);
        using var sut = CreateSut(sockets: factory);

        var result = await sut.ConnectAsync(TabUrl, Port, "tab TAB1", LongTimeout, CancellationToken.None);

        result.Success.Should().BeTrue();
        await using var connection = result.Value!;
        (await connection.SendAsync("Page.enable", null, LongTimeout, CancellationToken.None)).Success.Should().BeTrue();
        factory.ConnectedUris.Should().Equal(TabUrl);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:9333/devtools/page/TAB1")]
    [InlineData("ws://203.0.113.5:9222/devtools/page/TAB1")]
    public async Task ConnectAsync_RefusesAUrlOffTheLoopbackOrThePort_WithoutConnecting(string url)
    {
        var factory = new FakeCdpWebSocketFactory(new FakeCdpWebSocket());
        using var sut = CreateSut(sockets: factory);

        var result = await sut.ConnectAsync(new Uri(url), Port, "tab TAB1", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        factory.ConnectedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectAsync_ReturnsCdpTimeout_WhenTheConnectNeverFinishes()
    {
        using var sut = CreateSut(sockets: FakeCdpWebSocketFactory.NeverConnects());

        var result = await sut.ConnectAsync(TabUrl, Port, "tab TAB1", ShortTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("WebSocket").And.Contain("200 ms");
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenTheCallerCancels()
    {
        using var sut = CreateSut(sockets: FakeCdpWebSocketFactory.NeverConnects());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => sut.ConnectAsync(TabUrl, Port, "tab TAB1", LongTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConnectAsync_ExplainsAHandshakeRefusedWith403()
    {
        // What ClientWebSocket throws when the browser answers the upgrade with 403 (an Origin it does not allow)
        var refused = new WebSocketException(WebSocketError.NotAWebSocket,
            "The server returned status code '403' when status code '101' was expected.");
        using var sut = CreateSut(sockets: FakeCdpWebSocketFactory.Failing(refused));

        var result = await sut.ConnectAsync(TabUrl, Port, "tab TAB1", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("403").And.Contain("--remote-allow-origins");
    }

    [Fact]
    public async Task ConnectAsync_ReturnsCdpUnavailable_WhenTheConnectFails()
    {
        var failed = new WebSocketException(WebSocketError.Faulted, "Unable to connect to the remote server");
        using var sut = CreateSut(sockets: FakeCdpWebSocketFactory.Failing(failed));

        var result = await sut.ConnectAsync(TabUrl, Port, "tab TAB1", LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("Unable to connect").And.NotContain("--remote-allow-origins");
    }

    [Fact]
    public async Task ConnectAsync_GivesTheConnectionTheMessageLimit()
    {
        var socket = new FakeCdpWebSocket((s, _) => s.EnqueueRaw(new byte[4096]));
        using var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket), limits: new CdpTransportLimits(MaxMessageBytes: 1024));

        var connected = await sut.ConnectAsync(TabUrl, Port, "tab TAB1", LongTimeout, CancellationToken.None);
        await using var connection = connected.Value!;
        var result = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
    }

    #endregion
}
