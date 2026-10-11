namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The UPDATE_SNAPSHOTS=1 switch of the snapshot and generated-docs tests. In CI it would turn every one of those
/// guards into a rewrite that passes, so a CI run refuses it. Regenerated files are written in one step, so a test
/// that reads a file in parallel never sees it half written. Temporary files only.
/// </summary>
public class SnapshotsTests
{
    [Fact]
    public void IsUpdateRequested_IsFalse_WhenTheVariableIsNotOne()
    {
        Snapshots.IsUpdateRequested(Environment(("UPDATE_SNAPSHOTS", null))).Should().BeFalse();
        Snapshots.IsUpdateRequested(Environment(("UPDATE_SNAPSHOTS", "0"))).Should().BeFalse();
        Snapshots.IsUpdateRequested(Environment(("UPDATE_SNAPSHOTS", "true"))).Should().BeFalse("only 1 turns it on");
    }

    [Fact]
    public void IsUpdateRequested_IsTrue_OnADeveloperMachine()
    {
        Snapshots.IsUpdateRequested(Environment(("UPDATE_SNAPSHOTS", "1"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("CI", "true")]
    [InlineData("CI", "True")]
    [InlineData("GITHUB_ACTIONS", "true")]
    [InlineData("TF_BUILD", "True")]
    public void IsUpdateRequested_InCI_Throws(string variable, string value)
    {
        // A leaked UPDATE_SNAPSHOTS=1 (a repository variable, a workflow env) would make the snapshot and docs tests
        // rewrite their files on the runner and pass; refusing fails the run instead
        var act = () => Snapshots.IsUpdateRequested(Environment(("UPDATE_SNAPSHOTS", "1"), (variable, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*UPDATE_SNAPSHOTS=1*CI*");
    }

    [Fact]
    public void IsUpdateRequested_InCI_WithoutTheVariable_IsFalse()
    {
        Snapshots.IsUpdateRequested(Environment(("CI", "true"), ("GITHUB_ACTIONS", "true"))).Should().BeFalse();
    }

    [Fact]
    public void WritePreservingFormat_ReplacesTheFileInOneStep_AndKeepsItsLineEndsAndByteOrderMark()
    {
        // Arrange
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"SnapshotsTests_{Guid.NewGuid():N}"));
        try
        {
            var path = Path.Combine(folder.FullName, "doc.md");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "old\r\ntext\r\n"u8.ToArray()]);

            // Act
            Snapshots.WritePreservingFormat(path, "new\ntext\n");

            // Assert
            File.ReadAllBytes(path).Should().Equal([0xEF, 0xBB, 0xBF, .. "new\r\ntext\r\n"u8.ToArray()]);
            folder.GetFiles().Select(f => f.Name).Should().Equal(["doc.md"], "no temporary file is left behind");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static Func<string, string?> Environment(params (string Name, string? Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;
}
