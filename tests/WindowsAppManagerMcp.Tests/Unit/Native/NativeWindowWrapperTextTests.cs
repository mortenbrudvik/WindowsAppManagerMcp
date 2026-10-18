namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How <see cref="NativeWindowWrapper.GetWindowText"/> turns GetWindowTextW's buffer into a title. GetWindowTextLengthW may
/// report more characters than the title has (Windows documents it for windows that mix ANSI and Unicode), and a title can
/// shrink between the two calls, so the string ends where GetWindowTextW says it stopped copying, not at the length asked
/// for: otherwise "Foo" comes back as "Foo\0\0", which no titleEquals matches and JSON shows as \u0000.
/// </summary>
public class NativeWindowWrapperTextTests
{
    [Fact]
    public void TheTitle_EndsWhereGetWindowTextWStoppedCopying()
    {
        // Arrange - GetWindowTextLengthW said 5, GetWindowTextW copied 3 characters and their NUL
        var buffer = new[] { 'F', 'o', 'o', '\0', '\0', '\0' };

        // Act & Assert
        NativeWindowWrapper.TextFromBuffer(buffer, 3).Should().Be("Foo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NothingCopied_IsNoTitle(int copied)
    {
        NativeWindowWrapper.TextFromBuffer(['F', 'o', 'o', '\0'], copied).Should().BeEmpty();
    }

    [Fact]
    public void ACountLongerThanTheBuffer_IsCutToTheBuffer_AndAtItsNul()
    {
        NativeWindowWrapper.TextFromBuffer(['F', 'o', 'o', '\0'], 40).Should().Be("Foo");
        NativeWindowWrapper.TextFromBuffer(['F', 'o', 'o'], 40).Should().Be("Foo");
    }

    [Fact]
    public void ANulInsideTheCopiedCharacters_EndsTheTitle()
    {
        // A C string ends at its first NUL: what follows it in the buffer is not part of the title
        NativeWindowWrapper.TextFromBuffer(['F', 'o', 'o', '\0', 'x', '\0'], 5).Should().Be("Foo");
    }

    [Fact]
    public void AFullBuffer_IsTheWholeTitle()
    {
        NativeWindowWrapper.TextFromBuffer(['N', 'o', 't', 'e', 's', '\0'], 5).Should().Be("Notes");
    }
}
