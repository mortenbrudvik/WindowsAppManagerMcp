namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// <see cref="ChromiumSwitchGuard"/> (review F2): the launch tools refuse the Chromium and Electron switches that make a
/// program run a command line, load code, or open it to remote control, as Chromium reads a switch ("--", "-" or "/",
/// white space trimmed, any letter case, a value after "="), also inside another switch's value, and a --user-data-dir in a
/// browser's real 'User Data' folder (as launch_browser_with_debug does, review MCP-S-1). Only strings and a fake file
/// system: nothing is started.
/// </summary>
public class ChromiumSwitchGuardTests
{
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string ChromeUserData = @"C:\Users\me\AppData\Local\Google\Chrome\User Data";

    private readonly Dictionary<string, string> _longPaths = new(StringComparer.OrdinalIgnoreCase);

    private ChromiumSwitchGuard CreateSut() => new(path => _longPaths.GetValueOrDefault(path));

    [Theory]
    // Switches with which Chromium starts its child processes through a command line of the caller's choosing
    [InlineData("--utility-cmd-prefix=powershell -c calc")]
    [InlineData("--renderer-cmd-prefix=calc.exe")]
    [InlineData("--gpu-launcher=calc.exe")]
    [InlineData("--plugin-launcher=calc.exe")]
    [InlineData("--ppapi-plugin-launcher=calc.exe")]
    [InlineData(@"--browser-subprocess-path=C:\Temp\evil.exe")]
    // As Chromium reads a switch on Windows: '-' and '/' too, white space trimmed, any letter case
    [InlineData("-gpu-launcher=calc.exe")]
    [InlineData("/gpu-launcher=calc.exe")]
    [InlineData("  --GPU-Launcher=calc.exe")]
    [InlineData("\t--utility-cmd-prefix")]
    // Remote control over an unauthenticated port or pipe
    [InlineData("--remote-debugging-port=9222")]
    [InlineData("--remote-debugging-pipe")]
    [InlineData("--remote-debugging-address=0.0.0.0")]
    [InlineData("--remote-debugging-targets=127.0.0.1:9222")]
    [InlineData("--remote-allow-origins=*")]
    // Unpacked extensions: code that runs in the browser
    [InlineData(@"--load-extension=C:\Temp\ext")]
    [InlineData(@"--disable-extensions-except=C:\Temp\ext")]
    // Electron's Node.js inspector and V8 flags (Node takes '_' for '-')
    [InlineData("--inspect")]
    [InlineData("--inspect=9229")]
    [InlineData("--inspect-brk=0.0.0.0:9229")]
    [InlineData("--inspect-port=9229")]
    [InlineData("--inspect_brk")]
    [InlineData("--inspect-extensions=9333")]
    [InlineData("--js-flags=--allow-natives-syntax")]
    // The user's real profile
    [InlineData(@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\User Data")]
    [InlineData("--user-data-dir=C:/Users/me/AppData/Local/Microsoft/Edge/user data/Default")]
    [InlineData(@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\USERDA~1")]
    [InlineData(@"--user-data-dir=""C:\Users\me\AppData\Local\Google\Chrome\User Data""")]
    [InlineData(@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\User Data.")]
    [InlineData(@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\User Data::$INDEX_ALLOCATION")]
    [InlineData(@"--user-data-dir=\\?\C:\Users\me\AppData\Local\Google\Chrome\User Data")]
    // A switch handed on in another switch's value, or in one argument with others (a launcher passes them on)
    [InlineData("--process-start-args=--gpu-launcher=calc.exe")]
    [InlineData(@"--process-start-args=""--foo --utility-cmd-prefix=calc""")]
    [InlineData("--foo --renderer-cmd-prefix=calc")]
    // A quoted switch without white space: a launcher hands it on as raw text, which the program unquotes (Squirrel's
    // Update.exe --process-start-args)
    [InlineData(@"""--gpu-launcher=C:\x\e.exe""")]
    [InlineData(@"""--inspect=9229""")]
    [InlineData(@"--""inspect""")]
    [InlineData(@"--process-start-args=""--inspect""")]
    [InlineData(@"--process-start-args=""--remote-debugging-port=9222""")]
    // VS Code's switches that load and run an extension or a test script, or install one, and its old inspector switches
    [InlineData(@"--extensionDevelopmentPath=C:\Temp\ext")]
    [InlineData(@"--extensionTestsPath=C:\Temp\tests.js")]
    [InlineData("--extensionEnvironment={}")]
    [InlineData(@"--extensions-dir=C:\Temp\extensions")]
    [InlineData(@"--builtin-extensions-dir=C:\Temp\extensions")]
    [InlineData(@"--install-extension=C:\Temp\ext.vsix")]
    [InlineData("--install-extension")]
    [InlineData("--install-builtin-extension")]
    [InlineData("--debugPluginHost=9333")]
    [InlineData("--debugBrkPluginHost=9333")]
    // Nested deeper than the guard reads: refused, not let through unread
    [InlineData("--a=--b=--c=--d=--e=--gpu-launcher=calc.exe")]
    [InlineData(@"--a=--b=--c=--d=""--e --gpu-launcher=calc.exe""")]
    public void ASwitchThatRunsACommand_LoadsCode_OrOpensTheProgramToRemoteControl_IsRefused(string argument)
    {
        // Act
        var refusal = CreateSut().Check(Edge, ["https://example.com/", argument], workingDirectory: null);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        refusal.Message.Should().Contain(Edge).And.Contain("Nothing was launched");
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/?q=--gpu-launcher=calc.exe")]
    [InlineData("--new-window")]
    [InlineData("--incognito")]
    [InlineData("--force-renderer-accessibility")]
    [InlineData("--disable-extensions")]
    [InlineData("--profile-directory=Default")]
    [InlineData("--app=https://example.com/")]
    [InlineData(@"--user-data-dir=C:\Temp\debug-profile")]
    [InlineData(@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\User Data Backup")]
    [InlineData(@"C:\Projects\launcher")]
    [InlineData(@"C:\notes\remote-debugging-port.txt")]
    [InlineData("--inspector-notes")]
    [InlineData(@"/select,C:\Users\me\Documents")]
    [InlineData("")]
    [InlineData(@"""C:\Program Files\notes.txt""")]
    [InlineData(@"""https://example.com/?a=b""")]
    [InlineData(@"--process-start-args=""--foo \""C:\My Files\x.txt\""""")]
    [InlineData("--a=--b=--c=--d=e")]
    [InlineData("--extensions-download-dir-notes")]
    public void AnyOtherArgument_IsAllowed(string argument)
    {
        CreateSut().Check(Edge, [argument], workingDirectory: null).Should().BeNull();
    }

    [Fact]
    public void NoArguments_AreAllowed()
    {
        CreateSut().Check(Edge, null, null).Should().BeNull();
        CreateSut().Check(Edge, [], null).Should().BeNull();
    }

    [Fact]
    public void AUserDataDirGivenAsTheNextArgument_IsChecked()
    {
        // VS Code and other Electron apps read "--user-data-dir <folder>" as well
        CreateSut().Check(Edge, ["--user-data-dir", ChromeUserData], null)!.ErrorCode
            .Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        CreateSut().Check(Edge, ["--user-data-dir", @"C:\Temp\profile"], null).Should().BeNull();

        // Also when the switch is quoted, as a launcher hands it on
        CreateSut().Check(Edge, ["\"--user-data-dir\"", ChromeUserData], null)!.ErrorCode
            .Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
    }

    [Fact]
    public void ARelativeUserDataDir_IsTakenInTheWorkingDirectory()
    {
        CreateSut().Check(Edge, ["--user-data-dir=."], ChromeUserData).Should().NotBeNull();
        CreateSut().Check(Edge, [@"--user-data-dir=Default\.."], ChromeUserData).Should().NotBeNull();
        CreateSut().Check(Edge, ["--user-data-dir=."], @"C:\Temp").Should().BeNull();
    }

    [Fact]
    public void AnEightDotThreeNameOfAUserDataFolder_IsExpanded_AlsoWhenTheFolderInsideItDoesNotExistYet()
    {
        // Arrange - a hashed short name, which the USERDA~n pattern does not catch
        _longPaths[@"C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1"] = ChromeUserData;

        // Act & Assert
        CreateSut().Check(Edge, [@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1"], null).Should().NotBeNull();
        CreateSut().Check(Edge, [@"--user-data-dir=C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1\New"], null).Should().NotBeNull();
        CreateSut().Check(Edge, [@"--user-data-dir=C:\Temp\PROFIL~1"], null).Should().BeNull();

        // A drive letter's device path is local too
        _longPaths[@"\\?\C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1"] = @"\\?\" + ChromeUserData;
        CreateSut().Check(Edge, [@"--user-data-dir=\\?\C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1"], null).Should().NotBeNull();
    }

    [Theory]
    [InlineData(@"--user-data-dir=\\10.255.255.1\s\a\b\c~1", null)]
    [InlineData("--user-data-dir=//10.255.255.1/s/c~1", null)]
    [InlineData(@"--user-data-dir=\\?\UNC\10.255.255.1\s\c~1", null)]
    [InlineData(@"--user-data-dir=\\?\GLOBALROOT\Device\Mup\10.255.255.1\s\c~1", null)]
    [InlineData(@"--user-data-dir=\\localhost\C$\Users\me\AppData\Local\Google\Chrome\US1A2B~1", null)]
    [InlineData("--user-data-dir=c~1", @"\\10.255.255.1\s")]
    public void AShortNameOnANetworkOrDevicePath_IsRefused_WithoutReadingTheFileSystem(string argument, string? workingDirectory)
    {
        // Arrange - expanding it would make the server connect to a computer the agent named, maybe for minutes and with
        // the user's credentials; and \\localhost\C$ reaches the real profile's folder
        var asked = new List<string>();
        var sut = new ChromiumSwitchGuard(path =>
        {
            asked.Add(path);
            return null;
        });

        // Act
        var refusal = sut.Check(Edge, [argument], workingDirectory);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        refusal.Message.Should().Contain("short name").And.Contain("Nothing was launched");
        asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"--user-data-dir=\\10.255.255.1\s\a\b\c~1", null)]
    [InlineData("--user-data-dir=//10.255.255.1/s/c~1", null)]
    [InlineData(@"--user-data-dir=\\localhost\C$\Users\me\AppData\Local\Google\Chrome\US1A2B~1", null)]
    [InlineData("--user-data-dir=c~1", @"\\10.255.255.1\s")]
    [InlineData("--user-data-dir=profile", @"\\10.255.255.1\PROFIL~1")]
    public void AShortNameOnANetworkPath_IsRefused_BeforeThePathIsMadeFull(string argument, string? workingDirectory)
    {
        // Arrange - Path.GetFullPath expands 8.3 short names itself, from the file system (GetLongPathNameW): on a UNC path
        // that connects to the computer it names (seen: 21 s for an address that does not answer). The recorder makes a path
        // full by its text only, so a test that fails touches no network either
        var made = new List<string>();
        var sut = new ChromiumSwitchGuard(_ => null, (path, basePath) =>
        {
            made.Add(basePath == null ? path : $"{path} in {basePath}");
            return basePath == null ? path : Path.Join(basePath, path);
        });

        // Act
        var refusal = sut.Check(Edge, [argument], workingDirectory);

        // Assert
        refusal!.Message.Should().Contain("short name").And.Contain("Nothing was launched");
        made.Should().BeEmpty();
    }

    [Fact]
    public void ANetworkPathWithoutAShortName_IsCheckedByItsText_WithoutReadingTheFileSystem()
    {
        var asked = new List<string>();
        var sut = new ChromiumSwitchGuard(path =>
        {
            asked.Add(path);
            return null;
        });

        sut.Check(Edge, [@"--user-data-dir=\\server\s\profiles\debug"], null).Should().BeNull();
        sut.Check(Edge, [@"--user-data-dir=\\localhost\C$\Users\me\AppData\Local\Google\Chrome\User Data"], null).Should().NotBeNull();
        sut.Check(Edge, [@"--user-data-dir=\\localhost\C$\Users\me\AppData\Local\Google\Chrome\USERDA~1"], null).Should().NotBeNull();
        asked.Should().BeEmpty();
    }

    [Fact]
    public void TheMessage_SaysWhatTheSwitchDoes_AndWhatToUseInstead()
    {
        var debug = CreateSut().Check(Edge, ["--remote-debugging-port=9222"], null)!;
        debug.Message.Should().Contain("--remote-debugging-port").And.Contain("launch_browser_with_debug");

        var command = CreateSut().Check(Edge, ["--gpu-launcher=calc.exe"], null)!;
        command.Message.Should().Contain("--gpu-launcher").And.Contain("command");
    }

    [Theory]
    [InlineData(@"--load-extension=C:\Temp\ext")]
    [InlineData(@"--extensionTestsPath=C:\Temp\tests.js")]
    [InlineData("--inspect=9229")]
    [InlineData("--js-flags=--allow-natives-syntax")]
    [InlineData("--a=--b=--c=--d=--e=--gpu-launcher=calc.exe")]
    public void EveryRefusal_SaysWhatToDoInstead(string argument)
    {
        // Act
        var message = CreateSut().Check(Edge, [argument], null)!.Message;

        // Assert - what happened, what was not done, then what to do
        const string notDone = "Nothing was launched. ";
        message.Should().Contain(notDone);
        message[(message.IndexOf(notDone, StringComparison.Ordinal) + notDone.Length)..].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ASwitchNestedTooDeep_IsRefusedAsUnread_NotNamedAsASwitch()
    {
        var message = CreateSut().Check(Edge, ["--a=--b=--c=--d=--e=--gpu-launcher=calc.exe"], null)!.Message;

        message.Should().Contain("deeper than").And.NotContain("as the switch --e");
    }

    [Fact]
    public void LaunchBrowserWithDebug_MayPassItsOwnDebugPort_ButNoOtherSwitchOfTheList()
    {
        // The debug launch builds --remote-debugging-port itself, next to a profile the tool checked (review MCP-S-1)
        var sut = CreateSut();
        string[] own = ["--no-first-run", "--remote-debugging-port=0", @"--user-data-dir=C:\Temp\browser-debug-1", "https://example.com/"];

        sut.Check(Edge, own, null, debugBrowserLaunch: true).Should().BeNull();
        sut.Check(Edge, own, null).Should().NotBeNull("only the debug launch may open a debug port");
        sut.Check(Edge, [.. own, "--remote-allow-origins=*"], null, debugBrowserLaunch: true).Should().NotBeNull();
        sut.Check(Edge, [.. own, "--remote-debugging-address=0.0.0.0"], null, debugBrowserLaunch: true).Should().NotBeNull();
        sut.Check(Edge, ["--remote-debugging-port=0", $"--user-data-dir={ChromeUserData}"], null, debugBrowserLaunch: true)
            .Should().NotBeNull("the real profile is refused for the debug launch too");
    }
}
