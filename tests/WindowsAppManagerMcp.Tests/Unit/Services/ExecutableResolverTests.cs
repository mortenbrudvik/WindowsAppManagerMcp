namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ExecutableResolver (pro-m2 binary planting, pro-m3 batch-file detection).
/// Uses a fake file system, PATH and App Paths registry. Nothing is launched.
/// </summary>
public class ExecutableResolverTests
{
    private const string WindowsDir = @"C:\Windows";
    private const string SystemDir = @"C:\Windows\System32";
    private const string CurrentDir = @"C:\repo";
    private const string VsCodeBin = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _probedPaths = [];
    private readonly Dictionary<string, List<string>> _appPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _pathVariable = $@"{SystemDir};{WindowsDir};C:\Tools;{VsCodeBin}";
    private string? _pathExtVariable;

    private ExecutableResolver CreateSut() => new(
        fileExists: path =>
        {
            _probedPaths.Add(path);
            return _files.Contains(path);
        },
        getPathVariable: () => _pathVariable,
        getAppPathCandidates: key => _appPaths.TryGetValue(key, out var values) ? values : [],
        windowsDirectory: WindowsDir,
        systemDirectory: SystemDir,
        getCurrentDirectory: () => CurrentDir,
        getPathExtVariable: () => _pathExtVariable);

    #region Bare Names - Search Order

    [Fact]
    public void Resolve_BareName_ReturnsAbsolutePathFromWindowsDirectory()
    {
        // Arrange
        _files.Add(@"C:\Windows\explorer.exe");

        // Act
        var result = CreateSut().Resolve("explorer", null);

        // Assert
        result.FileName.Should().Be(@"C:\Windows\explorer.exe");
        result.Kind.Should().Be(LaunchTargetKind.NativeExecutable);
        result.FoundOnlyInWorkingDirectory.Should().BeNull();
    }

    /// <summary>
    /// Binary planting regression: a same-named program in the current directory (normally the
    /// repository the agent works in) must not win over the system copy.
    /// </summary>
    [Fact]
    public void Resolve_BareNamePlantedInCurrentDirectory_ReturnsSystemCopy()
    {
        // Arrange
        _files.Add(@"C:\repo\notepad.exe");
        _files.Add(@"C:\Windows\System32\notepad.exe");

        // Act
        var result = CreateSut().Resolve("notepad", CurrentDir);

        // Assert
        result.FileName.Should().Be(@"C:\Windows\System32\notepad.exe");
        result.FoundOnlyInWorkingDirectory.Should().BeNull();
        _probedPaths.Should().NotContain(@"C:\repo\notepad.exe");
    }

    [Fact]
    public void Resolve_BareNameOnlyInCurrentDirectory_IsFlaggedAndNotResolved()
    {
        // Arrange
        _files.Add(@"C:\repo\build.exe");

        // Act
        var result = CreateSut().Resolve("build", null);

        // Assert
        result.FileName.Should().Be("build");
        result.Kind.Should().Be(LaunchTargetKind.Other);
        result.FoundOnlyInWorkingDirectory.Should().Be(@"C:\repo\build.exe");
    }

    [Fact]
    public void Resolve_BareNameOnlyInWorkingDirectory_IsFlagged()
    {
        // Arrange
        _files.Add(@"D:\work\tool.cmd");

        // Act
        var result = CreateSut().Resolve("tool", @"D:\work");

        // Assert
        result.FoundOnlyInWorkingDirectory.Should().Be(@"D:\work\tool.cmd");
    }

    [Fact]
    public void Resolve_BareNameNotFoundAnywhere_ReturnsInputAsOther()
    {
        // Act
        var result = CreateSut().Resolve("nonexistent-app", CurrentDir);

        // Assert - marked not found, so the launch refuses it instead of letting ShellExecute search the current directory
        result.FileName.Should().Be("nonexistent-app");
        result.Kind.Should().Be(LaunchTargetKind.Other);
        result.FoundOnlyInWorkingDirectory.Should().BeNull();
        result.NotFound.Should().BeTrue();
    }

    [Fact]
    public void Resolve_BareNameFound_IsNotMarkedNotFound()
    {
        // Arrange
        _files.Add(@"C:\Windows\System32\notepad.exe");

        // Act
        var result = CreateSut().Resolve("notepad", null);

        // Assert
        result.NotFound.Should().BeFalse();
    }

    [Fact]
    public void Resolve_BareNameOnlyInWorkingDirectory_IsNotMarkedNotFound()
    {
        // Arrange - the planted file gets its own error (ExecutableInWorkingDirectory)
        _files.Add(@"C:\repo\build.exe");

        // Act
        var result = CreateSut().Resolve("build", null);

        // Assert
        result.NotFound.Should().BeFalse();
        result.FoundOnlyInWorkingDirectory.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_BareName_FoundOnPath()
    {
        // Arrange
        _files.Add(@"C:\Tools\rg.exe");

        // Act
        var result = CreateSut().Resolve("rg", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\rg.exe");
        result.Kind.Should().Be(LaunchTargetKind.NativeExecutable);
    }

    /// <summary>
    /// VS Code's 'code' command is bin\code.cmd on PATH. PATH is searched before App Paths
    /// (documented ShellExecuteEx order), so the target is a batch file.
    /// </summary>
    [Fact]
    public void Resolve_Code_ResolvesToBatchShimOnPathBeforeAppPaths()
    {
        // Arrange
        _files.Add($@"{VsCodeBin}\code");      // extensionless shell script shipped next to code.cmd
        _files.Add($@"{VsCodeBin}\code.cmd");
        _files.Add(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        _appPaths["code.exe"] = [@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe"];

        // Act
        var result = CreateSut().Resolve("code", null);

        // Assert
        result.FileName.Should().Be($@"{VsCodeBin}\code.cmd");
        result.Kind.Should().Be(LaunchTargetKind.BatchFile);
    }

    [Fact]
    public void Resolve_BareNameWithoutExtension_TriesComExeBatCmdInOrder()
    {
        // Arrange
        _files.Add(@"C:\Tools\tool.cmd");
        _files.Add(@"C:\Tools\tool.exe");

        // Act
        var result = CreateSut().Resolve("tool", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\tool.exe");
    }

    [Fact]
    public void Resolve_BareNameWithoutExtension_FollowsPathExtOrder()
    {
        // Arrange - PATHEXT puts .CMD before .EXE
        _pathExtVariable = ".CMD;.EXE";
        _files.Add(@"C:\Tools\tool.cmd");
        _files.Add(@"C:\Tools\tool.exe");

        // Act
        var result = CreateSut().Resolve("tool", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\tool.cmd");
        result.Kind.Should().Be(LaunchTargetKind.BatchFile);
    }

    [Fact]
    public void Resolve_ScriptExtensionFromPathExt_IsFoundButNotANativeExecutable()
    {
        // Arrange - a script type the user registered in PATHEXT
        _pathExtVariable = ".COM;.EXE;.BAT;.CMD;.VBS;.PY";
        _files.Add(@"C:\Tools\report.py");

        // Act
        var result = CreateSut().Resolve("report", null);

        // Assert - its arguments get the cmd metacharacter check, as for any target that is not a native .exe
        result.FileName.Should().Be(@"C:\Tools\report.py");
        result.Kind.Should().Be(LaunchTargetKind.Other);
        result.NotFound.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EXE;CMD;..\\x;.e/x;.;")]
    public void Resolve_PathExtUnsetOrWithoutUsableEntries_UsesComExeBatCmd(string? pathExt)
    {
        // Arrange - entries without a leading dot, with path characters, or a lone dot are ignored
        _pathExtVariable = pathExt;
        _files.Add(@"C:\Tools\tool.bat");

        // Act
        var result = CreateSut().Resolve("tool", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\tool.bat");
    }

    [Fact]
    public void Resolve_PathExtEntries_AreUsedForThePlantedFileCheckToo()
    {
        // Arrange - only the working directory has the script
        _pathExtVariable = ".EXE;.PS1";
        _files.Add(@"D:\work\deploy.ps1");

        // Act
        var result = CreateSut().Resolve("deploy", @"D:\work");

        // Assert
        result.FoundOnlyInWorkingDirectory.Should().Be(@"D:\work\deploy.ps1");
    }

    [Fact]
    public void Resolve_BareNameWithExplicitBatchExtension_IsBatchFile()
    {
        // Arrange
        _files.Add($@"{VsCodeBin}\code.cmd");

        // Act
        var result = CreateSut().Resolve("code.cmd", null);

        // Assert
        result.FileName.Should().Be($@"{VsCodeBin}\code.cmd");
        result.Kind.Should().Be(LaunchTargetKind.BatchFile);
    }

    [Fact]
    public void Resolve_PathEntriesThatAreNotAbsolute_AreNeverSearched()
    {
        // Arrange - '.', relative and empty PATH entries would resolve against the current directory
        _pathVariable = @".;bin;;.\tools;C:relative";

        // Act
        var result = CreateSut().Resolve("planted", null);

        // Assert
        result.FoundOnlyInWorkingDirectory.Should().BeNull();
        _probedPaths
            .Where(p => !p.StartsWith(@"C:\repo\", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(p => Path.IsPathFullyQualified(p));
    }

    [Fact]
    public void Resolve_QuotedPathEntry_IsSearched()
    {
        // Arrange
        _pathVariable = "\"C:\\Program Files\\Tool\";C:\\Other";
        _files.Add(@"C:\Program Files\Tool\tool.exe");

        // Act
        var result = CreateSut().Resolve("tool", null);

        // Assert
        result.FileName.Should().Be(@"C:\Program Files\Tool\tool.exe");
    }

    [Fact]
    public void Resolve_NullPathVariable_StillSearchesWindowsDirectories()
    {
        // Arrange
        _pathVariable = null;
        _files.Add(@"C:\Windows\System32\calc.exe");

        // Act
        var result = CreateSut().Resolve("calc", null);

        // Assert
        result.FileName.Should().Be(@"C:\Windows\System32\calc.exe");
    }

    #endregion

    #region Bare Names - App Paths

    [Fact]
    public void Resolve_AppPathsEntry_ReturnsRegisteredExecutable()
    {
        // Arrange
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        _files.Add(chrome);
        _appPaths["chrome.exe"] = ["\"" + chrome + "\""];

        // Act
        var result = CreateSut().Resolve("chrome", null);

        // Assert
        result.FileName.Should().Be(chrome);
        result.Kind.Should().Be(LaunchTargetKind.NativeExecutable);
    }

    [Fact]
    public void Resolve_AppPathsFirstCandidateMissing_UsesNextCandidate()
    {
        // Arrange - e.g. a stale HKCU entry followed by a valid HKLM entry
        const string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        _files.Add(edge);
        _appPaths["msedge.exe"] = [@"C:\Stale\msedge.exe", edge];

        // Act
        var result = CreateSut().Resolve("msedge.exe", null);

        // Assert
        result.FileName.Should().Be(edge);
    }

    [Fact]
    public void Resolve_AppPathsValueWithoutExtension_AppendsExe()
    {
        // Arrange
        _files.Add(@"C:\Apps\word\winword.exe");
        _appPaths["winword.exe"] = [@"C:\Apps\word\winword"];

        // Act
        var result = CreateSut().Resolve("winword", null);

        // Assert
        result.FileName.Should().Be(@"C:\Apps\word\winword.exe");
    }

    [Fact]
    public void Resolve_AppPathsRelativeValue_IsIgnored()
    {
        // Arrange
        _files.Add(@"C:\repo\evil.exe");
        _appPaths["evil.exe"] = ["evil.exe"];

        // Act
        var result = CreateSut().Resolve("evil", null);

        // Assert - not resolved from the registry; flagged because only the current directory has it
        result.Kind.Should().Be(LaunchTargetKind.Other);
        result.FoundOnlyInWorkingDirectory.Should().Be(@"C:\repo\evil.exe");
    }

    #endregion

    #region Paths and URIs

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", LaunchTargetKind.NativeExecutable)]
    [InlineData(@"C:\Tools\legacy.COM", LaunchTargetKind.NativeExecutable)]
    [InlineData(@"C:\Tools\build.cmd", LaunchTargetKind.BatchFile)]
    [InlineData(@"C:\Tools\BUILD.BAT", LaunchTargetKind.BatchFile)]
    [InlineData(@"C:\Users\me\Desktop\App.lnk", LaunchTargetKind.Other)]
    [InlineData(@"C:\Docs\report.docx", LaunchTargetKind.Other)]
    [InlineData(@"C:\Scripts\setup.ps1", LaunchTargetKind.Other)]
    [InlineData(@"\\server\share\tool.exe", LaunchTargetKind.NativeExecutable)]
    public void Resolve_AbsolutePath_ClassifiesByExtension(string path, LaunchTargetKind expected)
    {
        // Act
        var result = CreateSut().Resolve(path, null);

        // Assert
        result.FileName.Should().Be(path);
        result.Kind.Should().Be(expected);
    }

    [Fact]
    public void Resolve_AbsolutePathWithoutExtension_ResolvesToBatchFileNextToIt()
    {
        // Arrange - ShellExecute would run C:\Tools\deploy.cmd for "C:\Tools\deploy"
        _files.Add(@"C:\Tools\deploy.cmd");

        // Act
        var result = CreateSut().Resolve(@"C:\Tools\deploy", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\deploy.cmd");
        result.Kind.Should().Be(LaunchTargetKind.BatchFile);
    }

    [Fact]
    public void Resolve_AbsolutePathWithoutExtensionNotFound_IsOther()
    {
        // Act
        var result = CreateSut().Resolve(@"C:\Tools\unknown", null);

        // Assert
        result.FileName.Should().Be(@"C:\Tools\unknown");
        result.Kind.Should().Be(LaunchTargetKind.Other);
    }

    [Theory]
    [InlineData("https://example.com/?a=1&b=2")]
    [InlineData("mailto:someone@example.com")]
    public void Resolve_Uri_IsReturnedUnchangedAsOther(string uri)
    {
        // Act
        var result = CreateSut().Resolve(uri, null);

        // Assert
        result.FileName.Should().Be(uri);
        result.Kind.Should().Be(LaunchTargetKind.Other);
        _probedPaths.Should().BeEmpty();
    }

    #endregion

    #region Real File System

    /// <summary>
    /// Plants same-named files in a temporary directory used as the current directory, then resolves
    /// with the real file system, PATH and registry. Files are created empty and never executed.
    /// </summary>
    [Fact]
    public void Resolve_RealFileSystem_IgnoresProgramsPlantedInCurrentDirectory()
    {
        // Arrange
        var plantDir = Path.Combine(Path.GetTempPath(), $"ExecutableResolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(plantDir);
        var uniqueName = $"zz-planted-tool-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(Path.Combine(plantDir, "notepad.exe"), []);
            File.WriteAllBytes(Path.Combine(plantDir, uniqueName + ".exe"), []);

            var sut = new ExecutableResolver(
                File.Exists,
                () => Environment.GetEnvironmentVariable("PATH"),
                ExecutableResolver.ReadAppPathsFromRegistry,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.SystemDirectory,
                () => plantDir);

            // Act
            var notepad = sut.Resolve("notepad", plantDir);
            var planted = sut.Resolve(uniqueName, plantDir);

            // Assert
            notepad.FileName.Should().NotStartWith(plantDir);
            Path.IsPathFullyQualified(notepad.FileName).Should().BeTrue();
            notepad.FileName.Should().EndWithEquivalentOf(@"\notepad.exe");
            notepad.Kind.Should().Be(LaunchTargetKind.NativeExecutable);

            planted.FileName.Should().Be(uniqueName);
            planted.FoundOnlyInWorkingDirectory.Should().Be(Path.Combine(plantDir, uniqueName + ".exe"));
        }
        finally
        {
            Directory.Delete(plantDir, recursive: true);
        }
    }

    #endregion
}
