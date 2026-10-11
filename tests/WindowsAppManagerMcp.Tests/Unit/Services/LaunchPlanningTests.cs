namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ProcessService.PlanLaunch: validation, target resolution, batch-file argument
/// rules (BatBadBut, pro-m3), binary planting (pro-m2) and argument quoting (PAL-7/PAL-8).
/// PlanLaunch only builds a ProcessStartInfo; these tests never start a process. The process
/// wrapper is a strict mock with no setups, so a test that did start or kill one would throw.
/// </summary>
public class LaunchPlanningTests
{
    private const string EdgeX86 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string CodeCmd = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd";

    private readonly Mock<IExecutableResolver> _mockResolver = new();
    private readonly Mock<IBrowserDetectionService> _mockBrowserDetection = TestDataFactory.CreateMockBrowserDetectionService();

    private ProcessService CreateSut() => new(
        TestDataFactory.CreateMockWindowService().Object,
        new InputValidationService(ProtocolAllowlist.BuiltIn),
        _mockBrowserDetection.Object,
        _mockResolver.Object,
        new Mock<INativeProcessWrapper>(MockBehavior.Strict).Object);

    private void ResolveAs(string fileName, LaunchTargetKind kind, string? plantedPath = null)
    {
        _mockResolver
            .Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new ResolvedLaunchTarget(fileName, kind, plantedPath));
    }

    #region Legitimate Paths

    /// <summary>
    /// Regression (PAL-2 / BRW-2): the Edge debug launch path was rejected as command injection.
    /// </summary>
    [Fact]
    public void PlanLaunch_EdgeX86PathWithDebugArguments_ProducesStartInfo()
    {
        // Arrange
        ResolveAs(EdgeX86, LaunchTargetKind.NativeExecutable);
        var arguments = ProcessService.BuildDebugBrowserArguments(9222, @"C:\Users\Jane Doe\AppData\Local\Temp\browser-debug-1", url: null);

        // Act
        var plan = CreateSut().PlanLaunch(EdgeX86, arguments.ToArray());

        // Assert
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be(EdgeX86);
        plan.StartInfo.UseShellExecute.Should().BeTrue();
        plan.StartInfo.Arguments.Should().Be(
            "--remote-debugging-port=9222 \"--user-data-dir=C:\\Users\\Jane Doe\\AppData\\Local\\Temp\\browser-debug-1\"");
        _mockResolver.Verify(r => r.Resolve(EdgeX86, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void PlanLaunch_BareName_StartsResolvedAbsolutePath()
    {
        // Arrange
        ResolveAs(@"C:\Windows\System32\notepad.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("notepad");

        // Assert
        plan.IsValid.Should().BeTrue();
        plan.StartInfo!.FileName.Should().Be(@"C:\Windows\System32\notepad.exe");
        plan.TargetKind.Should().Be(LaunchTargetKind.NativeExecutable);
    }

    [Fact]
    public void PlanLaunch_ValidationFailure_DoesNotResolve()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var plan = sut.PlanLaunch(@"..\..\evil.exe");

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.PathTraversal));
        _mockResolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    #endregion

    #region Batch Files (cmd.exe re-parses arguments)

    [Theory]
    [InlineData(@"C:\R&D\proj")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("%USERPROFILE%")]
    [InlineData("!PATH!")]
    [InlineData("say \"hi\"")]
    [InlineData("\" & calc & \"")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    public void PlanLaunch_BatchFileWithCmdMetacharacters_IsRejected(string argument)
    {
        // Arrange - 'code' resolves to VS Code's bin\code.cmd
        ResolveAs(CodeCmd, LaunchTargetKind.BatchFile);

        // Act
        var plan = CreateSut().PlanLaunch("code", ["--new-window", argument]);

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.StartInfo.Should().BeNull();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeBatchArguments));
        plan.ErrorMessage.Should().Contain("batch file").And.Contain(CodeCmd).And.Contain("cmd.exe");
    }

    [Fact]
    public void PlanLaunch_BatchFileWithSafeArguments_QuotesForCmd()
    {
        // Arrange
        ResolveAs(CodeCmd, LaunchTargetKind.BatchFile);

        // Act
        var plan = CreateSut().PlanLaunch(
            "code",
            [@"C:\Program Files (x86)\proj", "--new-window", "a;b", @"C:\Users\O'Brien\notes.md", "plain"]);

        // Assert
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be(CodeCmd);
        plan.StartInfo.Arguments.Should().Be(
            "\"C:\\Program Files (x86)\\proj\" --new-window \"a;b\" C:\\Users\\O'Brien\\notes.md plain");
    }

    [Fact]
    public void PlanLaunch_BatchFileWithoutArguments_IsAllowed()
    {
        // Arrange
        ResolveAs(CodeCmd, LaunchTargetKind.BatchFile);

        // Act
        var plan = CreateSut().PlanLaunch("code");

        // Assert
        plan.IsValid.Should().BeTrue();
        plan.StartInfo!.Arguments.Should().BeEmpty();
        plan.TargetKind.Should().Be(LaunchTargetKind.BatchFile);
    }

    [Fact]
    public void PlanLaunch_UnverifiedTargetWithCmdMetacharacters_IsRejected()
    {
        // Arrange - a name that could not be resolved may still end up in cmd.exe
        ResolveAs("someapp", LaunchTargetKind.Other);

        // Act
        var plan = CreateSut().PlanLaunch("someapp", ["a&b"]);

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeBatchArguments));
        plan.ErrorMessage.Should().Contain("could not be verified");
    }

    [Fact]
    public void PlanLaunch_UnverifiedTargetWithoutArguments_IsAllowed()
    {
        // Arrange
        ResolveAs("https://example.com/?a=1&b=2", LaunchTargetKind.Other);

        // Act
        var plan = CreateSut().PlanLaunch("https://example.com/?a=1&b=2");

        // Assert
        plan.IsValid.Should().BeTrue();
        plan.StartInfo!.FileName.Should().Be("https://example.com/?a=1&b=2");
    }

    [Fact]
    public void PlanLaunch_LongUnsafeArgument_IsTruncatedInMessage()
    {
        // Arrange
        ResolveAs(CodeCmd, LaunchTargetKind.BatchFile);
        var longArgument = "&" + new string('x', 500);

        // Act
        var plan = CreateSut().PlanLaunch("code", [longArgument]);

        // Assert
        plan.ErrorMessage!.Length.Should().BeLessThan(600);
        plan.ErrorMessage.Should().Contain("...");
    }

    #endregion

    #region Native Executables

    [Fact]
    public void PlanLaunch_NativeExecutableWithMetacharacters_PassesThemAsData()
    {
        // Arrange - a native .exe parses its own command line; & and % are plain characters there
        ResolveAs(@"C:\Program Files\App\app.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch(@"C:\Program Files\App\app.exe", [@"C:\R&D\proj", "%PATH%", "x=1"]);

        // Assert
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.Arguments.Should().Be(@"C:\R&D\proj %PATH% x=1");
    }

    [Fact]
    public void PlanLaunch_TrailingBackslashAndEmptyArgument_AreQuotedCorrectly()
    {
        // Arrange
        ResolveAs(@"C:\Windows\explorer.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("explorer", [@"C:\My Folder\", ""]);

        // Assert
        plan.StartInfo!.Arguments.Should().Be("\"C:\\My Folder\\\\\" \"\"");
    }

    #endregion

    #region Invalid Arguments and Planting

    [Fact]
    public void PlanLaunch_NullArgument_ReturnsInvalidArgumentValue()
    {
        // Arrange
        ResolveAs(@"C:\Windows\notepad.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("notepad", ["ok", null!]);

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidArgumentValue));
    }

    [Fact]
    public void PlanLaunch_ArgumentWithNulCharacter_ReturnsInvalidArgumentValue()
    {
        // Arrange
        ResolveAs(@"C:\Windows\notepad.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("notepad", ["file\0.txt"]);

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidArgumentValue));
    }

    [Fact]
    public void PlanLaunch_ProgramFoundOnlyInWorkingDirectory_IsRejected()
    {
        // Arrange
        ResolveAs("build", LaunchTargetKind.Other, plantedPath: @"C:\repo\build.exe");

        // Act
        var plan = CreateSut().PlanLaunch("build", workingDirectory: @"C:\repo");

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableInWorkingDirectory));
        plan.ErrorMessage.Should().Contain(@"C:\repo\build.exe").And.Contain("full path");
    }

    [Fact]
    public void PlanLaunch_BareNameFoundNowhere_IsRefused_InsteadOfLettingWindowsSearchTheCurrentDirectory()
    {
        // Arrange
        _mockResolver
            .Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new ResolvedLaunchTarget("not-installed-tool", LaunchTargetKind.Other, NotFound: true));

        // Act
        var plan = CreateSut().PlanLaunch("not-installed-tool");

        // Assert
        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableNotFound));
        plan.ErrorMessage.Should().Contain("not-installed-tool").And.Contain("full path");
    }

    [Fact]
    public void PlanLaunch_WithoutWorkingDirectory_StartsInTheProgramsFolder_NotTheServersCurrentDirectory()
    {
        // Arrange
        ResolveAs(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("code");

        // Assert
        plan.StartInfo!.WorkingDirectory.Should().Be(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code");
        _mockResolver.Verify(r => r.Resolve("code", null), Times.Once);
    }

    [Fact]
    public void PlanLaunch_ADocumentWithoutWorkingDirectory_StartsInItsFolder()
    {
        // Arrange
        ResolveAs(@"D:\Reports\q3.pdf", LaunchTargetKind.Other);

        // Act
        var plan = CreateSut().PlanLaunch(@"D:\Reports\q3.pdf");

        // Assert
        plan.StartInfo!.WorkingDirectory.Should().Be(@"D:\Reports");
    }

    [Fact]
    public void PlanLaunch_AUriWithoutWorkingDirectory_StartsInTheUsersProfile()
    {
        // Arrange
        ResolveAs("ms-settings:display", LaunchTargetKind.Other);

        // Act
        var plan = CreateSut().PlanLaunch("ms-settings:display");

        // Assert
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be("ms-settings:display");
        plan.StartInfo.WorkingDirectory.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public void PlanLaunch_KeepsTheResolvedTarget_ForTheWindowWait()
    {
        // Arrange
        ResolveAs(@"C:\Windows\System32\calc.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("calc");

        // Assert
        plan.Target.Should().Be(new ResolvedLaunchTarget(@"C:\Windows\System32\calc.exe", LaunchTargetKind.NativeExecutable));
    }

    /// <summary>
    /// Binary planting regression on the real file system (roadmap M2.7): a notepad.exe planted in the working
    /// directory, which is also the server's current directory, is never what gets started, and a program found only
    /// there is refused. The files are empty and live in a temporary folder; nothing is started.
    /// </summary>
    [Fact]
    public void PlanLaunch_RealFileSystem_ProgramsPlantedInTheWorkingDirectory_AreNeverStarted()
    {
        // Arrange
        var plantDir = Path.Combine(Path.GetTempPath(), $"LaunchPlanningTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(plantDir);
        var uniqueName = $"zz-planted-tool-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(Path.Combine(plantDir, "notepad.exe"), []);
            File.WriteAllBytes(Path.Combine(plantDir, uniqueName + ".exe"), []);
            var resolver = new ExecutableResolver(
                File.Exists,
                () => Environment.GetEnvironmentVariable("PATH"),
                ExecutableResolver.ReadAppPathsFromRegistry,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.SystemDirectory,
                () => plantDir,
                () => Environment.GetEnvironmentVariable("PATHEXT"));
            var sut = new ProcessService(
                TestDataFactory.CreateMockWindowService().Object,
                new InputValidationService(ProtocolAllowlist.BuiltIn),
                _mockBrowserDetection.Object,
                resolver,
                new Mock<INativeProcessWrapper>(MockBehavior.Strict).Object);

            // Act
            var inWorkingDirectory = sut.PlanLaunch("notepad", workingDirectory: plantDir);
            var withoutWorkingDirectory = sut.PlanLaunch("notepad");
            var plantedOnly = sut.PlanLaunch(uniqueName, workingDirectory: plantDir);

            // Assert - the installed Notepad, by its absolute path
            inWorkingDirectory.IsValid.Should().BeTrue(inWorkingDirectory.ErrorMessage);
            inWorkingDirectory.StartInfo!.FileName.Should().NotStartWithEquivalentOf(plantDir).And.EndWithEquivalentOf(@"\notepad.exe");
            Path.IsPathFullyQualified(inWorkingDirectory.StartInfo.FileName).Should().BeTrue();
            inWorkingDirectory.StartInfo.WorkingDirectory.Should().Be(plantDir, "a working directory that was asked for is kept");

            // Without a working directory, the program starts in its own folder, not in the current directory
            withoutWorkingDirectory.StartInfo!.WorkingDirectory.Should()
                .Be(Path.GetDirectoryName(withoutWorkingDirectory.StartInfo.FileName)).And.NotBe(plantDir);

            plantedOnly.IsValid.Should().BeFalse();
            plantedOnly.ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableInWorkingDirectory));
        }
        finally
        {
            Directory.Delete(plantDir, recursive: true);
        }
    }

    [Fact]
    public void PlanLaunch_PassesWorkingDirectoryToResolver()
    {
        // Arrange
        ResolveAs(@"C:\Windows\notepad.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("notepad", workingDirectory: @"C:\Program Files (x86)\App");

        // Assert
        plan.StartInfo!.WorkingDirectory.Should().Be(@"C:\Program Files (x86)\App");
        _mockResolver.Verify(r => r.Resolve("notepad", @"C:\Program Files (x86)\App"), Times.Once);
    }

    #endregion

    #region Browser --new-window Injection

    [Fact]
    public void PlanLaunch_BrowserWithUrl_StillInjectsNewWindowFlag()
    {
        // Arrange
        _mockBrowserDetection.Setup(b => b.IsBrowser("chrome")).Returns(true);
        _mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]?>())).Returns(true);
        _mockBrowserDetection.Setup(b => b.GetNewWindowFlag("chrome")).Returns("--new-window");
        ResolveAs(@"C:\Program Files\Google\Chrome\Application\chrome.exe", LaunchTargetKind.NativeExecutable);

        // Act
        var plan = CreateSut().PlanLaunch("chrome", ["https://example.com/?a=1&b=2"]);

        // Assert
        plan.StartInfo!.Arguments.Should().Be("--new-window https://example.com/?a=1&b=2");
    }

    #endregion
}
