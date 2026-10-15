using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CdpConnection (roadmap M5.2): id correlation, timeouts that keep the connection, events, dialogs, and
/// a connection that ends. The browser is an in-memory FakeCdpWebSocket that aborts on a cancelled token, as a
/// ClientWebSocket does; no network is involved.
/// </summary>
public class CdpConnectionTests
{
    private const string Label = "tab TAB1";
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // Generous bound so slow CI machines do not flake; a hang would never finish.
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private static CdpConnection Connect(FakeCdpWebSocket socket, int maxMessageBytes = MaxMessageBytes) =>
        new(socket, Label, maxMessageBytes);

    private static string Method(JsonElement command) => command.GetProperty("method").GetString()!;

    private static int Id(JsonElement command) => command.GetProperty("id").GetInt32();

    /// <summary>Answers every command with {"echo": method}.</summary>
    private static FakeCdpWebSocket Echoing() =>
        new((socket, command) => socket.Enqueue(JsonSerializer.Serialize(new
        {
            id = Id(command),
            result = new { echo = Method(command) }
        })));

    #region Ids and replies

    [Fact]
    public async Task SendAsync_NumbersCommandsFromOne_AndRoutesOutOfOrderReplies()
    {
        // Arrange - the first command is answered only after the second
        JsonElement? first = null;
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (Method(command) == "First")
            {
                first = command;
                return;
            }

            s.Enqueue("{\"id\":" + Id(command) + ",\"result\":{\"which\":\"second\"}}");
            s.Enqueue("{\"id\":" + Id(first!.Value) + ",\"result\":{\"which\":\"first\"}}");
        });
        await using var connection = Connect(socket);

        // Act
        var firstTask = connection.SendAsync("First", null, LongTimeout, CancellationToken.None);
        var second = await connection.SendAsync("Second", new { x = 1 }, LongTimeout, CancellationToken.None);
        var firstReply = await firstTask;

        // Assert
        socket.SentCommands.Select(Id).Should().Equal(1, 2);
        socket.SentCommands[1].GetProperty("params").GetProperty("x").GetInt32().Should().Be(1);
        socket.SentCommands[0].TryGetProperty("params", out _).Should().BeFalse("a command without params sends none");
        second.Value!.Result.GetProperty("which").GetString().Should().Be("second");
        firstReply.Value!.Result.GetProperty("which").GetString().Should().Be("first");
    }

    [Fact]
    public async Task SendAsync_TwentyConcurrentCommands_AreSentWholeAndAllAnswered()
    {
        // Arrange
        var socket = Echoing();
        await using var connection = Connect(socket);

        // Act
        var replies = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => connection.SendAsync($"M{i}", new { i }, LongTimeout, CancellationToken.None))));

        // Assert - every sent message parsed as one command, and each reply reached its own sender
        socket.SentCommands.Should().HaveCount(20);
        socket.SentCommands.Select(Id).Should().OnlyHaveUniqueItems();
        replies.Select((r, i) => r.Value!.Result.GetProperty("echo").GetString() == $"M{i}").Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public async Task SendAsync_ReturnsAnErrorReplyAsASuccessCarryingTheFault()
    {
        // Arrange
        var socket = new FakeCdpWebSocket((s, command) => s.ReplyError(command, -32601, "'Foo.bar' wasn't found"));
        await using var connection = Connect(socket);

        // Act
        var result = await connection.SendAsync("Foo.bar", null, LongTimeout, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Value!.Error.Should().Be(new CdpProtocolFault(-32601, "'Foo.bar' wasn't found"));
    }

    [Fact]
    public async Task SendAsync_TimedOutCommand_ReturnsCdpTimeout_DropsItsLateReply_AndKeepsTheConnection()
    {
        // Arrange - "Slow" is never answered while it waits; its reply comes in with the next command's
        var slowId = 0;
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (Method(command) == "Slow")
            {
                slowId = Id(command);
                return;
            }

            s.Enqueue("{\"id\":" + slowId + ",\"result\":{\"late\":true}}");
            s.ReplyEmpty(command);
        });
        await using var connection = Connect(socket);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var slow = await connection.SendAsync("Slow", null, ShortTimeout, CancellationToken.None);
        var next = await connection.SendAsync("Next", null, LongTimeout, CancellationToken.None);

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        slow.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        slow.Error.Should().Contain("Slow").And.Contain("200 ms");
        socket.AbortedByCancellation.Should().BeFalse("a command's timeout must never reach the socket");
        socket.WasAborted.Should().BeFalse();
        next.Success.Should().BeTrue();
        next.Value!.Result.TryGetProperty("late", out _).Should().BeFalse("the late reply belongs to the forgotten command");
        connection.Closed.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_Throws_AndLeavesTheConnectionOpen()
    {
        // Arrange
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (Method(command) != "Hang")
                s.ReplyEmpty(command);
        });
        await using var connection = Connect(socket);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        var hang = () => connection.SendAsync("Hang", null, LongTimeout, cts.Token);
        await hang.Should().ThrowAsync<OperationCanceledException>();
        var next = await connection.SendAsync("Next", null, LongTimeout, CancellationToken.None);

        // Assert
        socket.AbortedByCancellation.Should().BeFalse();
        next.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ThatStalls_AbortsTheConnection_AndReturnsCdpTimeout()
    {
        // Arrange - the socket takes no more bytes, so half a message may be on it
        var socket = new FakeCdpWebSocket { StallSends = true };
        await using var connection = Connect(socket);

        // Act
        var stalled = await connection.SendAsync("Page.enable", null, ShortTimeout, CancellationToken.None);

        // Assert
        stalled.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        connection.Closed!.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        connection.Closed.Error.Should().Contain("aborted after a send timeout");
        socket.WasAborted.Should().BeTrue();
        socket.AbortedByCancellation.Should().BeFalse();
        var later = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);
        later.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
    }

    #endregion

    #region Events

    [Fact]
    public async Task ExpectEvent_RegisteredBeforeTheSend_CatchesAnEventTheBrowserSendsAtOnce()
    {
        // Arrange - the event arrives before the command's reply
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            s.EnqueueEvent("Page.loadEventFired", new { timestamp = 1.5 });
            s.ReplyEmpty(command);
        });
        await using var connection = Connect(socket);

        // Act
        using var loaded = connection.ExpectEvent("Page.loadEventFired");
        await connection.SendAsync("Page.navigate", new { url = "about:blank" }, LongTimeout, CancellationToken.None);
        var result = await loaded.WaitAsync(LongTimeout, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Value.GetProperty("timestamp").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task ExpectEvent_HonoursItsFilter_AndDropsEventsNobodyWaitsFor()
    {
        // Arrange
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            s.EnqueueEvent("Runtime.consoleAPICalled", new { type = "log" });
            s.EnqueueEvent("Page.frameNavigated", new { frame = new { id = "child" } });
            s.EnqueueEvent("Page.frameNavigated", new { frame = new { id = "main" } });
            s.ReplyEmpty(command);
        });
        await using var connection = Connect(socket);

        // Act
        using var mainFrame = connection.ExpectEvent(
            "Page.frameNavigated", p => p.GetProperty("frame").GetProperty("id").GetString() == "main");
        await connection.SendAsync("Page.reload", null, LongTimeout, CancellationToken.None);
        var result = await mainFrame.WaitAsync(LongTimeout, CancellationToken.None);

        // Assert
        result.Value.GetProperty("frame").GetProperty("id").GetString().Should().Be("main");
    }

    [Fact]
    public async Task ExpectEvent_ThatNeverComes_ReturnsCdpTimeout()
    {
        await using var connection = Connect(new FakeCdpWebSocket());
        using var waiter = connection.ExpectEvent("Page.loadEventFired");

        var result = await waiter.WaitAsync(ShortTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("Page.loadEventFired");
    }

    #endregion

    #region Dialogs

    [Fact]
    public async Task Dialog_FailsTheWaitingCommand_AndLaterOnesWithoutSendingThem_UntilItCloses()
    {
        // Arrange - the page opens a confirm() while Runtime.evaluate runs, and never answers it
        var longText = new string('x', 300);
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            switch (Method(command))
            {
                case "Runtime.evaluate":
                    s.EnqueueEvent("Page.javascriptDialogOpening",
                        new { url = "https://shop.example/", message = longText, type = "confirm", hasBrowserHandler = true });
                    break;
                default:
                    s.ReplyEmpty(command);
                    break;
            }
        });
        await using var connection = Connect(socket);

        // Act
        var evaluate = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);
        var sentBefore = socket.SentCommands.Count;
        var plain = await connection.SendAsync("DOM.getDocument", null, LongTimeout, CancellationToken.None);
        var sentAfterPlain = socket.SentCommands.Count;
        var allowed = await connection.SendAsync(
            "Page.handleJavaScriptDialog", new { accept = false }, LongTimeout, CancellationToken.None, CdpSendOptions.AllowWhileDialogOpen);

        // Assert
        evaluate.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        evaluate.Error.Should().Contain("JavaScript confirm dialog").And.Contain("Do not press Enter")
            .And.Contain(new string('x', 200) + "...").And.NotContain(new string('x', 201));
        connection.OpenDialog.Should().Be(new CdpDialog("confirm", longText, null, "https://shop.example/"));
        plain.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        sentAfterPlain.Should().Be(sentBefore, "nothing is sent to a page blocked by a dialog");
        allowed.Success.Should().BeTrue();

        // Act - the dialog closes
        socket.EnqueueEvent("Page.javascriptDialogClosed", new { result = false, userInput = "" });
        var deadline = Stopwatch.StartNew();
        while (connection.OpenDialog != null && deadline.Elapsed < HangBound)
            await Task.Delay(10);
        var afterClose = await connection.SendAsync("DOM.getDocument", null, LongTimeout, CancellationToken.None);

        // Assert
        connection.OpenDialog.Should().BeNull();
        afterClose.Success.Should().BeTrue();
    }

    #endregion

    #region The connection ends

    public static TheoryData<string, Action<FakeCdpWebSocket>, string> Endings() => new()
    {
        { "close frame", s => s.EnqueueClose(), "closed the DevTools connection" },
        { "detached", s => s.EnqueueEvent("Inspector.detached", new { reason = "Render process gone." }), "Render process gone." },
        { "crashed", s => s.EnqueueEvent("Inspector.targetCrashed"), "crashed" },
        { "reset", s => s.FailNextReceive(new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The remote party closed the WebSocket connection without completing the close handshake.")), "without completing" },
    };

    [Theory]
    [MemberData(nameof(Endings))]
    public async Task TheConnectionEnding_FailsTheWaitingCommand_AndLaterOnes_WithCdpTargetClosed(
        string ending, Action<FakeCdpWebSocket> end, string reason)
    {
        // Arrange
        _ = ending;
        var socket = new FakeCdpWebSocket((s, command) => end(s));
        await using var connection = Connect(socket);

        // Act
        var waiting = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);
        var later = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);

        // Assert
        waiting.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        waiting.Error.Should().Contain(reason).And.Contain("without targetId");
        later.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        socket.SentCommands.Should().ContainSingle("nothing is sent on a connection that ended");
        connection.Closed!.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
    }

    [Fact]
    public async Task AMessageOverTheLimit_EndsTheConnection_WithCdpResponseTooLarge_ReadingAtMostOneChunkMore()
    {
        // Arrange - a 1 MB message against a 100 KB limit, in 16 KB frames
        const int limit = 100 * 1024;
        var big = Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"v\":\"" + new string('a', 1024 * 1024) + "\"}}");
        var socket = new FakeCdpWebSocket((s, _) => s.EnqueueRaw(big, frameSize: 16 * 1024));
        await using var connection = Connect(socket, limit);

        // Act
        var result = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        result.Error.Should().Contain("102400 bytes").And.Contain("maxLength");
    }

    [Fact]
    public async Task AMessageThatIsNotJson_EndsTheConnection_WithCdpProtocolError()
    {
        var socket = new FakeCdpWebSocket((s, _) => s.Enqueue("this is not json"));
        await using var connection = Connect(socket);

        var result = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("not JSON");
    }

    [Fact]
    public async Task AMessageSplitAcrossFrames_IsReassembledBeforeItIsDecoded()
    {
        // 5-byte frames split multi-byte UTF-8 characters between frames.
        const string text = "Blåbærsyltetøy – 日本語 😀 done";
        var socket = new FakeCdpWebSocket((s, command) =>
            s.Enqueue(JsonSerializer.Serialize(new { id = Id(command), result = new { text } }), frameSize: 5));
        await using var connection = Connect(socket);

        var result = await connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);

        result.Value!.Result.GetProperty("text").GetString().Should().Be(text);
    }

    [Fact]
    public async Task DisposeAsync_CancelsTheReceive_AbortsAndDisposesTheSocket_AndFailsWhatIsLeft()
    {
        // Arrange - a page that never answers
        var socket = new FakeCdpWebSocket();
        var connection = Connect(socket);
        var waiting = connection.SendAsync("Runtime.evaluate", null, LongTimeout, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        // Act
        await connection.DisposeAsync();
        var result = await waiting.WaitAsync(HangBound);

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        socket.ReceiveTokenWasCancelled.Should().BeTrue("the reader's receive ends with the connection");
        socket.WasAborted.Should().BeTrue();
        socket.WasDisposed.Should().BeTrue();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("disposed");
    }

    #endregion
}
