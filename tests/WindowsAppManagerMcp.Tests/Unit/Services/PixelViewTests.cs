using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The lent pixels of IScreenCaptureWrapper.WithPixels (roadmap M6, polling without copies): rows a stride apart, crops
/// that share the memory, and copies only when asked for. Pure; no screen is read.
/// </summary>
public class PixelViewTests
{
    /// <summary>A 4x3 capture at (10, 20) whose pixel (x, y) has blue x, green y and red 100 + x + y.</summary>
    private static CaptureResult Capture()
    {
        var pixels = new byte[4 * 3 * 4];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(100 + x + y);
                pixels[i + 3] = 0xEE;
            }
        }

        return new CaptureResult(pixels, 4, 3, 10, 20, "PrintWindow");
    }

    [Fact]
    public void Of_LendsTheCapturesOwnArray()
    {
        // Arrange
        var capture = Capture();

        // Act
        var view = PixelView.Of(capture);

        // Assert
        view.Width.Should().Be(4);
        view.Height.Should().Be(3);
        view.Stride.Should().Be(16);
        view.ScreenOriginX.Should().Be(10);
        view.ScreenOriginY.Should().Be(20);
        view.Method.Should().Be("PrintWindow");
        Unsafe.AreSame(ref MemoryMarshal.GetReference(view.Bgra), ref capture.Bgra[0]).Should().BeTrue();
    }

    [Fact]
    public void Row_IsOneRowOfPixels()
    {
        var row = PixelView.Of(Capture()).Row(2).ToArray();

        row.Should().HaveCount(16);
        row[0..3].Should().Equal(0, 2, 102);
        row[12..15].Should().Equal(3, 2, 105);
    }

    [Fact]
    public void Crop_SharesTheMemory_AndKeepsTheStride()
    {
        // Arrange
        var capture = Capture();

        // Act - screen (11, 21) is pixel (1, 1)
        var crop = PixelView.Of(capture).Crop(11, 21, 2, 2);

        // Assert
        crop.Width.Should().Be(2);
        crop.Height.Should().Be(2);
        crop.Stride.Should().Be(16);
        crop.ScreenOriginX.Should().Be(11);
        crop.ScreenOriginY.Should().Be(21);
        crop.Method.Should().Be("PrintWindow");
        Unsafe.AreSame(ref MemoryMarshal.GetReference(crop.Bgra), ref capture.Bgra[(1 * 4 + 1) * 4]).Should().BeTrue();
        crop.Bgra.Length.Should().Be(16 + 8, "the last row ends at the crop's right edge");
    }

    [Fact]
    public void ToArray_OfACrop_PacksItsRows()
    {
        // Act
        var copy = PixelView.Of(Capture()).Crop(12, 20, 2, 3).ToCaptureResult();

        // Assert - pixels (2..3, 0..2), 2 * 4 bytes per row
        copy.Width.Should().Be(2);
        copy.Height.Should().Be(3);
        copy.ScreenOriginX.Should().Be(12);
        copy.ScreenOriginY.Should().Be(20);
        copy.Bgra.Should().HaveCount(2 * 3 * 4);
        copy.Bgra[0..3].Should().Equal(2, 0, 102);
        copy.Bgra[^4..^1].Should().Equal(3, 2, 105);
    }

    [Fact]
    public void ToArray_IsACopy()
    {
        // Arrange
        var capture = Capture();

        // Act
        var copy = PixelView.Of(capture).ToArray();

        // Assert
        copy.Should().Equal(capture.Bgra);
        copy.Should().NotBeSameAs(capture.Bgra);
    }

    [Theory]
    [InlineData(9, 20, 2, 2)]
    [InlineData(10, 19, 2, 2)]
    [InlineData(13, 20, 2, 1)]
    [InlineData(10, 22, 1, 2)]
    [InlineData(10, 20, -1, 1)]
    public void Crop_OutsideTheView_IsRefused(int x, int y, int width, int height)
    {
        var capture = Capture();

        Action act = () => PixelView.Of(capture).Crop(x, y, width, height);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RefusesAStrideShorterThanARow_OrABufferShorterThanTheRows()
    {
        var pixels = new byte[60];

        // A ref struct cannot be a lambda's return type argument, so the lambdas are actions
        Action shortStride = () => new PixelView(pixels, 4, 3, 12, 0, 0, "ScreenBitBlt");
        Action shortBuffer = () => new PixelView(pixels.AsSpan(0, 2 * 16 + 15), 4, 3, 16, 0, 0, "ScreenBitBlt");

        shortStride.Should().Throw<ArgumentOutOfRangeException>();
        shortBuffer.Should().Throw<ArgumentOutOfRangeException>();

        // Rows 20 bytes apart: the last row needs only its 16 bytes
        new PixelView(pixels.AsSpan(0, 2 * 20 + 16), 4, 3, 20, 0, 0, "ScreenBitBlt").Height.Should().Be(3);
    }
}
