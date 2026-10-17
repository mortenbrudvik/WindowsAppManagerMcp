using System.Buffers.Binary;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What a capture looked like, without its pixels (roadmap M6.4): a 64-bit hash of every <see cref="TileSize"/> x
/// <see cref="TileSize"/> tile of the image, row by row, and where the image was on the screen. Two signatures of the same
/// size tell which tiles differ (<see cref="Services.VisualDiff"/>): any change of a pixel's blue, green or red, a change of
/// hue that keeps the brightness included, changes its tile's hash. The 4th byte of each pixel is left out: GDI leaves it
/// undefined. The hashes are only compared within one server run and never stored, so they need not be stable across runs.
/// </summary>
public sealed class VisualSignature
{
    /// <summary>The edge of a tile in pixels; the tiles of the last column and row can be narrower.</summary>
    public const int TileSize = 16;

    // Odd 64-bit constants (the golden ratio's, and a second one) and the rotation of the mix: each step is a bijection of
    // the hash for a given pixel pair, so two tiles that differ in one pair differ in their hash
    private const ulong Seed = 0x9E3779B97F4A7C15;
    private const ulong Multiplier = 0xBF58476D1CE4E5B9;
    private const int Rotation = 29;

    // Blue, green and red of two pixels; the undefined 4th byte of each left out
    private const ulong PairMask = 0x00FFFFFF_00FFFFFF;
    private const uint PixelMask = 0x00FFFFFF;

    // Mixed into a tile's last odd pixel, so a lone pixel never hashes like a pair whose second pixel is black
    private const ulong LonePixel = 1UL << 63;

    private readonly ulong[] _hashes;

    private VisualSignature(int originX, int originY, int width, int height, ulong[] hashes, bool uniform, string? method,
        IReadOnlyList<Rectangle> excluded)
    {
        OriginX = originX;
        OriginY = originY;
        Width = width;
        Height = height;
        Columns = (width + TileSize - 1) / TileSize;
        Rows = (height + TileSize - 1) / TileSize;
        _hashes = hashes;
        Uniform = uniform;
        Method = method;
        Excluded = excluded;
    }

    /// <summary>Screen X (physical virtual-screen pixels) of the image's top-left pixel.</summary>
    public int OriginX { get; }

    /// <summary>Screen Y of the image's top-left pixel.</summary>
    public int OriginY { get; }

    /// <summary>Width of the image in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the image in pixels.</summary>
    public int Height { get; }

    /// <summary>Tiles per row.</summary>
    public int Columns { get; }

    /// <summary>Rows of tiles.</summary>
    public int Rows { get; }

    /// <summary>
    /// True when every pixel has the same color: an empty area, or more often GPU or protected content the capture cannot
    /// read, or a window that has not painted yet. Such an image never changes, so a wait on it means little.
    /// </summary>
    public bool Uniform { get; }

    /// <summary>
    /// How the pixels were read (PrintWindow, ScreenBitBlt): two reads of a window by different methods differ where
    /// nothing changed (covering windows, rounded corners), so they are not compared with each other.
    /// </summary>
    public string? Method { get; }

    /// <summary>
    /// Screen rectangles left out of every comparison with this signature: the windows the target policy denied over a
    /// screen read, and the text caret, where they were when it was read (owner decision D8).
    /// </summary>
    public IReadOnlyList<Rectangle> Excluded { get; }

    /// <summary>The tiles' hashes, row by row: tile (column, row) is at <c>row * Columns + column</c>.</summary>
    public ReadOnlySpan<ulong> Hashes => _hashes;

    /// <summary>About how much memory the signature holds, for the capture registry's cap.</summary>
    public long ByteSize => _hashes.LongLength * sizeof(ulong) + Excluded.Count * 16L + 64;

    /// <summary>The screen rectangle the image shows.</summary>
    public CapturedRegion Region => new(OriginX, OriginY, Width, Height);

    /// <summary>The same signature, with <paramref name="excluded"/> as the rectangles it leaves out.</summary>
    public VisualSignature WithExcluded(IReadOnlyList<Rectangle> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        return new VisualSignature(OriginX, OriginY, Width, Height, _hashes, Uniform, Method, [.. excluded]);
    }

    /// <summary>True when <paramref name="other"/> has the same size, so the two compare tile by tile.</summary>
    public bool SameSizeAs(VisualSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Width == Width && other.Height == Height;
    }

    /// <summary>
    /// The signature of <paramref name="pixels"/>, in one pass over them. It keeps nothing of the lent memory, so it can run
    /// as an <see cref="IScreenCaptureWrapper.WithPixels{T}"/> reader.
    /// </summary>
    public static VisualSignature Of(PixelView pixels)
    {
        var width = pixels.Width;
        var height = pixels.Height;
        var columns = (width + TileSize - 1) / TileSize;
        var rows = (height + TileSize - 1) / TileSize;
        var hashes = new ulong[columns * rows];
        hashes.AsSpan().Fill(Seed);
        if (width == 0 || height == 0)
        {
            return new VisualSignature(pixels.ScreenOriginX, pixels.ScreenOriginY, width, height, hashes, uniform: true, pixels.Method, []);
        }

        var first = BinaryPrimitives.ReadUInt32LittleEndian(pixels.Bgra) & PixelMask;
        var firstPair = first | ((ulong)first << 32);
        var uniform = true;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Row(y);
            var tileRow = hashes.AsSpan(y / TileSize * columns, columns);
            for (var column = 0; column < columns; column++)
            {
                var left = column * TileSize;
                var count = Math.Min(TileSize, width - left);
                var tile = row.Slice(left * 4, count * 4);
                var hash = tileRow[column];
                foreach (var pair in MemoryMarshal.Cast<byte, ulong>(tile))
                {
                    var value = pair & PairMask;
                    uniform &= value == firstPair;
                    hash = BitOperations.RotateLeft((hash ^ value) * Multiplier, Rotation);
                }

                if ((count & 1) != 0)
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(tile[^4..]) & PixelMask;
                    uniform &= value == first;
                    hash = BitOperations.RotateLeft((hash ^ value ^ LonePixel) * Multiplier, Rotation);
                }

                tileRow[column] = hash;
            }
        }

        return new VisualSignature(pixels.ScreenOriginX, pixels.ScreenOriginY, width, height, hashes, uniform, pixels.Method, []);
    }
}
