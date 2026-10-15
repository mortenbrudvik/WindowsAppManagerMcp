namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CdpEndpointValidator (roadmap M5.2): what the server agrees to connect to, and which targetIds it
/// looks up. The DevTools endpoint's answers are written by whatever listens on the port, so they are checked.
/// </summary>
public class CdpEndpointValidatorTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:9222/devtools/page/TAB1")]
    [InlineData("ws://localhost:9222/devtools/browser/abc")]
    [InlineData("ws://[::1]:9222/devtools/page/TAB1")]
    public void ValidateWebSocketUrl_AcceptsLoopbackWsOnTheSamePort(string url)
    {
        var result = CdpEndpointValidator.ValidateWebSocketUrl(url, 9222, "tab TAB1");

        result.Success.Should().BeTrue();
        result.Value.Should().Be(new Uri(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void ValidateWebSocketUrl_ReportsAMissingUrl_AsCdpUnavailable(string? url)
    {
        var result = CdpEndpointValidator.ValidateWebSocketUrl(url, 9222, "tab TAB1");

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("tab TAB1");
    }

    [Theory]
    [InlineData("ws://203.0.113.5:9222/devtools/page/TAB1")]
    [InlineData("ws://example.com:9222/devtools/page/TAB1")]
    [InlineData("http://127.0.0.1:9222/devtools/page/TAB1")]
    [InlineData("wss://127.0.0.1:9222/devtools/page/TAB1")]
    public void ValidateWebSocketUrl_RefusesAnotherHostOrScheme(string url)
    {
        var result = CdpEndpointValidator.ValidateWebSocketUrl(url, 9222, "tab TAB1");

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().StartWith("Refusing");
    }

    [Theory]
    [InlineData("ws://127.0.0.1:9333/devtools/page/TAB1")]
    [InlineData("ws://127.0.0.1/devtools/page/TAB1")]
    public void ValidateWebSocketUrl_RefusesAnotherPort_ThanTheOneThatListedIt(string url)
    {
        // A program on 9222 could list a URL that points at another local endpoint
        var result = CdpEndpointValidator.ValidateWebSocketUrl(url, 9222, "tab TAB1");

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().StartWith("Refusing").And.Contain("9222");
    }

    [Theory]
    [InlineData("8C1F0A6E2B7D4F3A9E5C1B2D3F4A5B6C")]
    [InlineData("a")]
    [InlineData("tab-1.main_2")]
    public void IsValidTargetId_AcceptsIds(string id)
    {
        CdpEndpointValidator.IsValidTargetId(id).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../browser")]
    [InlineData("TAB1?x=1")]
    [InlineData("TAB1%2F..")]
    [InlineData("TAB 1")]
    [InlineData("TAB1\n")]
    [InlineData("Tåb")]
    public void IsValidTargetId_RefusesAnythingElse(string? id)
    {
        CdpEndpointValidator.IsValidTargetId(id).Should().BeFalse();
    }

    [Fact]
    public void IsValidTargetId_AcceptsAtMost128Characters()
    {
        CdpEndpointValidator.IsValidTargetId(new string('A', 128)).Should().BeTrue();
        CdpEndpointValidator.IsValidTargetId(new string('A', 129)).Should().BeFalse();
    }
}
