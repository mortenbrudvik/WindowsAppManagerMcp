namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The one folder the fixture's Save As button may write into (roadmap M3.F4): the fixture compiles the same
/// FixtureSaveFolder.cs. Pure tests; nothing is started or written.
/// </summary>
public class FixtureSaveFolderTests
{
    private const string Folder = @"C:\Temp\WamE2E\abc123";

    private static string Same(string path) => path;

    [Fact]
    public void For_IsTheFolderOfTheEventLog()
    {
        var options = new FixtureOptions("abc", EventLogPath: Folder + @"\events.jsonl");

        FixtureSaveFolder.For(options).Should().Be(Folder);
    }

    [Fact]
    public void For_WithoutAnEventLog_IsNull_SoTheButtonWritesNothing()
    {
        FixtureSaveFolder.For(new FixtureOptions("abc")).Should().BeNull();
    }

    [Theory]
    [InlineData(Folder + @"\saved.txt")]
    [InlineData(@"c:\temp\wame2e\ABC123\Saved.TXT")]
    [InlineData(Folder + @"\sub\..\saved.txt")]
    public void Allows_AFileDirectlyInTheFolder(string path)
    {
        FixtureSaveFolder.Allows(Folder, path, Same).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Temp\WamE2E\saved.txt")]
    [InlineData(Folder + @"\sub\saved.txt")]
    [InlineData(Folder + @"\..\saved.txt")]
    [InlineData(Folder + @"4\saved.txt")]
    [InlineData(@"D:\Temp\WamE2E\abc123\saved.txt")]
    [InlineData(@"\\server\share\saved.txt")]
    [InlineData(@"saved.txt")]
    [InlineData(Folder + @"\saved.txt:stream")]
    [InlineData(Folder + @"\")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_AnyOtherPath(string? path)
    {
        FixtureSaveFolder.Allows(Folder, path, Same).Should().BeFalse();
    }

    [Fact]
    public void Refuses_EveryPath_WithoutAFolder()
    {
        FixtureSaveFolder.Allows(null, Folder + @"\saved.txt", Same).Should().BeFalse();
    }

    [Fact]
    public void Allows_AFolderSpelledWithShortNames_ThroughItsCanonicalForm()
    {
        // Arrange - the harness may name the temp folder by its 8.3 short name, the dialog by its long one
        static string Canonical(string path) => path.Replace(@"\MORTEN~1\", @"\morten brudvik\", StringComparison.OrdinalIgnoreCase);

        // Act
        var allowed = FixtureSaveFolder.Allows(@"C:\Users\MORTEN~1\Temp\abc", @"C:\Users\morten brudvik\Temp\abc\saved.txt", Canonical);

        // Assert
        allowed.Should().BeTrue();
    }
}
