namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The new-window probe behind element_action's openedPopup (roadmap M3.6): a before/after comparison of the application's
/// visible top-level windows, through a mocked window wrapper.
/// </summary>
public class AppWindowProbeTests
{
    private const int App = 100;
    private const int Other = 200;

    // Win32 values (NativeEnums is internal to the server assembly)
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const long WsCaption = 0x00C00000;
    private const long WsDisabled = 0x08000000;
    private const long WsPopup = 0x80000000;
    private const long WsExToolWindow = 0x00000080;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly List<(nint Handle, int Process, bool Visible)> _desktop = [];

    public AppWindowProbeTests()
    {
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> callback) =>
        {
            foreach (var window in _desktop.ToList())
            {
                if (!callback(window.Handle))
                {
                    break;
                }
            }
        });
        _windows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns((nint h) => (uint)_desktop.FirstOrDefault(w => w.Handle == h).Process);
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _desktop.FirstOrDefault(w => w.Handle == h).Visible);
        _windows.Setup(w => w.GetWindowText(It.IsAny<nint>())).Returns((nint h) => $"Window {h}");
        _windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns("#32770");
        _windows.Setup(w => w.GetProcessName((uint)App)).Returns("app");
    }

    [Fact]
    public void OpenedSince_ReturnsTheApplicationsNewVisibleWindows_TopmostFirst()
    {
        // Arrange
        _desktop.Add((0x10, App, true));
        _desktop.Add((0x20, Other, true));
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);

        // Act - a dialog of the app opens on top; a hidden window of it and another app's window appear too
        _desktop.Insert(0, (0x30, App, true));
        _desktop.Add((0x40, App, false));
        _desktop.Add((0x50, Other, true));
        var opened = sut.OpenedSince(before);

        // Assert
        opened.All.Should().Equal(new OpenedWindow(0x30, "Window 48", "#32770", App, "app"));
    }

    [Fact]
    public void OpenedSince_LeavesOutTooltips_AndPutsADialogFirst_EvenBelowATopmostWindow()
    {
        // Arrange - the action opens a dialog; a tooltip, and a hover card with no title bar, show above it
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _desktop.Add((0x10, App, true));
        _desktop.Add((0x20, App, true));
        _desktop.Add((0x30, App, true));
        Class(0x10, "tooltips_class32");
        Class(0x20, "Chrome_WidgetWin_1", style: WsPopup);
        Class(0x30, "#32770", style: WsPopup | WsCaption);

        // Act
        var opened = sut.OpenedSince(before);

        // Assert
        opened.Dialogs.Select(w => w.Handle).Should().Equal(0x30);
        opened.Others.Select(w => w.Handle).Should().Equal(0x20);
        opened.All.Select(w => w.Handle).Should().Equal(0x30, 0x20);
    }

    [Fact]
    public void OpenedSince_LeavesOutWindowsWithoutASize_DisabledOnes_AndCloakedOnes()
    {
        // Arrange
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _desktop.Add((0x10, App, true));
        _desktop.Add((0x20, App, true));
        _desktop.Add((0x30, App, true));
        _desktop.Add((0x40, App, true));
        _windows.Setup(w => w.GetWindowRect(0x10)).Returns((100, 100, 100, 100));
        Class(0x20, "#32770", style: WsCaption | WsDisabled);
        _windows.Setup(w => w.IsCloaked(0x30)).Returns(true);

        // Act
        var opened = sut.OpenedSince(before);

        // Assert
        opened.All.Select(w => w.Handle).Should().Equal(0x40);
    }

    [Theory]
    [InlineData("#32770", 0L, 0L, false)]                         // a dialog box
    [InlineData("WindowsForms10.Window", WsCaption, 0L, false)]   // a window with a title bar
    [InlineData("HwndWrapper", WsPopup, 0L, true)]                // a window whose owner it disabled: a modal dialog
    public void AWindowThatCanBeADialog_IsADialog(string className, long style, long exStyle, bool ownerDisabled)
    {
        // Arrange
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _desktop.Add((0x10, App, true));
        _desktop.Add((0x30, App, true));
        Class(0x30, className, style, exStyle);
        _windows.Setup(w => w.GetWindow(0x30, GwOwner)).Returns(0x10);
        Class(0x10, "MainWindow", ownerDisabled ? WsCaption | WsDisabled : WsCaption);
        before = new AppWindowSnapshot(before.ProcessIds, new HashSet<nint> { 0x10 });

        // Act
        var opened = sut.OpenedSince(before);

        // Assert
        opened.Dialogs.Select(w => w.Handle).Should().Equal(0x30);
    }

    [Fact]
    public void AToolWindowWithATitleBar_OrAPopupWhoseOwnerIsEnabled_IsNoDialog()
    {
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _desktop.Add((0x20, App, true));
        _desktop.Add((0x30, App, true));
        Class(0x20, "Palette", WsCaption, WsExToolWindow);
        Class(0x30, "ComboLBox", WsPopup);
        _windows.Setup(w => w.GetWindow(0x30, GwOwner)).Returns(0x20);

        var opened = sut.OpenedSince(before);

        opened.Dialogs.Should().BeEmpty();
        opened.Others.Select(w => w.Handle).Should().Equal(0x20, 0x30);
    }

    private void Class(nint handle, string className, long style = 0, long exStyle = 0)
    {
        _windows.Setup(w => w.GetClassName(handle)).Returns(className);
        _windows.Setup(w => w.GetWindowLongPtr(handle, GwlStyle)).Returns(new nint(style));
        _windows.Setup(w => w.GetWindowLongPtr(handle, GwlExStyle)).Returns(new nint(exStyle));
    }

    [Fact]
    public void OpenedSince_WithoutProcesses_IsEmpty()
    {
        _desktop.Add((0x10, App, true));
        var sut = new AppWindowProbe(_windows.Object);

        sut.OpenedSince(sut.Snapshot([0])).All.Should().BeEmpty();
    }

    [Fact]
    public void AFailingEnumeration_IsNoNewWindow()
    {
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Throws(new InvalidOperationException("gone"));

        sut.OpenedSince(before).All.Should().BeEmpty();
    }
}
