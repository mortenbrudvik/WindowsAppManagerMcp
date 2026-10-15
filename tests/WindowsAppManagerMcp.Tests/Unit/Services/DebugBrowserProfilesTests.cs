namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// DebugBrowserProfiles (roadmap M5.4): the server's own profile folders for debug browsers, which profile names are
/// allowed, which folders count as the server's, and the deletes and sweeps that only ever touch those. The roots are
/// real folders under %TEMP% that each test makes and removes; no browser runs.
/// </summary>
public sealed class DebugBrowserProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wam-m54-profiles-{Guid.NewGuid():N}");
    private readonly string _named;
    private readonly string _temporary;
    private DateTime _now = DateTime.UtcNow;

    public DebugBrowserProfilesTests()
    {
        _named = Path.Combine(_root, "browser-profiles");
        _temporary = Path.Combine(_root, "browser-debug");
        Directory.CreateDirectory(_named);
        Directory.CreateDirectory(_temporary);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DebugBrowserProfiles CreateSut() => new(_named, _temporary, () => _now);

    private static string NewGuidName() => Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("work")]
    [InlineData("Work-2")]
    [InlineData("a")]
    [InlineData("shop_test")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void NameRefusal_AcceptsLettersDigitsDashAndUnderscore(string name)
    {
        DebugBrowserProfiles.NameRefusal(name).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-work")]
    [InlineData("_work")]
    [InlineData("..")]
    [InlineData("work/../x")]
    [InlineData(@"..\User Data")]
    [InlineData("C:")]
    [InlineData("work profile")]
    [InlineData("wörk")]
    [InlineData("a.b")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public void NameRefusal_RefusesAnythingThatIsNotOneSimpleFolderName(string name)
    {
        DebugBrowserProfiles.NameRefusal(name).Should().NotBeNull().And.Contain("profile must be");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Com1")]
    [InlineData("LPT9")]
    public void NameRefusal_RefusesWindowsDeviceNames(string name)
    {
        DebugBrowserProfiles.NameRefusal(name).Should().Contain("device name");
    }

    [Fact]
    public void NamedPath_IsTheNameDirectlyUnderTheNamedRoot_AndRefusesABadName()
    {
        var sut = CreateSut();

        sut.NamedPath("work").Should().Be(Path.Combine(_named, "work"));
        sut.Invoking(s => s.NamedPath(@"..\x")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NewTemporaryPath_IsANewGuidFolderUnderTheTemporaryRoot()
    {
        var sut = CreateSut();

        var first = sut.NewTemporaryPath();
        var second = sut.NewTemporaryPath();

        first.Should().NotBe(second);
        Path.GetDirectoryName(first).Should().Be(_temporary);
        sut.KindOf(first).Should().Be(DebugProfileKind.Temporary);
        Directory.Exists(first).Should().BeFalse("it is created by the launch");
    }

    [Fact]
    public void KindOf_TellsTheServersFoldersFromAnyOther()
    {
        var sut = CreateSut();
        var guid = NewGuidName();

        sut.KindOf(Path.Combine(_temporary, guid)).Should().Be(DebugProfileKind.Temporary);
        sut.KindOf(Path.Combine(_temporary, guid.ToUpperInvariant()) + "\\").Should().Be(DebugProfileKind.Temporary, "case and a trailing separator do not matter");
        sut.KindOf(Path.Combine(_named, "work")).Should().Be(DebugProfileKind.Named);
        sut.KindOf(Path.Combine(_named.ToUpperInvariant(), "work")).Should().Be(DebugProfileKind.Named);

        // Not the server's: another name, a folder further down, the roots themselves, a relative path, the user's profile
        sut.KindOf(Path.Combine(_temporary, "browser-debug-1")).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(Path.Combine(_temporary, guid, "Default")).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(Path.Combine(_named, "work", "nested")).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(Path.Combine(_named, "-bad")).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(_temporary).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(_named).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(Path.Combine(_named, "..", "browser-debug", guid)).Should().Be(DebugProfileKind.Temporary, "the full path decides");
        sut.KindOf(Path.Combine(_named, "work", "..", "..", "elsewhere")).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(@"relative\" + guid).Should().Be(DebugProfileKind.Custom);
        sut.KindOf(@"C:\Users\me\AppData\Local\Google\Chrome\User Data").Should().Be(DebugProfileKind.Custom);
        sut.KindOf(null).Should().Be(DebugProfileKind.Custom);
    }

    [Fact]
    public async Task DeleteAsync_DeletesATemporaryAndANamedProfile_WithTheirFiles()
    {
        // Arrange
        var sut = CreateSut();
        var temporary = Path.Combine(_temporary, NewGuidName());
        var named = Path.Combine(_named, "work");
        foreach (var folder in new[] { temporary, named })
        {
            Directory.CreateDirectory(Path.Combine(folder, "Default"));
            File.WriteAllText(Path.Combine(folder, "Default", "Cookies"), "x");
        }

        File.SetAttributes(Path.Combine(named, "Default", "Cookies"), FileAttributes.ReadOnly);

        // Act
        var temporaryFailure = await sut.DeleteAsync(temporary, TimeSpan.FromSeconds(2));
        var namedFailure = await sut.DeleteAsync(named, TimeSpan.FromSeconds(2));

        // Assert
        temporaryFailure.Should().BeNull();
        namedFailure.Should().BeNull("a read-only file does not stop the delete");
        Directory.Exists(temporary).Should().BeFalse();
        Directory.Exists(named).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NeverDeletesAFolderThatIsNotTheServers()
    {
        // Arrange - a userDataDir the agent passed, next to the server's roots
        var sut = CreateSut();
        var custom = Path.Combine(_root, "MyProfile");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "Cookies"), "x");

        // Act
        var failure = await sut.DeleteAsync(custom, TimeSpan.FromSeconds(1));

        // Assert
        failure.Should().Contain("not one of the server's own profile folders");
        File.Exists(Path.Combine(custom, "Cookies")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_OfAFolderThatIsGone_Succeeds()
    {
        (await CreateSut().DeleteAsync(Path.Combine(_temporary, NewGuidName()), TimeSpan.Zero)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_TriesAgainWhileAFileIsHeld_AndSucceedsOnceItIsReleased()
    {
        // Arrange - a browser process that exits a moment after its main process still holds a file
        var sut = new DebugBrowserProfiles(_named, _temporary);
        var folder = Path.Combine(_temporary, NewGuidName());
        Directory.CreateDirectory(folder);
        var held = new FileStream(Path.Combine(folder, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.None);
        _ = Task.Delay(400).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        // Act
        var failure = await sut.DeleteAsync(folder, TimeSpan.FromSeconds(10));

        // Assert
        failure.Should().BeNull();
        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_GivesUpAfterItsBudget_AndSaysWhy()
    {
        // Arrange
        var sut = new DebugBrowserProfiles(_named, _temporary);
        var folder = Path.Combine(_temporary, NewGuidName());
        Directory.CreateDirectory(folder);
        using var held = new FileStream(Path.Combine(folder, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.None);

        // Act
        var failure = await sut.DeleteAsync(folder, TimeSpan.FromMilliseconds(300));

        // Assert
        failure.Should().NotBeNullOrEmpty();
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void SweepStaleTemporary_DeletesOnlyOldUnheldTemporaryProfiles_ThatAreNotKept()
    {
        // Arrange
        var stale = Path.Combine(_temporary, NewGuidName());
        var held = Path.Combine(_temporary, NewGuidName());
        var kept = Path.Combine(_temporary, NewGuidName());
        var otherName = Path.Combine(_temporary, "not-a-profile");
        var named = Path.Combine(_named, "work");
        foreach (var folder in new[] { stale, held, kept, otherName, named })
            Directory.CreateDirectory(folder);

        using var lockfile = new FileStream(Path.Combine(held, ChromiumProfileLock.FileName), FileMode.Create, FileAccess.Write,
            FileShare.Read, 1, FileOptions.DeleteOnClose);
        _now = DateTime.UtcNow + TimeSpan.FromHours(1);
        var sut = CreateSut();

        // Act
        var swept = sut.SweepStaleTemporary(folder => DebugBrowserProfiles.SamePath(folder, kept));

        // Assert
        swept.Should().Be(1);
        Directory.Exists(stale).Should().BeFalse();
        Directory.Exists(held).Should().BeTrue("a running browser holds its lockfile");
        Directory.Exists(kept).Should().BeTrue("a browser this server started still uses it");
        Directory.Exists(otherName).Should().BeTrue("only a temporary profile's name is the server's");
        Directory.Exists(named).Should().BeTrue("named profiles are never swept");
    }

    [Fact]
    public void SweepStaleTemporary_KeepsAYoungProfile_ABrowserMayBeStartingWith()
    {
        // Arrange
        var young = Path.Combine(_temporary, NewGuidName());
        Directory.CreateDirectory(young);
        _now = DateTime.UtcNow + DebugBrowserProfiles.SweepMinimumAge - TimeSpan.FromMinutes(1);

        // Act
        var swept = CreateSut().SweepStaleTemporary();

        // Assert
        swept.Should().Be(0);
        Directory.Exists(young).Should().BeTrue();
    }

    [Fact]
    public void SweepStaleTemporary_WithoutTheRoot_DoesNothing()
    {
        new DebugBrowserProfiles(_named, Path.Combine(_root, "missing")).SweepStaleTemporary().Should().Be(0);
    }

    [Fact]
    public void FromEnvironment_UsesTheServersFoldersUnderLocalAppDataAndTemp()
    {
        var profiles = DebugBrowserProfiles.FromEnvironment();

        profiles.NamedRoot.Should().EndWith(@"WindowsAppManagerMcp\browser-profiles")
            .And.StartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        profiles.TemporaryRoot.Should().EndWith(@"WindowsAppManagerMcp\browser-debug")
            .And.StartWith(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
    }
}
