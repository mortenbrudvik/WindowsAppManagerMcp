namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Pixels for the visual change tests (roadmap M6.4): a capture of one color with rectangles painted on it, built so a test
/// says only what differs between two frames. The 4th byte of every pixel is random, as GDI leaves it.
/// </summary>
public sealed class VisualFrames
{
    private readonly byte[] _bgra;
    private static readonly Random Undefined = new(4);

    /// <summary>A <paramref name="width"/> x <paramref name="height"/> frame at (<paramref name="originX"/>, <paramref name="originY"/>) of one color.</summary>
    public VisualFrames(int width, int height, int originX = 0, int originY = 0, (byte R, byte G, byte B)? color = null)
    {
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
        _bgra = new byte[width * height * 4];
        Paint(0, 0, width, height, color ?? (0xF0, 0xF0, 0xF0));
    }

    public int Width { get; }
    public int Height { get; }
    public int OriginX { get; }
    public int OriginY { get; }

    /// <summary>Paints the image rectangle (x, y, width, height) in <paramref name="color"/>.</summary>
    public VisualFrames Paint(int x, int y, int width, int height, (byte R, byte G, byte B) color)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var i = (row * Width + column) * 4;
                _bgra[i] = color.B;
                _bgra[i + 1] = color.G;
                _bgra[i + 2] = color.R;
                _bgra[i + 3] = (byte)Undefined.Next(256);
            }
        }

        return this;
    }

    /// <summary>A copy of the frame, to paint a second frame from the first.</summary>
    public VisualFrames Copy(int? originX = null, int? originY = null)
    {
        var copy = new VisualFrames(Width, Height, originX ?? OriginX, originY ?? OriginY);
        _bgra.CopyTo(copy._bgra, 0);
        return copy;
    }

    /// <summary>The pixels as the capture wrapper returns them.</summary>
    public CaptureResult Capture(string method = "PrintWindow") => new((byte[])_bgra.Clone(), Width, Height, OriginX, OriginY, method);

    /// <summary>The frame's signature, as read by <paramref name="method"/>.</summary>
    public VisualSignature Signature(string method = "PrintWindow") => VisualSignature.Of(PixelView.Of(Capture(method)));
}
