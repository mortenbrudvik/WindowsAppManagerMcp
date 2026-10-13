namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ChromiumPrograms"/> (launch_application's forceAccessibility, roadmap M3.3), over an
/// in-memory file system: which programs --force-renderer-accessibility may be added to.
/// </summary>
public class ChromiumProgramsTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string ChromeVersion = @"C:\Program Files\Google\Chrome\Application\131.0.6778.86";
    private const string Code = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string CodeFolder = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _folders = new(StringComparer.OrdinalIgnoreCase);

    private ChromiumPrograms CreateSut() => new(
        _files.Contains,
        folder => _folders.TryGetValue(folder, out var inside) ? inside : []);

    private void AddResources(string folder)
    {
        _files.Add(Path.Combine(folder, "icudtl.dat"));
        _files.Add(Path.Combine(folder, "resources.pak"));
    }

    private void AddChrome()
    {
        _files.Add(Chrome);
        _folders[Path.GetDirectoryName(Chrome)!] = [ChromeVersion, @"C:\Program Files\Google\Chrome\Application\SetupMetrics"];
        AddResources(ChromeVersion);
    }

    private void AddCode()
    {
        _files.Add(Code);
        _files.Add(Path.Combine(CodeFolder, "unins000.exe"));
        AddResources(CodeFolder);
    }

    [Fact]
    public void AChromiumBrowser_WithItsResourcesInAVersionFolder_IsOne()
    {
        AddChrome();

        CreateSut().WhyNot(Chrome).Should().BeNull();
    }

    [Fact]
    public void AnElectronApp_WithItsResourcesNextToIt_IsOne()
    {
        AddCode();

        CreateSut().WhyNot(Code).Should().BeNull();
    }

    [Fact]
    public void AProgramNamedChrome_WithoutChromiumsResources_IsNotOne()
    {
        // The name alone is not trusted
        const string fake = @"C:\Tools\chrome.exe";
        _files.Add(fake);

        CreateSut().WhyNot(fake).Should().Contain("icudtl.dat");
    }

    [Theory]
    [InlineData("unins000.exe")]
    [InlineData("Update.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("chrome_proxy.exe")]
    [InlineData("Uninstall Code.exe")]
    public void AnInstallerOrHelper_NextToTheResources_IsNotOne(string name)
    {
        AddCode();
        var helper = Path.Combine(CodeFolder, name);
        _files.Add(helper);

        CreateSut().WhyNot(helper).Should().Contain("not the program itself");
    }

    [Fact]
    public void AFolderThatIsNoVersion_DoesNotCount()
    {
        // Discord: Update.exe's folder holds app-1.0.9000\, not a version folder
        const string launcher = @"C:\Users\me\AppData\Local\Discord\Discord.exe";
        _files.Add(launcher);
        _folders[@"C:\Users\me\AppData\Local\Discord"] = [@"C:\Users\me\AppData\Local\Discord\app-1.0.9000"];
        AddResources(@"C:\Users\me\AppData\Local\Discord\app-1.0.9000");

        CreateSut().WhyNot(launcher).Should().NotBeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd")]
    [InlineData("code")]
    [InlineData(@"relative\Code.exe")]
    [InlineData("https://example.com")]
    public void SomethingThatIsNoFullPathToAnExe_IsNotOne(string target)
    {
        AddCode();

        CreateSut().WhyNot(target).Should().Be("it is not a program's .exe");
    }

    [Fact]
    public void AProgramThatDoesNotExist_IsNotOne()
    {
        AddResources(CodeFolder);

        CreateSut().WhyNot(Code).Should().Be("the program does not exist");
    }

    [Theory]
    [InlineData("--force-renderer-accessibility", true)]
    [InlineData("--FORCE-RENDERER-ACCESSIBILITY", true)]
    [InlineData("--force-renderer-accessibility=complete", true)]
    [InlineData("--force-renderer-accessibility-x", false)]
    [InlineData("file.txt", false)]
    public void IsForceAccessibilityArgument_RecognizesTheSwitch(string argument, bool expected)
    {
        ChromiumPrograms.IsForceAccessibilityArgument(argument).Should().Be(expected);
    }
}
