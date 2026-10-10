using System.Drawing;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// Pixel checks for captures of the fixture. Decoding the server's capture result lives here only, so a
/// change to the result shape (image content blocks, roadmap M1.2) is adapted in one place.
/// </summary>
public static class CaptureChecks
{
    /// <summary>How far a swatch pixel may be from its color per channel (color management, scaling).</summary>
    public const int ColorTolerance = 12;

    /// <summary>Decodes the image of a successful capture that returned its data inline.</summary>
    public static Bitmap DecodeImage(ScreenshotResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success || string.IsNullOrEmpty(result.ImageData))
        {
            throw new InvalidOperationException($"The capture has no inline image: {result.Error ?? "no ImageData"}.");
        }
        return new Bitmap(new MemoryStream(Convert.FromBase64String(result.ImageData)));
    }

    /// <summary>
    /// Checks the center pixel of every fixture swatch in <paramref name="image"/>, given that image pixel
    /// (0, 0) shows screen point (<paramref name="originX"/>, <paramref name="originY"/>). Returns one line
    /// per swatch that is outside the image or has the wrong color; empty when all match.
    /// </summary>
    public static IReadOnlyList<string> SwatchMismatches(Bitmap image, int originX, int originY, FixtureReady ready)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ready);

        var mismatches = new List<string>();
        foreach (var swatch in FixtureContract.Swatches)
        {
            var rect = ready.Element(swatch.Id);
            var x = rect.CenterX - originX;
            var y = rect.CenterY - originY;
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                mismatches.Add($"{swatch.Id}: screen center ({rect.CenterX}, {rect.CenterY}) maps to image ({x}, {y}), " +
                               $"outside the {image.Width}x{image.Height} image.");
                continue;
            }

            var pixel = image.GetPixel(x, y);
            if (Math.Abs(pixel.R - swatch.R) > ColorTolerance ||
                Math.Abs(pixel.G - swatch.G) > ColorTolerance ||
                Math.Abs(pixel.B - swatch.B) > ColorTolerance)
            {
                mismatches.Add($"{swatch.Id}: image ({x}, {y}) is #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}, " +
                               $"expected #{swatch.R:X2}{swatch.G:X2}{swatch.B:X2}.");
            }
        }
        return mismatches;
    }
}
