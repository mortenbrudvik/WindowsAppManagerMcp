using System.Diagnostics;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Reads a tab's page where the page's own scripts cannot reach: an isolated world of its main frame
/// (Page.createIsolatedWorld), then Runtime.evaluate in that world's execution context.
/// </summary>
/// <remarks>
/// An isolated world shares the page's DOM but has JavaScript wrappers of its own: a page that redefines
/// document.visibilityState, window.innerWidth or devicePixelRatio in its main world (Object.defineProperty on document,
/// window or Document.prototype) changes nothing an isolated world reads. The checks a page must not be able to talk its
/// way through read here: which of several tabs a window shows (<see cref="CdpShownTab"/>), and whether a page is shown
/// and where its viewport is (the DOM bridge's page read). browser_evaluate runs the agent's own script in the main world,
/// where it means to be. Chromium keeps one world per name and frame, for as long as the DevTools session lasts.
/// </remarks>
internal static class CdpIsolatedWorld
{
    /// <summary>The isolated world's name, which only DevTools sees.</summary>
    public const string WorldName = "wam-isolated-read";

    /// <summary>
    /// Evaluates <paramref name="expression"/> by value in an isolated world of the tab's main frame, all three commands
    /// within <paramref name="timeout"/>. The reply is the Runtime.evaluate reply, or the failure of the command that did
    /// not work (<see cref="CdpIsolatedEvaluation.Method"/>): a refused Page.getFrameTree or Page.createIsolatedWorld, or
    /// one without the frame or the context, is CdpProtocolError, so a failure never falls back to the main world.
    /// </summary>
    public static async Task<CdpIsolatedEvaluation> EvaluateAsync(
        ICdpConnection connection, string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var stopwatch = Stopwatch.StartNew();

        const string frameTree = "Page.getFrameTree";
        var tree = await connection.SendAsync(frameTree, null, timeout, cancellationToken).ConfigureAwait(false);
        if (!tree.Success)
            return new CdpIsolatedEvaluation(frameTree, tree);

        if (tree.Value.Error is { } treeFault)
            return new CdpIsolatedEvaluation(frameTree, CdpReplies.ToFailure<CdpReply>(frameTree, treeFault));

        if (MainFrameId(tree.Value.Result) is not { } frameId)
        {
            return new CdpIsolatedEvaluation(frameTree, CdpResult<CdpReply>.Fail(BrowserErrorCode.CdpProtocolError,
                "The tab answered Page.getFrameTree without its main frame, so its page could not be read where its scripts " +
                "cannot change it. Nothing was done."));
        }

        const string createWorld = "Page.createIsolatedWorld";
        var world = await connection.SendAsync(createWorld, new { frameId, worldName = WorldName }, Left(timeout, stopwatch), cancellationToken)
            .ConfigureAwait(false);
        if (!world.Success)
            return new CdpIsolatedEvaluation(createWorld, world);

        if (world.Value.Error is { } worldFault)
            return new CdpIsolatedEvaluation(createWorld, CdpReplies.ToFailure<CdpReply>(createWorld, worldFault));

        if (!TryGetContextId(world.Value.Result, out var contextId))
        {
            return new CdpIsolatedEvaluation(createWorld, CdpResult<CdpReply>.Fail(BrowserErrorCode.CdpProtocolError,
                "The tab answered Page.createIsolatedWorld without an execution context, so its page could not be read where " +
                "its scripts cannot change it. Nothing was done."));
        }

        const string evaluate = "Runtime.evaluate";
        var evaluated = await connection.SendAsync(evaluate, new { expression, contextId, returnByValue = true }, Left(timeout, stopwatch),
            cancellationToken).ConfigureAwait(false);
        return new CdpIsolatedEvaluation(evaluate, evaluated);
    }

    /// <summary>Page.getFrameTree's main frame id, or null.</summary>
    private static string? MainFrameId(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("frameTree", out var tree) && tree.ValueKind == JsonValueKind.Object &&
        tree.TryGetProperty("frame", out var frame) && CdpReplies.GetString(frame, "id") is { Length: > 0 } id
            ? id
            : null;

    private static bool TryGetContextId(JsonElement result, out int contextId)
    {
        contextId = 0;
        return result.ValueKind == JsonValueKind.Object &&
               result.TryGetProperty("executionContextId", out var id) &&
               id.ValueKind == JsonValueKind.Number &&
               id.TryGetInt32(out contextId);
    }

    private static TimeSpan Left(TimeSpan timeout, Stopwatch stopwatch)
    {
        var left = timeout - stopwatch.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }
}

/// <summary>
/// What <see cref="CdpIsolatedWorld.EvaluateAsync"/> got: the Runtime.evaluate reply, or the failure of
/// <paramref name="Method"/>, the command that did not work.
/// </summary>
internal readonly record struct CdpIsolatedEvaluation(string Method, CdpResult<CdpReply> Reply);
