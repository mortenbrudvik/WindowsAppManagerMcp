using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// One scenario's measures (roadmap M7.6): whether it reached its goal, how many tool calls it took, how long it took, and how
/// much the server sent back (text in bytes and estimated tokens, at 4 bytes per token as ToolPayloadBudgetTests counts them,
/// and the images).
/// </summary>
public sealed record ScenarioMeasure(
    string Name,
    bool Success,
    int Calls,
    int FailedCalls,
    long WallMs,
    long TextBytes,
    long TextTokens,
    int Images,
    string? Error = null);

/// <summary>A run of scenarios, as written to its JSON file.</summary>
public sealed record ScenarioRun(DateTimeOffset StartedAt, string ServerVersion, IReadOnlyList<ScenarioMeasure> Scenarios);

/// <summary>
/// Counts what one scenario costs as it calls tools through the in-process MCP server (<see cref="InProcessMcpServer"/>): every
/// call, every failed call (isError), and the size of every result.
/// </summary>
public sealed class ScenarioRecorder(InProcessMcpServer server, ITestOutputHelper output)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public int Calls { get; private set; }

    public int FailedCalls { get; private set; }

    public long TextBytes { get; private set; }

    public int Images { get; private set; }

    /// <summary>Calls <paramref name="tool"/> and returns its JSON result; a failed call (isError) is counted and returned too.</summary>
    public async Task<JsonObject> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await server.CallToolAsync(tool, arguments, CallTimeout);
        Calls++;
        if (result.IsError == true)
        {
            FailedCalls++;
        }

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        TextBytes += Encoding.UTF8.GetByteCount(text);
        Images += result.Content.OfType<ImageContentBlock>().Count();
        output.WriteLine($"{tool} -> {(result.IsError == true ? "isError " : string.Empty)}{(text.Length > 300 ? text[..300] + "..." : text)}");
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }
}

/// <summary>
/// The scenarios of one test run (roadmap M7.6), shared by the scenario tests as a class fixture: each scenario is measured
/// with a <see cref="ScenarioRecorder"/>, compared with the same scenario of the previous run, and the run is written as a
/// JSON file when the tests end. The folder is WAM_SCENARIO_RESULTS, or WamScenarios in the temp folder; the previous run is
/// the newest file there.
/// </summary>
public sealed class ScenarioReport : IDisposable
{
    /// <summary>The environment variable that names the results folder (a CI artifact folder, for example).</summary>
    public const string ResultsVariable = "WAM_SCENARIO_RESULTS";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly List<ScenarioMeasure> _measures = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public ScenarioReport()
    {
        Folder = Environment.GetEnvironmentVariable(ResultsVariable) is { Length: > 0 } folder
            ? folder
            : Path.Combine(Path.GetTempPath(), "WamScenarios");
        Directory.CreateDirectory(Folder);
        Previous = LoadNewest(Folder);
    }

    /// <summary>Where the runs are written.</summary>
    public string Folder { get; }

    /// <summary>The previous run, or null when there is none.</summary>
    public ScenarioRun? Previous { get; }

    /// <summary>The file this run was written to, once it was.</summary>
    public string? WrittenTo { get; private set; }

    /// <summary>
    /// Runs scenario <paramref name="name"/>: <paramref name="scenario"/> calls tools through the recorder and returns whether
    /// it reached its goal. An exception counts as not reached. The measure is recorded and compared with the previous run's.
    /// </summary>
    public async Task<ScenarioMeasure> RunAsync(string name, InProcessMcpServer server, ITestOutputHelper output,
        Func<ScenarioRecorder, Task<bool>> scenario)
    {
        var recorder = new ScenarioRecorder(server, output);
        var watch = Stopwatch.StartNew();
        bool success;
        string? error = null;
        try
        {
            success = await scenario(recorder);
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
        }

        var measure = new ScenarioMeasure(name, success, recorder.Calls, recorder.FailedCalls, watch.ElapsedMilliseconds,
            recorder.TextBytes, recorder.TextBytes / 4, recorder.Images, error);
        lock (_lock)
        {
            _measures.Add(measure);
        }

        output.WriteLine(Describe(measure, Previous?.Scenarios.FirstOrDefault(s => s.Name == name)));
        return measure;
    }

    /// <summary>One line: the measure, and how it changed since the previous run.</summary>
    public static string Describe(ScenarioMeasure now, ScenarioMeasure? before)
    {
        static string Change(long current, long? previous) =>
            previous is { } p && p != current ? $" ({(current > p ? "+" : string.Empty)}{current - p})" : string.Empty;

        return $"Scenario {now.Name}: {(now.Success ? "reached its goal" : "FAILED" + (now.Error is { } e ? $" ({e})" : string.Empty))}, " +
               $"{now.Calls} calls{Change(now.Calls, before?.Calls)}, {now.FailedCalls} failed, {now.WallMs} ms{Change(now.WallMs, before?.WallMs)}, " +
               $"about {now.TextTokens} tokens of text{Change(now.TextTokens, before?.TextTokens)}, {now.Images} images" +
               (before == null ? " (no previous run)" : before.Success != now.Success ? $" (was {(before.Success ? "a success" : "a failure")})" : string.Empty);
    }

    /// <summary>Writes the run, when it measured anything.</summary>
    public void Dispose()
    {
        List<ScenarioMeasure> measures;
        lock (_lock)
        {
            measures = [.. _measures.OrderBy(m => m.Name, StringComparer.Ordinal)];
        }

        if (measures.Count == 0)
        {
            return;
        }

        var version = typeof(WindowsAppManagerMcp.Infrastructure.ServerIdentity).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        var run = new ScenarioRun(_startedAt, version, measures);
        WrittenTo = Path.Combine(Folder, $"scenarios-{_startedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(WrittenTo, JsonSerializer.Serialize(run, Json));
    }

    /// <summary>The newest run in <paramref name="folder"/>, or null when there is none or it cannot be read.</summary>
    public static ScenarioRun? LoadNewest(string folder)
    {
        var newest = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "scenarios-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (newest == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScenarioRun>(File.ReadAllText(newest), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
