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
    public void LaunchApplicationsBatch_MapsErrorCodePerItem()
    {
        // Arrange
        _mockProcessService
            .Setup(p => p.LaunchApplicationsBatch(
                It.IsAny<IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)>>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .Returns(new List<LaunchResult>
            {
                new(true, ProcessId: 100),
                new(false, ErrorMessage: "cmd.exe would interpret '&'", ErrorCode: nameof(LaunchErrorCode.UnsafeBatchArguments))
            });
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("notepad"),
            new ApplicationLaunchRequest("code", [@"C:\R&D\proj"])
        ]);

        // Assert
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Results[0].ErrorCode.Should().BeNull();
        result.Results[1].Executable.Should().Be("code");
        result.Results[1].ErrorCode.Should().Be("UnsafeBatchArguments");
        result.Results[1].Error.Should().Contain("cmd.exe");
    }
}
