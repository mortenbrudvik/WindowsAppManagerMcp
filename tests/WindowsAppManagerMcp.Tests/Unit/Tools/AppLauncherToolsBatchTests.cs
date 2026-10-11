using ModelContextProtocol;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for launch_applications_batch result mapping (PAL-15: batch items had no error code).
/// </summary>
public class AppLauncherToolsBatchTests
{
    private readonly Mock<IProcessService> _mockProcessService = new();

    private AppLauncherTools CreateSut() => new(_mockProcessService.Object);

    [Fact]
    public async Task LaunchApplicationsBatch_MapsErrorCodePerItem()
    {
        // Arrange
        _mockProcessService
            .Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<LaunchRequest>>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LaunchResult>
            {
                new(true, ProcessId: 100, Status: LaunchStatus.Started),
                new(false, ErrorMessage: "cmd.exe would interpret '&'", ErrorCode: nameof(LaunchErrorCode.UnsafeBatchArguments))
            });
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("notepad"),
            new ApplicationLaunchRequest("code", [@"C:\R&D\proj"])
        ]);

        // Assert
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Results[0].ErrorCode.Should().BeNull();
        result.Results[0].LaunchStatus.Should().Be("started");
        result.Results[1].LaunchStatus.Should().BeNull();
        result.Results[1].Executable.Should().Be("code");
        result.Results[1].ErrorCode.Should().Be("UnsafeBatchArguments");
        result.Results[1].Error.Should().Contain("cmd.exe");
    }

    [Fact]
    public async Task LaunchApplicationsBatch_PassesTheCallsTokenAndForwardsProgressToTheClient()
    {
        // Arrange - the service reports one launch
        using var call = new CancellationTokenSource();
        CancellationToken serviceToken = default;
        _mockProcessService
            .Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<LaunchRequest>>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<LaunchRequest> _, bool _, int _, IProgress<OperationProgress>? progress, CancellationToken token) =>
            {
                serviceToken = token;
                progress!.Report(new OperationProgress(1, 1, "1 of 1: notepad started"));
            })
            .ReturnsAsync(new List<LaunchResult> { new(true, ProcessId: 100) });
        var client = new RecordingProgress<ProgressNotificationValue>();

        // Act
        await CreateSut().LaunchApplicationsBatch([new ApplicationLaunchRequest("notepad")], progress: client, cancellationToken: call.Token);

        // Assert
        serviceToken.Should().Be(call.Token);
        var report = client.Reports.Should().ContainSingle().Subject;
        (report.Progress, report.Total, report.Message).Should().Be((1f, 1f, "1 of 1: notepad started"));
    }
}
