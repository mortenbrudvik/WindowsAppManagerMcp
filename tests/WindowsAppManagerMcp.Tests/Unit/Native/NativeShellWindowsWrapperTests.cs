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

    public static TheoryData<string, Exception> NoFolderViews() => new()
    {
        { "a document without a Folder member", new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException("'System.__ComObject' does not contain a definition for 'Folder'") },
        { "an unknown name (DISP_E_UNKNOWNNAME)", new System.Runtime.InteropServices.COMException("Unknown name.", unchecked((int)0x80020006)) },
        { "a member not found (DISP_E_MEMBERNOTFOUND)", new System.Runtime.InteropServices.COMException("Member not found.", unchecked((int)0x80020003)) },
        { "not implemented (E_NOTIMPL)", new System.Runtime.InteropServices.COMException("Not implemented.", unchecked((int)0x80004001)) },
        { "no such interface (E_NOINTERFACE)", new System.Runtime.InteropServices.COMException("No such interface.", unchecked((int)0x80004002)) },
    };

    [Theory]
    [MemberData(nameof(NoFolderViews))]
    public void AFolderReadThatFindsNoFolderView_MeansTheWindowShowsNoFolder(string why, Exception failure)
    {
        // A shell window that shows no folder (a web page in an old browser window) has no Folder to read: not a failure
        NativeShellWindowsWrapper.IsNoFolderView(failure).Should().BeTrue(why);
    }

    public static TheoryData<string, Exception> FailedFolderReads() => new()
    {
        { "the window went away (RPC_E_DISCONNECTED)", new System.Runtime.InteropServices.COMException("Disconnected.", unchecked((int)0x80010108)) },
        { "the shell failed (RPC_E_SERVERFAULT)", new System.Runtime.InteropServices.COMException("The server threw an exception.", unchecked((int)0x80010105)) },
        { "the shell failed (E_FAIL)", new System.Runtime.InteropServices.COMException("Unspecified error.", unchecked((int)0x80004005)) },
        { "a path of the wrong kind", new InvalidCastException() },
    };

    [Theory]
    [MemberData(nameof(FailedFolderReads))]
    public void AFolderReadThatFails_IsAWindowThatCouldNotBeRead_SoItIsCounted(string why, Exception failure)
    {
        // The window may show a folder on disk that is then missing from save_workspace: Collect counts it as skipped
        NativeShellWindowsWrapper.IsNoFolderView(failure).Should().BeFalse(why);
    }
}
