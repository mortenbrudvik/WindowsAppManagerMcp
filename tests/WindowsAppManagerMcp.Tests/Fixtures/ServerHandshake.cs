using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// What the server answers a client's first request, on each protocol revision: the initialize result (2025-06-18,
/// 2025-11-25) or the server/discover result (2026-07-28). A client acts on more than tools/list: the negotiated
/// revision, serverInfo, the instructions and the capabilities object (tools, logging, prompts, resources, completions,
/// extensions). Contract/initialize.snapshot.json holds these results, so an SDK upgrade that changes them (a new
/// capability, another revision) fails ToolSurfaceSnapshotTests until the snapshot is regenerated and the diff reviewed.
/// </summary>
public static class ServerHandshake
{
    /// <summary>The checked-in snapshot, relative to the repository root.</summary>
    public static readonly string[] SnapshotRelativePath = ["tests", "WindowsAppManagerMcp.Tests", "Contract", "initialize.snapshot.json"];

    /// <summary>The note at the top of the snapshot file.</summary>
    public const string SnapshotComment =
        "The result the server sends for a client's first request on each protocol revision (initialize, or server/discover " +
        "on 2026-07-28), with serverInfo.version masked. Generated: do not edit. After a deliberate change, run the tests with " +
        "UPDATE_SNAPSHOTS=1 and review the diff (see TESTING.md, \"Tool surface snapshot\").";

    /// <summary>What replaces serverInfo.version, which changes with every commit.</summary>
    public const string MaskedVersion = "<version>";

    /// <summary>The revisions in the snapshot: the SDK client's default (2026-07-28) and both initialize revisions.</summary>
    public static IReadOnlyList<string> Revisions { get; } =
        [InProcessMcpServer.InitializeRevision, InProcessMcpServer.NewestInitializeRevision, InProcessMcpServer.LatestRevision];

    public static string SnapshotPath => RepositoryFiles.PathOf(SnapshotRelativePath);

    /// <summary>The first result of a server with the production identity and registrations, by revision.</summary>
    public static async Task<JsonObject> ReadResultsAsync()
    {
        var results = new JsonObject();
        foreach (var revision in Revisions)
        {
            results[revision] = await ReadResultAsync(revision);
        }

        return results;
    }

    /// <summary>The snapshot document for <paramref name="results"/> (LF line ends).</summary>
    public static string ToSnapshotText(JsonObject results)
    {
        var document = new JsonObject
        {
            ["$comment"] = SnapshotComment,
            ["results"] = results.DeepClone()
        };
        return document.ToJsonString(ToolSurface.SnapshotFormat).ReplaceLineEndings("\n") + "\n";
    }

    private static async Task<JsonObject> ReadResultAsync(string revision)
    {
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder =>
            {
                // Program.cs: AddMcpServer(ServerIdentity.Configure); this runs after the fixture's own options
                builder.Services.Configure<McpServerOptions>(ServerIdentity.Configure);
                builder.AddWindowsAppManagerTools();
            },
            clientOptions: InProcessMcpServer.WithRevision(null, revision));

        // The client's first request is answered first; the recording holds the server's output in order
        var first = server.ServerMessages
            .Select(message => JsonNode.Parse(message)!.AsObject())
            .First(message => message["result"] != null);
        var result = first["result"]!.DeepClone().AsObject();
        ServerInfoOf(result)!["version"] = MaskedVersion;
        return result;
    }

    /// <summary>
    /// The serverInfo of a first result: a member of the initialize result, and in the server/discover result of
    /// 2026-07-28 the _meta entry io.modelcontextprotocol/serverInfo.
    /// </summary>
    public static JsonNode? ServerInfoOf(JsonObject result) =>
        result["serverInfo"] ?? result["_meta"]?["io.modelcontextprotocol/serverInfo"];

    /// <summary>The revisions a first result names: protocolVersion (initialize) or supportedVersions (server/discover).</summary>
    public static IReadOnlyList<string> RevisionsOf(JsonObject result) =>
        result["protocolVersion"] is { } negotiated
            ? [(string)negotiated!]
            : result["supportedVersions"]?.AsArray().Select(v => (string)v!).ToList() ?? [];
}
