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
    }

    [Fact]
    public void TheMessage_SaysWhatTheSwitchDoes_AndWhatToUseInstead()
    {
        var debug = CreateSut().Check(Edge, ["--remote-debugging-port=9222"], null)!;
        debug.Message.Should().Contain("--remote-debugging-port").And.Contain("launch_browser_with_debug");

        var command = CreateSut().Check(Edge, ["--gpu-launcher=calc.exe"], null)!;
        command.Message.Should().Contain("--gpu-launcher").And.Contain("command");
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
