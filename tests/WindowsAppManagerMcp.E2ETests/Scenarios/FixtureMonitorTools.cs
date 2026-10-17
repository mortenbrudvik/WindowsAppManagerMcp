using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Calls a tool through the in-process MCP host (the server's own filters and argument binding over the harness's guarded
/// services) and reads its JSON result, for the roadmap M6.7 scenarios: monitor selectors, percent placement and the
/// z-order. Every call names the fixture's own windows only.
/// </summary>
internal static class FixtureMonitorTools
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The tool's JSON result, logged; a failing call fails the test with its error.</summary>
    public static async Task<JsonObject> CallAsync(InProcessMcpServer server, ITestOutputHelper output, string tool,
        Dictionary<string, object?> arguments, bool expectSuccess = true)
    {
        var result = await server.CallToolAsync(tool, arguments, CallTimeout);
        var text = result.Content.OfType<TextContentBlock>().Last().Text;
        output.WriteLine($"{tool}: isError={result.IsError}: {text}");
        var body = JsonNode.Parse(text)!.AsObject();
        if (expectSuccess)
        {
            (result.IsError ?? false).Should().BeFalse(text);
        }

        return body;
    }

    /// <summary>A visible frame in a result ("bounds").</summary>
    public static WindowRect Frame(JsonObject result) =>
        new(Int(result["bounds"]!["x"]), Int(result["bounds"]!["y"]), Int(result["bounds"]!["width"]), Int(result["bounds"]!["height"]));

    public static int Int(JsonNode? node) => node!.GetValue<int>();

    /// <summary>The root of the window in front, which the M6.7 tools must never change.</summary>
    public static nint ForegroundRoot() => E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER);
}
