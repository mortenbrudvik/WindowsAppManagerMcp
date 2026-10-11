namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The real <see cref="NativeHitTestWrapper"/>'s client area read (scroll's default point). It only reads a window's
/// client rectangle and maps it to the screen, so it runs in the default suite; it is checked on handles that are no
/// window, and on the desktop shell's window, which every interactive session has.
/// </summary>
public class NativeHitTestWrapperTests
{
    private readonly NativeHitTestWrapper _sut = new();

    [Fact]
    public void GetClientAreaOnScreen_OfNoWindow_IsNull()
    {
        _sut.GetClientAreaOnScreen(nint.Zero).Should().BeNull();
        _sut.GetClientAreaOnScreen(0x7FFF_FFF0).Should().BeNull("a handle that is no window has no client area");
    }

    [Fact]
    public void GetClientAreaOnScreen_OfTheShellWindow_IsOnScreen()
    {
        var shell = new ReadOnlyNativeWindowWrapper().GetShellWindow();
        if (shell == nint.Zero)
        {
            return; // no desktop shell in this session (a service or CI runner without Explorer)
        }

        var area = _sut.GetClientAreaOnScreen(shell);

        area.Should().NotBeNull();
        area!.Width.Should().BeGreaterThan(0);
        area.Height.Should().BeGreaterThan(0);
    }
}
