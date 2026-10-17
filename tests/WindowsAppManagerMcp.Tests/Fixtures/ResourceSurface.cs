using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The server's resources and prompts as an MCP client sees them (roadmap M6.9): the resources/list,
/// resources/templates/list and prompts/list results of an in-process server with the production registrations, the
/// counterpart of <see cref="ToolSurface"/>. Listing them reads no resource and gets no prompt, so nothing native runs.
/// The checked-in snapshot (<see cref="SnapshotPath"/>) holds each list sorted, and scripts/Test-ToolsSnapshot.ps1 compares
/// the lists of a built exe with the same file in CI.
/// </summary>
public static class ResourceSurface
{
    /// <summary>The checked-in snapshot, relative to the repository root.</summary>
    public static readonly string[] SnapshotRelativePath = ["tests", "WindowsAppManagerMcp.Tests", "Contract", "resources.snapshot.json"];

    /// <summary>The note at the top of the snapshot file.</summary>
    public const string SnapshotComment =
        "The resources/list, resources/templates/list and prompts/list results of the server, each sorted. Generated: do not " +
        "edit. After a deliberate change, run the tests with UPDATE_SNAPSHOTS=1 and review the diff (see TESTING.md, \"Tool " +
        "surface snapshot\").";

    /// <summary>The lists of the snapshot document: the member that holds each, the method that lists it and its sort key.</summary>
    public static IReadOnlyList<(string Member, string Method, string Key)> Lists { get; } =
    [
        ("resources", RequestMethods.ResourcesList, "uri"),
        ("resourceTemplates", RequestMethods.ResourcesTemplatesList, "uriTemplate"),
        ("prompts", RequestMethods.PromptsList, "name"),
    ];

    private static readonly Lazy<Task<JsonObject>> LazyLists = new(() => ListFromServerAsync(toolsets: null));

    public static string SnapshotPath => RepositoryFiles.PathOf(SnapshotRelativePath);

    /// <summary>
    /// The three lists of a server that serves every toolset, by <see cref="Lists"/> member. Listed once per test run; each
    /// call returns a copy the caller may change.
    /// </summary>
    public static async Task<JsonObject> ListAsync() => (JsonObject)(await LazyLists.Value).DeepClone();

    /// <summary>The snapshot document for <paramref name="lists"/> (LF line ends).</summary>
    public static string ToSnapshotText(JsonObject lists)
    {
        var document = new JsonObject { ["$comment"] = SnapshotComment };
        foreach (var (member, _, _) in Lists)
        {
            document[member] = lists[member]!.DeepClone();
        }

        return document.ToJsonString(ToolSurface.SnapshotFormat).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The estimated tokens of the three lists, as the SDK writes them (<see cref="ToolSurface.EstimatedTokens"/>).</summary>
    public static int Tokens(JsonObject lists) =>
        ToolSurface.EstimatedTokens(Lists.Sum(list => ToolSurface.WireBytes(lists[list.Member]!)));

    /// <summary>
    /// The three lists of a new in-process server with the production registrations, each sorted by its key.
    /// </summary>
    /// <param name="toolsets">The toolsets the server serves; null serves every tool (<see cref="ToolsetSelection.All"/>).</param>
    public static async Task<JsonObject> ListFromServerAsync(ToolsetSelection? toolsets)
    {
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(toolsets));

        var lists = new JsonObject();
        foreach (var (member, method, key) in Lists)
        {
            var items = new List<JsonObject>();
            string? cursor = null;
            do
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await server.Client.SendRequestAsync(
                    new JsonRpcRequest { Method = method, Params = cursor == null ? null : new JsonObject { ["cursor"] = cursor } },
                    timeout.Token);
                var result = response.Result?.AsObject() ?? throw new InvalidOperationException($"{method} returned no result.");
                items.AddRange(result[member]!.AsArray().Select(item => item!.DeepClone().AsObject()));
                cursor = (string?)result["nextCursor"];
            }
            while (cursor != null);

            lists[member] = new JsonArray(items
                .OrderBy(item => (string?)item[key], StringComparer.Ordinal)
                .Select(JsonNode? (item) => item)
                .ToArray());
        }

        return lists;
    }
}
