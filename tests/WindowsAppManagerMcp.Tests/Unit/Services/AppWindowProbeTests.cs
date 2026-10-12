namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The new-window probe behind element_action's openedPopup (roadmap M3.6): a before/after comparison of the application's
/// visible top-level windows, through a mocked window wrapper.
/// </summary>
public class AppWindowProbeTests
{
    private const int App = 100;
    private const int Other = 200;

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
        opened.Should().Equal(new OpenedWindow(0x30, "Window 48", "#32770", App, "app"));
    }

    [Fact]
    public void OpenedSince_WithoutProcesses_IsEmpty()
    {
        _desktop.Add((0x10, App, true));
        var sut = new AppWindowProbe(_windows.Object);

        sut.OpenedSince(sut.Snapshot([0])).Should().BeEmpty();
    }

    [Fact]
    public void AFailingEnumeration_IsNoNewWindow()
    {
        var sut = new AppWindowProbe(_windows.Object);
        var before = sut.Snapshot([App]);
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Throws(new InvalidOperationException("gone"));

        sut.OpenedSince(before).Should().BeEmpty();
    }
}
