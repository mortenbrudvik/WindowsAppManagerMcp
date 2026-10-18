namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// DebugBrowserLocator (roadmap M5.1): App Paths first, then Program Files, Program Files (x86) and %LOCALAPPDATA%, or
/// only the explicit executablePath; never the current directory or PATH. The file system, registry and special folders
/// are fakes.
/// </summary>
public class DebugBrowserLocatorTests
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _appPaths = new(StringComparer.OrdinalIgnoreCase);

    private DebugBrowserLocator CreateSut(bool sameProgramFiles = false) => new(
        _files.Contains,
        name => _appPaths.GetValueOrDefault(name) ?? [],
        folder => folder switch
        {
            Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
            Environment.SpecialFolder.ProgramFilesX86 => sameProgramFiles ? @"C:\Program Files" : @"C:\Program Files (x86)",
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\me\AppData\Local",
            _ => string.Empty
        });

    [Theory]
    [InlineData(DebugBrowser.Chrome, @"C:\Users\me\AppData\Local\Google\Chrome\Application\chrome.exe")]
    [InlineData(DebugBrowser.Edge, @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(DebugBrowser.Brave, @"C:\Users\me\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe")]
    public void ABrowserInItsInstallFolder_IsFound(DebugBrowser browser, string path)
    {
        // Arrange
        _files.Add(path);

        // Act
        var location = CreateSut().Locate(browser);

        // Assert
        location.Path.Should().Be(path);
        location.Tried.Should().EndWith(path);
    }

    [Fact]
    public void AppPaths_ComeFirst_HKCUBeforeHKLM()
    {
        // Arrange - a per-user install registered in App Paths, and a system-wide one in Program Files
        _appPaths["chrome.exe"] = [@"D:\Apps\Chrome\chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"];
        _files.Add(@"D:\Apps\Chrome\chrome.exe");
        _files.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");

        // Act & Assert
        CreateSut().Locate(DebugBrowser.Chrome).Path.Should().Be(@"D:\Apps\Chrome\chrome.exe");
    }

    [Theory]
    [InlineData(@"""D:\Apps\Chrome\chrome.exe""", @"D:\Apps\Chrome\chrome.exe")]
    [InlineData(@" D:\Apps\Chrome\chrome.exe ", @"D:\Apps\Chrome\chrome.exe")]
    public void AQuotedAppPathsValue_IsUnquoted(string value, string path)
    {
        // Arrange
        _appPaths["chrome.exe"] = [value];
        _files.Add(path);

        // Act & Assert
        CreateSut().Locate(DebugBrowser.Chrome).Path.Should().Be(path);
    }

    [Theory]
    [InlineData(@"C:\Tools\launcher.exe")]
    [InlineData(@"chrome.exe")]
    [InlineData(@"Chrome\chrome.exe")]
    public void AnAppPathsValueThatIsNotAFullPathToTheBrowsersExe_IsSkipped(string value)
    {
        // Arrange - another program registered under chrome.exe, or a relative value the current directory would resolve
        _appPaths["chrome.exe"] = [value];
        _files.Add(value);
        _files.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");

        // Act
        var location = CreateSut().Locate(DebugBrowser.Chrome);

        // Assert
        location.Path.Should().Be(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        location.Tried.Should().StartWith(value);
    }

    [Fact]
    public void NothingFound_ListsEveryPlaceTried_InOrder()
    {
        // Act
        var location = CreateSut().Locate(DebugBrowser.Brave);

        // Assert
        location.Path.Should().BeNull();
        location.Tried.Should().Equal(
            @"App Paths\brave.exe",
            @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"C:\Users\me\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe");
    }

    [Fact]
    public void TheSameFolderTwice_IsTriedOnce()
    {
        // Act - a 32-bit view, where both Program Files are one folder
        var location = CreateSut(sameProgramFiles: true).Locate(DebugBrowser.Edge);

        // Assert
        location.Tried.Should().OnlyHaveUniqueItems().And.HaveCount(3);
    }

    [Fact]
    public void AnExplicitExecutablePath_IsTheOnlyPlaceTried()
    {
        // Arrange
        _files.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");

        // Act
        var missing = CreateSut().Locate(DebugBrowser.Chrome, @"E:\Chrome Canary\chrome.exe");
        _files.Add(@"E:\Chrome Canary\chrome.exe");
        var found = CreateSut().Locate(DebugBrowser.Chrome, @" E:\Chrome Canary\chrome.exe ");

        // Assert
        missing.Path.Should().BeNull();
        missing.Tried.Should().Equal(@"E:\Chrome Canary\chrome.exe");
        found.Path.Should().Be(@"E:\Chrome Canary\chrome.exe");
    }

    [Theory]
    [InlineData(DebugBrowser.Chrome, "chrome.exe")]
    [InlineData(DebugBrowser.Edge, "msedge.exe")]
    [InlineData(DebugBrowser.Brave, "brave.exe")]
    [InlineData((DebugBrowser)99, null)]
    public void ExecutableName_IsTheBrowsersOwnExe(DebugBrowser browser, string? name)
    {
        DebugBrowserLocator.ExecutableName(browser).Should().Be(name);
    }

    [Fact]
    public void EveryBrowserTheToolTakes_HasAnInstall_SoNoneIsUnsupportedAtRunTime()
    {
        // The DebugBrowser enum is the list: the tool's schema, the launch and close_debug_browser all derive from it
        foreach (var browser in Enum.GetValues<DebugBrowser>())
        {
            var name = DebugBrowserLocator.ExecutableName(browser);
            name.Should().NotBeNull($"{browser} needs an install entry");
            DebugBrowserLocator.ExecutableNames.Should().Contain(name!);

            var path = Path.Combine(@"C:\Program Files", "Install", name!);
            _files.Clear();
            _appPaths[name!] = [path];
            _files.Add(path);
            CreateSut().Locate(browser).Path.Should().Be(path);
        }

        DebugBrowserLocator.ExecutableNames.Should().HaveCount(Enum.GetValues<DebugBrowser>().Length);
    }
}
