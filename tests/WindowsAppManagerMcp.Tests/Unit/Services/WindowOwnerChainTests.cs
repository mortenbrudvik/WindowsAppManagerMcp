namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="WindowOwnerChain"/>, the one owner rule that the close guard, the host guard
/// (and so WindowInfo.IsAgentHost) and the input guard share. GA_ROOTOWNER walks the chain GetParent
/// returns, which follows the owner only for WS_POPUP windows, so an owned overlapped window (a WPF or
/// WinForms dialog) is linked to its owner only through GW_OWNER. The window wrapper is a mock.
/// </summary>
public class WindowOwnerChainTests
{
    private const uint GwOwner = 4;

    private static readonly nint Main = 0x100;
    private static readonly nint Win32Dialog = 0x110;   // WS_POPUP, owned by Main: GA_ROOTOWNER is Main
    private static readonly nint WpfDialog = 0x120;     // overlapped, owned by Main: GA_ROOTOWNER is itself
    private static readonly nint DialogOfDialog = 0x130; // overlapped, owned by WpfDialog
    private static readonly nint Child = 0x140;         // a child control of WpfDialog
    private static readonly nint Unrelated = 0x200;

    private readonly Mock<INativeWindowWrapper> _windows = new();

    public WindowOwnerChainTests()
    {
        Window(Main);
        Window(Win32Dialog, owner: Main, rootOwner: Main);
        Window(WpfDialog, owner: Main);
        Window(DialogOfDialog, owner: WpfDialog);
        Window(Unrelated);
        _windows.Setup(w => w.GetAncestor(Child, WindowAncestor.Root)).Returns(WpfDialog);
        _windows.Setup(w => w.GetAncestor(Child, WindowAncestor.RootOwner)).Returns(WpfDialog);
    }

    private void Window(nint handle, nint owner = 0, nint rootOwner = 0)
    {
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.Root)).Returns(handle);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.RootOwner)).Returns(rootOwner == 0 ? handle : rootOwner);
        _windows.Setup(w => w.GetWindow(handle, GwOwner)).Returns(owner);
    }

    [Fact]
    public void SelfAndOwners_UnownedWindow_IsJustTheWindow()
    {
        WindowOwnerChain.SelfAndOwners(_windows.Object, Main).Should().Equal(Main);
    }

    [Fact]
    public void SelfAndOwners_Win32Dialog_ReachesItsOwner()
    {
        WindowOwnerChain.SelfAndOwners(_windows.Object, Win32Dialog).Should().Equal(Win32Dialog, Main);
    }

    [Fact]
    public void SelfAndOwners_OwnedOverlappedWindow_ReachesItsOwnerThroughGwOwner()
    {
        // GA_ROOTOWNER of the WPF dialog is the dialog itself; only GW_OWNER links it to Main
        WindowOwnerChain.SelfAndOwners(_windows.Object, WpfDialog).Should().Equal(WpfDialog, Main);
        WindowOwnerChain.SelfAndOwners(_windows.Object, DialogOfDialog).Should().Equal(DialogOfDialog, WpfDialog, Main);
    }

    [Fact]
    public void SelfAndOwners_ChildWindow_StartsAtTheChildThenItsTopLevelWindowAndItsOwners()
    {
        WindowOwnerChain.SelfAndOwners(_windows.Object, Child).Should().Equal(Child, WpfDialog, Main);
    }

    [Fact]
    public void SelfAndOwners_RootOwnerKnownButNoGwOwner_StillIncludesTheRootOwner()
    {
        // Some mocks and some windows answer GA_ROOTOWNER only; the chain continues from there
        var popup = new nint(0x150);
        _windows.Setup(w => w.GetAncestor(popup, WindowAncestor.Root)).Returns(popup);
        _windows.Setup(w => w.GetAncestor(popup, WindowAncestor.RootOwner)).Returns(WpfDialog);

        WindowOwnerChain.SelfAndOwners(_windows.Object, popup).Should().Equal(popup, WpfDialog, Main);
    }

    [Fact]
    public void SelfAndOwners_ZeroHandle_IsEmpty()
    {
        WindowOwnerChain.SelfAndOwners(_windows.Object, nint.Zero).Should().BeEmpty();
    }

    [Fact]
    public void SelfAndOwners_OwnerCycle_StopsWithoutRepeating()
    {
        // A corrupt chain must not loop
        var a = new nint(0x300);
        var b = new nint(0x310);
        Window(a, owner: b);
        Window(b, owner: a);

        WindowOwnerChain.SelfAndOwners(_windows.Object, a).Should().Equal(a, b);
    }

    [Fact]
    public void SelfAndOwners_EndlessChain_StopsAtTheLimit()
    {
        var first = new nint(0x10000);
        for (var i = 0; i < WindowOwnerChain.MaxOwners + 10; i++)
        {
            Window(first + i, owner: first + i + 1);
        }

        WindowOwnerChain.SelfAndOwners(_windows.Object, first).Should().HaveCount(WindowOwnerChain.MaxOwners + 1);
    }

    [Fact]
    public void IsOwnedBy_WindowItselfAndEveryOwner_IsTrue()
    {
        WindowOwnerChain.IsOwnedBy(_windows.Object, DialogOfDialog, DialogOfDialog).Should().BeTrue();
        WindowOwnerChain.IsOwnedBy(_windows.Object, DialogOfDialog, WpfDialog).Should().BeTrue();
        WindowOwnerChain.IsOwnedBy(_windows.Object, DialogOfDialog, Main).Should().BeTrue();
    }

    [Fact]
    public void IsOwnedBy_OwnedWindowsAndUnrelatedWindows_AreNotOwners()
    {
        // The walk goes up only: an owner is never owned by its dialog, and siblings are unrelated
        WindowOwnerChain.IsOwnedBy(_windows.Object, Main, WpfDialog).Should().BeFalse();
        WindowOwnerChain.IsOwnedBy(_windows.Object, Win32Dialog, WpfDialog).Should().BeFalse();
        WindowOwnerChain.IsOwnedBy(_windows.Object, Unrelated, Main).Should().BeFalse();
        WindowOwnerChain.IsOwnedBy(_windows.Object, Main, nint.Zero).Should().BeFalse();
    }

    [Fact]
    public void TopOwner_IsTheLastWindowOfTheChain()
    {
        WindowOwnerChain.TopOwner(_windows.Object, DialogOfDialog).Should().Be(Main);
        WindowOwnerChain.TopOwner(_windows.Object, Child).Should().Be(Main);
        WindowOwnerChain.TopOwner(_windows.Object, Unrelated).Should().Be(Unrelated);
    }

    [Fact]
    public void Walk_OnlyReadsWindows()
    {
        // Arrange - a strict mock throws on anything but the reads set up here
        var strict = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        strict.Setup(w => w.GetAncestor(It.IsAny<nint>(), It.IsAny<WindowAncestor>())).Returns<nint, WindowAncestor>((h, _) => h);
        strict.Setup(w => w.GetWindow(It.IsAny<nint>(), GwOwner)).Returns(nint.Zero);

        // Act & Assert
        WindowOwnerChain.SelfAndOwners(strict.Object, Main).Should().Equal(Main);
    }
}
