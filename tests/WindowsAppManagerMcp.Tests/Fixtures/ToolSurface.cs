using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The server's tool surface as an MCP client sees it: the tools/list result of an in-process server with the
/// production tool and filter registrations (<see cref="ServiceRegistration.AddWindowsAppManagerTools"/>, the
/// same call Program.cs makes). Listing tools creates no tool class and no service, so nothing native runs.
/// </summary>
/// <remarks>
/// The checked-in snapshot (<see cref="SnapshotPath"/>) holds every tool object exactly as the server sends it,
/// sorted by name. scripts/Test-ToolsSnapshot.ps1 compares the tools/list of a built exe with the same file in CI.
/// </remarks>
public static class ToolSurface
{
    /// <summary>The checked-in tools/list snapshot, relative to the repository root.</summary>
    public static readonly string[] SnapshotRelativePath = ["tests", "WindowsAppManagerMcp.Tests", "Contract", "tools.snapshot.json"];

    /// <summary>The note at the top of the snapshot file.</summary>
    public const string SnapshotComment =
        "The tools/list result of the server, sorted by tool name. Generated: do not edit. After a deliberate change to the " +
        "tool surface, run the tests with UPDATE_SNAPSHOTS=1 and review the diff (see TESTING.md, \"Tool surface snapshot\").";

    /// <summary>
    /// Formatting of the snapshot: indented, and only the escaping JSON needs, so quotes, apostrophes and
    /// non-ASCII text in descriptions stay readable in a diff.
    /// </summary>
    public static readonly JsonSerializerOptions SnapshotFormat = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Lazy<Task<JsonArray>> LazyTools = new(() => ListToolsFromServerAsync(revision: null));

    public static string SnapshotPath => RepositoryFiles.PathOf(SnapshotRelativePath);

    /// <summary>
    /// The tools of the server's tools/list result, as raw JSON objects sorted by name. Listed once per test run;
    /// each call returns a copy the caller may change.
    /// </summary>
    public static async Task<JsonArray> ListToolsAsync() => (JsonArray)(await LazyTools.Value).DeepClone();

    /// <summary>The snapshot document for <paramref name="tools"/>, as written to <see cref="SnapshotPath"/> (LF line ends).</summary>
    public static string ToSnapshotText(JsonArray tools)
    {
        var document = new JsonObject
        {
            ["$comment"] = SnapshotComment,
            ["tools"] = tools.DeepClone()
        };
        return document.ToJsonString(SnapshotFormat).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The tools of a snapshot document, or null when the file does not exist.</summary>
    public static JsonArray? ReadSnapshotTools()
    {
        if (!File.Exists(SnapshotPath))
        {
            return null;
        }

        var document = JsonNode.Parse(File.ReadAllText(SnapshotPath, Encoding.UTF8))
            ?? throw new InvalidOperationException($"{SnapshotPath} is empty.");
        return document["tools"]?.AsArray()
            ?? throw new InvalidOperationException($"{SnapshotPath} has no \"tools\" array.");
    }

    /// <summary>A tool object by name.</summary>
    public static JsonObject Tool(JsonArray tools, string name) =>
        tools.OfType<JsonObject>().SingleOrDefault(t => (string?)t["name"] == name)
        ?? throw new KeyNotFoundException($"tools/list has no tool named '{name}'.");

    /// <summary>
    /// Lists the tools of a new in-process server whose client negotiates <paramref name="revision"/> (null: the SDK
    /// client's default, <see cref="InProcessMcpServer.LatestRevision"/>), sorted by name.
    /// </summary>
    public static async Task<JsonArray> ListToolsFromServerAsync(string? revision)
    {
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(null, revision));

        var tools = new List<JsonObject>();
        string? cursor = null;
        do
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await server.Client.SendRequestAsync(
                new JsonRpcRequest
                {
                    Method = RequestMethods.ToolsList,
                    Params = cursor == null ? null : new JsonObject { ["cursor"] = cursor }
                },
                timeout.Token);

            var result = response.Result?.AsObject()
                ?? throw new InvalidOperationException("tools/list returned no result.");
            tools.AddRange(result["tools"]!.AsArray().Select(tool => tool!.DeepClone().AsObject()));
            cursor = (string?)result["nextCursor"];
        }
        while (cursor != null);

        return new JsonArray(tools
            .OrderBy(tool => (string?)tool["name"], StringComparer.Ordinal)
            .Select(JsonNode? (tool) => tool)
            .ToArray());
    }
}

/// <summary>
/// The UPDATE_SNAPSHOTS=1 switch: the snapshot and generated-docs tests rewrite their files instead of failing
/// when the files are out of date. See TESTING.md. A CI run refuses it (<see cref="IsUpdateRequested"/>).
/// </summary>
public static class Snapshots
{
    public const string UpdateVariable = "UPDATE_SNAPSHOTS";

    /// <summary>
    /// Variables a CI service sets to "true": GitHub Actions sets CI and GITHUB_ACTIONS, Azure Pipelines TF_BUILD, and
    /// most other services CI.
    /// </summary>
    private static readonly string[] CiVariables = ["CI", "GITHUB_ACTIONS", "TF_BUILD"];

    public static bool UpdateRequested => IsUpdateRequested(Environment.GetEnvironmentVariable);

    /// <summary>
    /// True when <see cref="UpdateVariable"/> is "1". Throws when it is set in a CI run: there the snapshot and docs
    /// tests would rewrite their files on the runner and pass, which turns the guards off without anyone noticing.
    /// </summary>
    public static bool IsUpdateRequested(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment(UpdateVariable) != "1")
        {
            return false;
        }

        var ci = CiVariables.FirstOrDefault(name => string.Equals(environment(name), "true", StringComparison.OrdinalIgnoreCase));
        if (ci != null)
        {
            throw new InvalidOperationException(
                $"{UpdateVariable}=1 is refused in CI ({ci} is true): it would rewrite the tool snapshot and the generated docs " +
                "on the runner instead of checking them. Regenerate them locally and commit the result (see TESTING.md).");
        }

        return true;
    }

    /// <summary>
    /// The text of a repository file with LF line ends, or null when it does not exist.
    /// </summary>
    public static string? ReadNormalized(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).ReplaceLineEndings("\n") : null;

    /// <summary>
    /// Writes <paramref name="text"/> (LF line ends) to <paramref name="path"/>, keeping the line ends (CRLF or
    /// LF) and the byte order mark of the file that is there, so a regenerated file differs from the checked-out
    /// one only where its content changed. The file is replaced in one step (a temporary file moved over it), so
    /// a test that reads it in parallel sees the old or the new text, never a truncated file.
    /// </summary>
    public static void WritePreservingFormat(string path, string text)
    {
        var crlf = false;
        var bom = false;
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            crlf = Encoding.UTF8.GetString(bytes).Contains("\r\n", StringComparison.Ordinal);
        }

        var content = crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(bom));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
