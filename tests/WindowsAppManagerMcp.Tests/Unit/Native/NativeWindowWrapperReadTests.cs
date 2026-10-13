namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The window identity reads of the real <see cref="NativeWindowWrapper"/>. They go through
/// <see cref="ReadOnlyNativeWindowWrapper"/>, so nothing here can change a window; they only read.
/// </summary>
public class NativeWindowWrapperReadTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;

    /// <summary>A handle no window has (the same value the WindowService tests use).</summary>
    private static readonly nint InvalidHandle = new(99999999);

    private readonly INativeWindowWrapper _sut = new ReadOnlyNativeWindowWrapper();

    [Fact]
    public void Reads_OfAHandleThatIsNotAWindow_ReturnTheDocumentedEmptyValues()
    {
        var children = new List<nint>();

        _sut.EnumChildWindows(InvalidHandle, child => { children.Add(child); return true; });
        _sut.EnumChildWindows(nint.Zero, child => { children.Add(child); return true; });

        children.Should().BeEmpty("a zero parent must not list the top-level windows");
        _sut.GetClassName(InvalidHandle).Should().BeEmpty();
        _sut.GetWindowLongPtr(InvalidHandle, GwlExStyle).Should().Be(nint.Zero);
        _sut.GetWindow(InvalidHandle, GwOwner).Should().Be(nint.Zero);
        _sut.IsCloaked(InvalidHandle).Should().BeFalse();
        _sut.GetExtendedFrameBounds(InvalidHandle).Should().BeNull();
        _sut.IsHungAppWindow(InvalidHandle).Should().BeFalse();
        _sut.GetDpiForWindow(InvalidHandle).Should().Be(0u);
        _sut.RespondsWithin(InvalidHandle, 100u).Should().BeNull("SendMessageTimeout fails for a handle that is not a window, which says nothing about a thread");
    }

    [Fact]
    public void Reads_OfAVisibleWindow_ReturnItsClassDpiAndFrame()
    {
        // Arrange - any visible top-level window of the desktop
        var handles = new List<nint>();
        _sut.EnumWindows(h => { handles.Add(h); return true; });
        var window = handles.FirstOrDefault(_sut.IsWindowVisible);
        if (window == nint.Zero)
        {
            return; // no visible window in this session
        }

        // Act
        var className = _sut.GetClassName(window);
        var dpi = _sut.GetDpiForWindow(window);
        var frame = _sut.GetExtendedFrameBounds(window);

        // Assert
        className.Should().NotBeEmpty("every window has a class");
        dpi.Should().BeGreaterThanOrEqualTo(96u);
        frame.Should().NotBeNull();
        frame!.Value.Right.Should().BeGreaterThanOrEqualTo(frame.Value.Left);
        frame.Value.Bottom.Should().BeGreaterThanOrEqualTo(frame.Value.Top);
    }
}
