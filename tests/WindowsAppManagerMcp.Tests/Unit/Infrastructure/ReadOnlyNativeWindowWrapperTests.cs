namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="ReadOnlyNativeWindowWrapper"/> must forward every query and refuse every window change
/// before it reaches the wrapped instance. The wrapped instance here is a strict mock, so nothing
/// touches a real window.
/// </summary>
public class ReadOnlyNativeWindowWrapperTests
{
    private static readonly nint Handle = new(4242);

    public static TheoryData<string, Action<INativeWindowWrapper>> WindowChanges() => new()
    {
        { nameof(INativeWindowWrapper.SetWindowPos), w => w.SetWindowPos(Handle, 0, 0, 100, 100, 0) },
        { nameof(INativeWindowWrapper.ShowWindow), w => w.ShowWindow(Handle, 0) },
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
        inner.Setup(w => w.GetWindowRect(Handle)).Returns((1, 2, 3, 4));
        inner.Setup(w => w.GetForegroundWindow()).Returns(Handle);
        inner.Setup(w => w.GetAncestor(Handle, WindowAncestor.RootOwner)).Returns(new nint(4343));
        inner.Setup(w => w.GetShellWindow()).Returns(new nint(4444));
        inner.Setup(w => w.GetCurrentProcessId()).Returns(88u);
        inner.Setup(w => w.GetProcessName(77u)).Returns("app");
        var sut = new ReadOnlyNativeWindowWrapper(inner.Object);
        var enumerated = new List<nint>();

        // Act
        sut.EnumWindows(h => { enumerated.Add(h); return true; });

        // Assert
        enumerated.Should().Equal(Handle);
        sut.IsWindowVisible(Handle).Should().BeTrue();
        sut.IsWindow(Handle).Should().BeTrue();
        sut.IsIconic(Handle).Should().BeTrue();
        sut.IsZoomed(Handle).Should().BeTrue();
        sut.GetWindowText(Handle).Should().Be("Title");
        sut.GetWindowThreadProcessId(Handle).Should().Be(77u);
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
            nameof(INativeWindowWrapper.GetWindowThreadProcessId), nameof(INativeWindowWrapper.GetWindowRect),
            nameof(INativeWindowWrapper.GetForegroundWindow), nameof(INativeWindowWrapper.GetAncestor),
            nameof(INativeWindowWrapper.GetShellWindow), nameof(INativeWindowWrapper.GetCurrentProcessId),
            nameof(INativeWindowWrapper.GetProcessName),
        ];
        var changes = WindowChanges().Select(row => (string)row[0]);

        typeof(INativeWindowWrapper).GetMethods().Select(m => m.Name)
            .Should().BeEquivalentTo(queries.Concat(changes));
    }
}
