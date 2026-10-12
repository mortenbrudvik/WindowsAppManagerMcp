using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The path file_dialog types (roadmap M3.14), checked like a capture's outputPath: absolute on a drive letter, no network
/// or device paths, no wildcards; open needs it to exist; save needs its folder, and an existing file needs overwrite.
/// The files are in a temporary folder of the test's own.
/// </summary>
public sealed class FileDialogPathsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamFileDialogPaths", Guid.NewGuid().ToString("N"));

    public FileDialogPathsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Existing(string name = "report.txt")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Theory]
    [InlineData(@"report.txt")]
    [InlineData(@"..\report.txt")]
    [InlineData(@"\\server\share\report.txt")]
    [InlineData(@"//server/share/report.txt")]
    [InlineData(@"\\?\C:\report.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:report.txt")]
    [InlineData(@"C:\docs\*.txt")]
    [InlineData(@"C:\docs\a?.txt")]
    [InlineData("\"C:\\a.txt\" \"C:\\b.txt\"")]
    [InlineData("")]
    public void APathThatIsNoFullLocalFilePath_IsInvalid(string path)
    {
        var check = FileDialogPaths.Check(path, FileDialogAction.Save, overwrite: true);

        check.FullPath.Should().BeNull();
        check.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public void AnAlternateDataStream_IsInvalid()
    {
        var check = FileDialogPaths.Check(Path.Combine(_folder, "a.txt:hidden"), FileDialogAction.Save, overwrite: false);

        check.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        check.Error.Should().Contain("alternate data stream");
    }

    [Fact]
    public void AnEnvironmentVariable_TheDialogWouldExpand_IsInvalid()
    {
        // The dialog would expand it, so the existence and overwrite checks would be of another path
        var check = FileDialogPaths.Check(Path.Combine(_folder, "%SystemRoot%.txt"), FileDialogAction.Save, overwrite: false);

        check.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        check.Error.Should().Contain("environment variable");

        // A percent sign that names no variable is an ordinary character of a file name
        FileDialogPaths.Check(Path.Combine(_folder, "100% done.txt"), FileDialogAction.Save, overwrite: false).FullPath.Should().NotBeNull();
    }

    [Fact]
    public void Open_OfAnExistingFile_TypesItsFullPath()
    {
        var file = Existing();
        var given = Path.Combine(_folder, "sub", "..", "report.txt");

        var check = FileDialogPaths.Check(given, FileDialogAction.Open, overwrite: false);

        check.FullPath.Should().Be(file);
    }

    [Fact]
    public void Open_OfAFolder_IsAccepted_ForAFolderPicker()
    {
        FileDialogPaths.Check(_folder, FileDialogAction.Open, overwrite: false).FullPath.Should().Be(_folder);
    }

    [Fact]
    public void Open_OfAMissingFile_IsInvalid()
    {
        var check = FileDialogPaths.Check(Path.Combine(_folder, "missing.txt"), FileDialogAction.Open, overwrite: false);

        check.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        check.Error.Should().Contain("does not exist");
    }

    [Fact]
    public void Save_OfANewFileInAnExistingFolder_IsAccepted()
    {
        var path = Path.Combine(_folder, "new.txt");

        FileDialogPaths.Check(path, FileDialogAction.Save, overwrite: false).FullPath.Should().Be(path);
    }

    [Fact]
    public void Save_OfAnExistingFile_WithoutOverwrite_IsFileExists()
    {
        var file = Existing();

        var check = FileDialogPaths.Check(file, FileDialogAction.Save, overwrite: false);

        check.FullPath.Should().BeNull();
        check.ErrorCode.Should().Be(nameof(UIElementErrorCode.FileExists));
        check.Error.Should().Contain("Nothing was typed");
    }

    [Fact]
    public void Save_OfAnExistingFile_WithOverwrite_IsAccepted()
    {
        var file = Existing();

        FileDialogPaths.Check(file, FileDialogAction.Save, overwrite: true).FullPath.Should().Be(file);
    }

    [Fact]
    public void Save_IntoAMissingFolder_OrAsAFolder_IsInvalid()
    {
        FileDialogPaths.Check(Path.Combine(_folder, "missing", "a.txt"), FileDialogAction.Save, overwrite: false).Error
            .Should().Contain("does not exist");
        FileDialogPaths.Check(_folder, FileDialogAction.Save, overwrite: true).Error.Should().Contain("is a folder");
        FileDialogPaths.Check(_folder + Path.DirectorySeparatorChar, FileDialogAction.Save, overwrite: true).Error
            .Should().Contain("is a folder");
    }

    [Fact]
    public void AVeryLongPath_IsInvalid()
    {
        var check = FileDialogPaths.Check(@"C:\" + new string('a', FileDialogPaths.MaxLength), FileDialogAction.Save, overwrite: true);

        check.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }
}
