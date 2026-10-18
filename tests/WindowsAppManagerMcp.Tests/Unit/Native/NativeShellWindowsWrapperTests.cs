namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How NativeShellWindowsWrapper collects the shell's windows (roadmap M7.5): a window that fails to read is skipped and
/// counted, never dropped silently, so save_workspace can say a folder was not kept and restore_workspace that one may
/// open twice. Pure: the reads are functions, no shell is asked.
/// </summary>
public class NativeShellWindowsWrapperTests
{
    [Fact]
    public void Collect_KeepsEveryWindowRead_InOrder()
    {
        var listing = NativeShellWindowsWrapper.Collect(2, i => new ExplorerFolder(0x100 + i, $@"C:\Folder{i}"));

        listing.Folders.Should().Equal(new ExplorerFolder(0x100, @"C:\Folder0"), new ExplorerFolder(0x101, @"C:\Folder1"));
        listing.SkippedWindows.Should().Be(0);
    }

    [Fact]
    public void Collect_AWindowThatFailsToRead_IsSkippedAndCounted()
    {
        // Arrange - the second window's read throws, as a COM call into a window that is closing does
        var listing = NativeShellWindowsWrapper.Collect(3, i => i == 1
            ? throw new System.Runtime.InteropServices.COMException("The object invoked has disconnected from its clients.")
            : new ExplorerFolder(0x100 + i, null));

        // Assert
        listing.Folders.Select(f => f.Window).Should().Equal(0x100, 0x102);
        listing.SkippedWindows.Should().Be(1);
    }

    [Fact]
    public void Collect_AnEmptySlot_IsNeitherAWindowNorSkipped()
    {
        // Arrange - the collection shrank while it was read: the slot is gone, nothing was lost
        var listing = NativeShellWindowsWrapper.Collect(2, i => i == 0 ? null : new ExplorerFolder(0x101, null));

        // Assert
        listing.Folders.Should().ContainSingle();
        listing.SkippedWindows.Should().Be(0);
    }
}
