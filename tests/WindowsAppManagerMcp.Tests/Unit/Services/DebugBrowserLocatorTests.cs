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
    [InlineData("chrome", @"C:\Users\me\AppData\Local\Google\Chrome\Application\chrome.exe")]
    [InlineData("edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("brave", @"C:\Users\me\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe")]
    public void ABrowserInItsInstallFolder_IsFound(string browser, string path)
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
        CreateSut().Locate("chrome").Path.Should().Be(@"D:\Apps\Chrome\chrome.exe");
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
        CreateSut().Locate("chrome").Path.Should().Be(path);
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
        var location = CreateSut().Locate("chrome");

        // Assert
        location.Path.Should().Be(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        location.Tried.Should().StartWith(value);
    }

    [Fact]
    public void NothingFound_ListsEveryPlaceTried_InOrder()
    {
        // Act
        var location = CreateSut().Locate("brave");

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
        var location = CreateSut(sameProgramFiles: true).Locate("edge");

        // Assert
        location.Tried.Should().OnlyHaveUniqueItems().And.HaveCount(3);
    }

    [Fact]
    public void AnExplicitExecutablePath_IsTheOnlyPlaceTried()
    {
        // Arrange
        _files.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");

        // Act
        var missing = CreateSut().Locate("chrome", @"E:\Chrome Canary\chrome.exe");
        _files.Add(@"E:\Chrome Canary\chrome.exe");
        var found = CreateSut().Locate("chrome", @" E:\Chrome Canary\chrome.exe ");

        // Assert
        missing.Path.Should().BeNull();
        missing.Tried.Should().Equal(@"E:\Chrome Canary\chrome.exe");
        found.Path.Should().Be(@"E:\Chrome Canary\chrome.exe");
    }

    [Theory]
    [InlineData("chrome", "chrome.exe")]
    [InlineData("EDGE", "msedge.exe")]
    [InlineData("brave", "brave.exe")]
    [InlineData("firefox", null)]
    public void ExecutableName_IsTheBrowsersOwnExe(string browser, string? name)
    {
        DebugBrowserLocator.ExecutableName(browser).Should().Be(name);
    }
}
