using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: a question to the user that gets no answer is never a yes (roadmap M2.10). <see cref="UserConfirmation.AskAsync"/>
/// fails closed two ways that the other confirmation tests never reach: the user does not answer before the tool's answer
/// timeout (the deadline's cancellation), and the client fails to ask (it answers the question with an error). Every tool that
/// asks is called both ways, with a short answer timeout, and must refuse and do nothing: kill_process, close_window (force on a
/// protected window), close_windows_batch, close_application, close_debug_browser, launch_application (runAsAdmin), apply_layout
/// (an agent-written layout that would launch) and get_clipboard (the user's clipboard). Even the model's confirm=true changes
/// nothing. The services are mocks (ContractServices) and the clipboard is a fake; nothing is closed, killed, started or read.
/// </summary>
/// <remarks>
/// Each tool class is served as one instance built with the short answer timeout (the SDK would build one per call with the
/// default of two minutes), behind the error reporting and the input gate filter. On the initialize-handshake revisions the
/// question is a request from the server while the call is open, so the deadline cancels it; on the SDK client's default
/// revision the question is a round trip the server cannot take back, so the user's answer comes back after the deadline
/// (a late yes), which must not count either.
/// </remarks>
public sealed class UserConfirmationFailClosedContractTests : IDisposable
{
    /// <summary>The tools' answer timeout here: short, so a question nobody answers ends the call at once.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>When the user's yes comes in a late answer: well after <see cref="AnswerTimeout"/>.</summary>
    private static readonly TimeSpan LateAnswer = TimeSpan.FromMilliseconds(800);

    private static readonly long[] FourWindows =
        [TypicalDesktop.NotepadHandle, TypicalDesktop.BrowserHandle, 0x300000 + 2 * 0x1F4, 0x300000 + 4 * 0x1F4];

    private readonly ContractServices _services = new(ContractMode.Typical);
    private readonly FakeClipboardWrapper _clipboard = new();
    private int _questions;

    public UserConfirmationFailClosedContractTests()
    {
        _services.Processes.Setup(p => p.CheckKillTarget(TypicalDesktop.NotepadProcessId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(new KillTargetCheck(null, "notepad", [], new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc)));
        _services.Windows.Setup(w => w.FindWindows(It.Is<WindowQuery>(q => q.ProcessId == TypicalDesktop.NotepadProcessId)))
            .Returns([TypicalDesktop.Notepad]);
    }

    public void Dispose() => _services.Dispose();

    /// <summary>How the question fails.</summary>
    public enum Failure
    {
        /// <summary>The user answers yes, but only after the tool's answer timeout (or never, where the deadline cancels the question).</summary>
        NoAnswerInTime,

        /// <summary>The client fails to show the question: its handler throws, and the server gets an error for an answer.</summary>
        ClientFails
    }

    /// <summary>
    /// Each failure on each revision where the server sees it. A client that fails is seen on the initialize-handshake
    /// revisions, where the question is the server's own request and the client answers it with an error; on the default
    /// revision the failing handler ends the client's own call, so the server is never told (nothing runs there either).
    /// </summary>
    public static TheoryData<Failure, string?> Failures => new()
    {
        { Failure.NoAnswerInTime, null },
        { Failure.NoAnswerInTime, InProcessMcpServer.InitializeRevision },
        { Failure.NoAnswerInTime, InProcessMcpServer.NewestInitializeRevision },
        { Failure.ClientFails, InProcessMcpServer.InitializeRevision },
        { Failure.ClientFails, InProcessMcpServer.NewestInitializeRevision }
    };

    /// <summary>What the refusal says for <paramref name="failure"/> (UserConfirmation.AskAsync's reasons).</summary>
    private static string ReasonFor(Failure failure) => failure == Failure.NoAnswerInTime ? "did not answer" : "could not be asked";

    // ---- kill_process ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task KillProcess_TheQuestionFails_IsConfirmationDeclined_AndNothingIsKilled(Failure failure, string? revision)
    {
        // Arrange
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(LauncherTools()));

        // Act - even with the model's confirm=true
        var result = await server.CallToolAsync("kill_process", new Dictionary<string, object?>
        {
            ["processId"] = TypicalDesktop.NotepadProcessId, ["confirm"] = true, ["forceKill"] = true
        });

        // Assert
        AssertDeclined(result, nameof(ProcessErrorCode.ConfirmationDeclined), failure);
        _services.Processes.Verify(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(),
            It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_OnTheDefaultRevision_AClientThatFailsToAsk_EndsItsOwnCall_AndNothingIsKilled()
    {
        // Arrange - on the multi-round-trip revision the question goes back to the client with the call's result; the client's
        // failing handler ends its own call, and the server, which only acts on an answer that comes back, does nothing
        await using var server = await StartAsync(Failure.ClientFails, revision: null, builder => builder.WithServerTools(LauncherTools()));

        // Act
        var act = async () => await server.CallToolAsync("kill_process", new Dictionary<string, object?>
        {
            ["processId"] = TypicalDesktop.NotepadProcessId, ["confirm"] = true, ["forceKill"] = true
        });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The client could not show the question.");
        Volatile.Read(ref _questions).Should().Be(1);
        _services.Processes.Verify(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(),
            It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- close_window, close_windows_batch, close_application ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task CloseWindow_ForceOnAProtectedWindow_TheQuestionFails_IsConfirmationDeclined_AndNothingIsClosed(Failure failure, string? revision)
    {
        // Arrange - Notepad's window is the agent's host: closing it without force is refused, force asks the user
        _services.Windows.Setup(w => w.CloseWindow((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow,
                "Window 1 belongs to the agent's host. Nothing was sent. Pass force=true only if the user explicitly asked to close this window."));
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(WindowTools()));

        // Act
        var result = await server.CallToolAsync("close_window", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle, ["force"] = true, ["confirm"] = true, ["waitMs"] = 0
        });

        // Assert
        AssertDeclined(result, nameof(WindowErrorCode.ConfirmationDeclined), failure);
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task CloseWindowsBatch_OfMoreThanThreeWindows_TheQuestionFails_IsConfirmationDeclined_AndNothingIsClosed(Failure failure, string? revision)
    {
        // Arrange
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(WindowTools()));

        // Act
        var result = await server.CallToolAsync("close_windows_batch", new Dictionary<string, object?>
        {
            ["windows"] = FourWindows.Select(h => new { handle = h }).ToArray(), ["confirm"] = true, ["delayBetweenMs"] = 0
        });

        // Assert
        AssertDeclined(result, nameof(WindowErrorCode.ConfirmationDeclined), failure);
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task CloseApplication_OfMoreThanThreeWindows_TheQuestionFails_IsConfirmationDeclined_AndNothingIsClosed(Failure failure, string? revision)
    {
        // Arrange - four Notepad windows of one process, as close_application finds them in the Alt-Tab set
        var windows = TypicalDesktop.Windows.Take(4)
            .Select((w, i) => w with { Title = $"note{i}.txt - Notepad", ProcessId = TypicalDesktop.NotepadProcessId, ProcessName = "notepad" })
            .ToList();
        _services.Windows.Setup(w => w.GetApplicationWindows()).Returns(windows);
        foreach (var window in windows)
        {
            _services.Windows.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        }

        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(WindowTools()));

        // Act
        var result = await server.CallToolAsync("close_application", new Dictionary<string, object?>
        {
            ["processId"] = TypicalDesktop.NotepadProcessId, ["confirm"] = true
        });

        // Assert
        AssertDeclined(result, nameof(WindowErrorCode.ConfirmationDeclined), failure);
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    // ---- close_debug_browser ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task CloseDebugBrowser_TheQuestionFails_IsConfirmationDeclined_AndNothingIsClosed(Failure failure, string? revision)
    {
        // Arrange
        var tools = new BrowserReadingTools(_services.Browser.Object, _services.Processes.Object, _services.Windows.Object,
            _services.DebugBrowsers.Object, _services.Placement.Object, _services.Monitors.Object, _services.Session, AnswerTimeout);
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(tools));

        // Act
        var result = await server.CallToolAsync("close_debug_browser", new Dictionary<string, object?>
        {
            ["processId"] = ContractServices.TypicalDebugBrowser.ProcessId, ["confirm"] = true
        });

        // Assert
        AssertDeclined(result, nameof(ProcessErrorCode.ConfirmationDeclined), failure);
        _services.DebugBrowsers.Verify(d => d.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
            It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- launch_application runAsAdmin ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LaunchApplication_RunAsAdminWindowsWouldNotAskAbout_TheQuestionFails_StartsNothing(Failure failure, string? revision)
    {
        // Arrange - User Account Control is off, so the user is asked here before an elevated launch
        _services.Processes.Setup(p => p.PreviewElevation(It.IsAny<LaunchRequest>()))
            .Returns(new ElevationPreview(true, "User Account Control is off",
                new ElevationApproval(@"C:\Tools\setup-helper.exe", "--repair")));
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(LauncherTools()));

        // Act
        var result = await server.CallToolAsync("launch_application", new Dictionary<string, object?>
        {
            ["executable"] = "setup-helper", ["arguments"] = new[] { "--repair" }, ["runAsAdmin"] = true
        });

        // Assert
        AssertDeclined(result, nameof(ProcessErrorCode.ConfirmationDeclined), failure);
        _services.Processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- apply_layout of an agent-written layout that would launch ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ApplyLayout_WhoseLaunchesTheQuestionFails_LaunchesNothingOfIt(Failure failure, string? revision)
    {
        // Arrange - a dry run says a launch needs the user; the layout is arranged anyway, with its launches refused
        var layout = new LayoutPreset("agent-desk", null,
        [
            new WindowPlacement(new WindowMatcher(ProcessName: "notepad"), 0, new RelativePosition(0, 0, 0.5, 1))
            {
                Launch = new LayoutLaunch(Executable: @"C:\Windows\System32\notepad.exe")
            }
        ])
        {
            AuthoredBy = LayoutPresetSchema.AgentAuthor,
            UpdatedAt = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc)
        };
        var applies = new List<LayoutApplyOptions?>();
        _services.Layouts.Setup(l => l.GetPreset("agent-desk")).Returns(layout);
        _services.Layouts.Setup(l => l.AreLaunchesConfirmed(layout)).Returns(false);
        _services.Layouts.Setup(l => l.ApplyPresetAsync("agent-desk", It.IsAny<LayoutMatchBy>(), true, It.IsAny<LayoutApplyOptions?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, LayoutMatchBy _, bool _, LayoutApplyOptions? options, IProgress<OperationProgress>? _, CancellationToken _) =>
            {
                lock (applies)
                {
                    applies.Add(options);
                }

                return options?.DryRun == true
                    ? new LayoutApplyResult(false, name, DryRun: true, Placements:
                        [new LayoutPlacementResult(0, LayoutPlacementStatus.WouldLaunch, ErrorCode: nameof(LayoutErrorCode.LaunchNotConfirmed))])
                    : new LayoutApplyResult(true, name, WindowsArranged: 1);
            });
        var tools = new LayoutPresetTools(_services.Layouts.Object, _services.Session, AnswerTimeout);
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(tools));

        // Act
        await server.CallToolAsync("apply_layout", new Dictionary<string, object?> { ["name"] = "agent-desk", ["launchMissing"] = true });

        // Assert - the user's yes is never recorded, and every arrangement that is not a dry run refuses the launches
        Volatile.Read(ref _questions).Should().Be(1);
        _services.Layouts.Verify(l => l.ConfirmLaunches(It.IsAny<LayoutPreset>()), Times.Never);
        var arrangements = applies.Where(o => o?.DryRun != true).ToList();
        arrangements.Should().NotBeEmpty("the layout is still arranged, without its launches");
        arrangements.Should().OnlyContain(o => o != null && o.LaunchRefusal != null && o.LaunchRefusal.Contains(ReasonFor(failure)),
            "a launch the user did not confirm in time is refused");
    }

    // ---- get_clipboard of the user's clipboard ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task GetClipboard_OfTheUsersContent_TheQuestionFails_IsConfirmationDeclined_AndNothingIsRead(Failure failure, string? revision)
    {
        // Arrange - the user copied something; the agent's read needs their yes
        _clipboard.UserCopies("hunter2");
        var service = new ClipboardService(_clipboard, new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan),
            new ClipboardOptions(false), copyWait: TimeSpan.FromMilliseconds(100));
        await using var server = await StartAsync(failure, revision, builder => builder.WithServerTools(new ClipboardTools(service, AnswerTimeout)));

        // Act
        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        // Assert
        AssertDeclined(result, nameof(ClipboardErrorCode.ConfirmationDeclined), failure);
        Payload(result).ToString().Should().NotContain("hunter2");
        _clipboard.Reads.Should().Be(0, "nothing is read from the clipboard without the user's yes");
    }

    // ---- helpers ----

    private AppLauncherTools LauncherTools() =>
        new(_services.Processes.Object, _services.Windows.Object, _services.Session, AnswerTimeout,
            placementService: _services.Placement.Object, monitorService: _services.Monitors.Object, installedApps: _services.InstalledApps.Object);

    private WindowManagerTools WindowTools() =>
        new(_services.Windows.Object, _services.Foreground.Object, _services.Session, AnswerTimeout,
            closeVerifier: _services.CloseVerifier.Object, monitorService: _services.Monitors.Object);

    /// <summary>
    /// Starts a server with the tools <paramref name="tools"/> registers, behind the error reporting and the input gate filter,
    /// and a client whose user answers each question as <paramref name="failure"/> says.
    /// </summary>
    private Task<InProcessMcpServer> StartAsync(Failure failure, string? revision, Action<IMcpServerBuilder> tools)
    {
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (_, token) =>
                {
                    Interlocked.Increment(ref _questions);
                    if (failure == Failure.ClientFails)
                    {
                        throw new InvalidOperationException("The client could not show the question.");
                    }

                    // The user says yes, but too late: the server's deadline has passed (or cancelled the question)
                    await Task.Delay(LateAnswer, token);
                    return new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { [UserConfirmation.ConfirmField] = JsonSerializer.SerializeToElement(true) }
                    };
                }
            }
        };

        return InProcessMcpServer.StartAsync(
            [],
            builder =>
            {
                builder.AddToolErrorReporting().AddInputGateSerialization();
                tools(builder);
            },
            clientOptions: InProcessMcpServer.WithRevision(options, revision),
            configureServices: _services.Register);
    }

    /// <summary>The call was refused with <paramref name="errorCode"/> after one question, saying why and that nothing was done.</summary>
    private void AssertDeclined(CallToolResult result, string errorCode, Failure failure)
    {
        var payload = Payload(result);
        result.IsError.Should().BeTrue(payload.ToString());
        payload.GetProperty("errorCode").GetString().Should().Be(errorCode, payload.ToString());
        payload.GetProperty("error").GetString().Should().Contain(ReasonFor(failure)).And.Contain("Nothing");
        Volatile.Read(ref _questions).Should().Be(1, "the user was asked once");
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();
}
