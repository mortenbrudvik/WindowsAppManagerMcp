namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="ReadOnlyNativeWindowWrapper"/> must forward every query and refuse every window change
/// before it reaches the wrapped instance. The wrapped instance here is a strict mock, so nothing
/// touches a real window.
/// </summary>
public class ReadOnlyNativeWindowWrapperTests
{
    private static readonly nint Handle = new(4242);
    private static readonly nint ChildHandle = new(4343);

    public static TheoryData<string, Action<INativeWindowWrapper>> WindowChanges() => new()
    {
        { nameof(INativeWindowWrapper.SetWindowPos), w => w.SetWindowPos(Handle, 0, 0, 100, 100, 0) },
        { nameof(INativeWindowWrapper.SetWindowZOrder), w => w.SetWindowZOrder(Handle, 0, 0) },
        { nameof(INativeWindowWrapper.ShowWindow), w => w.ShowWindow(Handle, 0) },
        {
            nameof(INativeWindowWrapper.SetWindowPlacement),
            w => w.SetWindowPlacement(Handle, new NativeWindowPlacement(7, 0, (0, 0), (0, 0), (0, 0, 100, 100)))
        },
        { nameof(INativeWindowWrapper.SetForegroundWindow), w => w.SetForegroundWindow(Handle) },
        { nameof(INativeWindowWrapper.BringWindowToTop), w => w.BringWindowToTop(Handle) },
        { nameof(INativeWindowWrapper.PostMessage), w => w.PostMessage(Handle, 0x0010, nint.Zero, nint.Zero) },
    };

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_ThrowsWithoutCallingTheWrappedInstance(string method, Action<INativeWindowWrapper> change)
    {
        // Arrange
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        var sut = new ReadOnlyNativeWindowWrapper(inner.Object);

        // Act
        var act = () => change(sut);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"{method} would change a real window*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void Queries_AreForwardedToTheWrappedInstance()
    {
        // Arrange
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback<Func<nint, bool>>(cb => cb(Handle));
        inner.Setup(w => w.IsWindowVisible(Handle)).Returns(true);
        inner.Setup(w => w.IsWindow(Handle)).Returns(true);
        inner.Setup(w => w.IsIconic(Handle)).Returns(true);
        inner.Setup(w => w.IsZoomed(Handle)).Returns(true);
        inner.Setup(w => w.GetWindowText(Handle)).Returns("Title");
        inner.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(77u);
        inner.Setup(w => w.GetWindowThreadId(Handle)).Returns(7700u);
        inner.Setup(w => w.GetWindowRect(Handle)).Returns((1, 2, 3, 4));
        inner.Setup(w => w.GetForegroundWindow()).Returns(Handle);
        inner.Setup(w => w.GetAncestor(Handle, WindowAncestor.RootOwner)).Returns(new nint(4343));
        inner.Setup(w => w.GetShellWindow()).Returns(new nint(4444));
        inner.Setup(w => w.GetCurrentProcessId()).Returns(88u);
        inner.Setup(w => w.GetProcessName(77u)).Returns("app");
        inner.Setup(w => w.EnumChildWindows(Handle, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, cb) => cb(ChildHandle));
        inner.Setup(w => w.GetClassName(Handle)).Returns("Notepad");
        inner.Setup(w => w.GetWindowLongPtr(Handle, -20)).Returns(new nint(0x80));
        inner.Setup(w => w.GetWindow(Handle, 4u)).Returns(ChildHandle);
        inner.Setup(w => w.IsCloaked(Handle)).Returns(true);
        inner.Setup(w => w.GetWindowDisplayAffinity(Handle)).Returns(0x11u);
        inner.Setup(w => w.GetExtendedFrameBounds(Handle)).Returns((5, 6, 7, 8));
        inner.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);
        inner.Setup(w => w.GetDpiForWindow(Handle)).Returns(144u);
        inner.Setup(w => w.RespondsWithin(Handle, 500u)).Returns(true);
        var placement = new NativeWindowPlacement(2, 0, (-32000, -32000), (-1, -1), (10, 20, 300, 400));
        inner.Setup(w => w.GetWindowPlacement(Handle)).Returns(placement);
        var sut = new ReadOnlyNativeWindowWrapper(inner.Object);
        var enumerated = new List<nint>();
        var children = new List<nint>();

        // Act
        sut.EnumWindows(h => { enumerated.Add(h); return true; });
        sut.EnumChildWindows(Handle, h => { children.Add(h); return true; });

        // Assert
        enumerated.Should().Equal(Handle);
        children.Should().Equal(ChildHandle);
        sut.GetClassName(Handle).Should().Be("Notepad");
        sut.GetWindowLongPtr(Handle, -20).Should().Be(new nint(0x80));
        sut.GetWindow(Handle, 4u).Should().Be(ChildHandle);
        sut.IsCloaked(Handle).Should().BeTrue();
        sut.GetWindowDisplayAffinity(Handle).Should().Be(0x11u);
        sut.GetExtendedFrameBounds(Handle).Should().Be((5, 6, 7, 8));
        sut.IsHungAppWindow(Handle).Should().BeTrue();
        sut.GetDpiForWindow(Handle).Should().Be(144u);
        sut.RespondsWithin(Handle, 500u).Should().BeTrue();
        sut.GetWindowPlacement(Handle).Should().Be(placement);
        sut.IsWindowVisible(Handle).Should().BeTrue();
        sut.IsWindow(Handle).Should().BeTrue();
        sut.IsIconic(Handle).Should().BeTrue();
        sut.IsZoomed(Handle).Should().BeTrue();
        sut.GetWindowText(Handle).Should().Be("Title");
        sut.GetWindowThreadProcessId(Handle).Should().Be(77u);
        sut.GetWindowThreadId(Handle).Should().Be(7700u);
        sut.GetWindowRect(Handle).Should().Be((1, 2, 3, 4));
        sut.GetForegroundWindow().Should().Be(Handle);
        sut.GetAncestor(Handle, WindowAncestor.RootOwner).Should().Be(new nint(4343));
        sut.GetShellWindow().Should().Be(new nint(4444));
        sut.GetCurrentProcessId().Should().Be(88u);
        sut.GetProcessName(77u).Should().Be("app");
    }

    [Fact]
    public void EveryInterfaceMember_IsClassifiedAsQueryOrWindowChange()
    {
        // The theory above covers the window changes and the fact above the queries. A new member of
        // INativeWindowWrapper must be added to one of them (and to the wrapper, which won't compile
        // without it).
        string[] queries =
        [
            nameof(INativeWindowWrapper.EnumWindows), nameof(INativeWindowWrapper.IsWindowVisible),
            nameof(INativeWindowWrapper.IsWindow), nameof(INativeWindowWrapper.IsIconic),
            nameof(INativeWindowWrapper.IsZoomed), nameof(INativeWindowWrapper.GetWindowText),
            nameof(INativeWindowWrapper.GetWindowThreadProcessId), nameof(INativeWindowWrapper.GetWindowThreadId),
            nameof(INativeWindowWrapper.GetWindowRect),
            nameof(INativeWindowWrapper.GetForegroundWindow), nameof(INativeWindowWrapper.GetAncestor),
            nameof(INativeWindowWrapper.GetShellWindow), nameof(INativeWindowWrapper.GetCurrentProcessId),
            nameof(INativeWindowWrapper.GetProcessName),
            nameof(INativeWindowWrapper.EnumChildWindows), nameof(INativeWindowWrapper.GetClassName),
            nameof(INativeWindowWrapper.GetWindowLongPtr), nameof(INativeWindowWrapper.GetWindow),
            nameof(INativeWindowWrapper.IsCloaked), nameof(INativeWindowWrapper.GetCloakReason), nameof(INativeWindowWrapper.HasWindowRegion),
            nameof(INativeWindowWrapper.GetWindowDisplayAffinity), nameof(INativeWindowWrapper.GetExtendedFrameBounds),
            nameof(INativeWindowWrapper.IsHungAppWindow), nameof(INativeWindowWrapper.GetDpiForWindow),
            nameof(INativeWindowWrapper.RespondsWithin), nameof(INativeWindowWrapper.GetWindowPlacement),
        ];
        var changes = WindowChanges().Select(row => (string)row[0]);

        typeof(INativeWindowWrapper).GetMethods().Select(m => m.Name)
            .Should().BeEquivalentTo(queries.Concat(changes));
    }
}
