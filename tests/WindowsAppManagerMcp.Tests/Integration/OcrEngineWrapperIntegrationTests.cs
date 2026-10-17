using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The real Windows OCR engine through <see cref="OcrEngineWrapper"/> (roadmap M6.3): the slim CsWinRT projection loads,
/// and text drawn into an in-memory bitmap is read back with its boxes. No screen, window or input is involved. A machine
/// without an OCR language (some CI images) skips the recognition tests with the reason.
/// </summary>
public class OcrEngineWrapperIntegrationTests
{
    private const string DrawnText = "WAM OCR 4711 Canvas";

    [Fact]
    public void GetCapabilities_ReportsTheInstalledLanguagesAndTheImageLimit()
    {
        using var sut = new OcrEngineWrapper();

        var capabilities = sut.GetCapabilities();

        capabilities.MaxImageDimension.Should().BeGreaterThan(1000);
        capabilities.Languages.Should().OnlyContain(tag => !string.IsNullOrWhiteSpace(tag));
        if (capabilities.ProfileLanguage is { } profile)
        {
            capabilities.Languages.Should().Contain(profile, "OCR picks the profile language from the installed recognizers");
        }
    }

    [OcrLanguageFact]
    public async Task RecognizeAsync_ReadsTextDrawnInMemory_WithBoxesWhereItWasDrawn()
    {
        using var sut = new OcrEngineWrapper();
        var language = OcrLanguageFactAttribute.Language!;
        var (bgra, width, height, textLeft, textRight) = Draw(DrawnText);

        var page = await sut.RecognizeAsync(bgra, width, height, language, CancellationToken.None);

        var words = page.Lines.SelectMany(line => line.Words).ToList();
        words.Select(w => w.Text).Should().Contain("4711");
        var number = words.First(w => w.Text == "4711");
        number.X.Should().BeInRange(textLeft, textRight);
        (number.X + number.Width).Should().BeInRange(textLeft, textRight + 2);
        number.Y.Should().BeInRange(0, height);
        page.Lines.Should().ContainSingle().Which.Text.Should().Contain("4711");
    }

    [OcrLanguageFact]
    public async Task RecognizeAsync_UnknownLanguage_IsOcrUnavailable()
    {
        using var sut = new OcrEngineWrapper();
        var (bgra, width, height, _, _) = Draw(DrawnText);

        var act = () => sut.RecognizeAsync(bgra, width, height, "qaa-Qaaa", CancellationToken.None);

        await act.Should().ThrowAsync<OcrUnavailableException>();
    }

    [Fact]
    public async Task RecognizeAsync_BufferShorterThanTheImage_IsAnArgumentError()
    {
        using var sut = new OcrEngineWrapper();

        var act = () => sut.RecognizeAsync(new byte[10], 10, 10, "en-US", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>Black text on white, as GDI captures it: top-down BGRA rows with an undefined 4th byte.</summary>
    private static (byte[] Bgra, int Width, int Height, double TextLeft, double TextRight) Draw(string text)
    {
        const int width = 720;
        const int height = 120;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 30, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.DrawString(text, font, Brushes.Black, 20, 30);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var bgra = new byte[width * height * 4];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, bgra, row * width * 4, width * 4);
            }

            using var measure = Graphics.FromImage(bitmap);
            using var font = new Font("Segoe UI", 30, GraphicsUnit.Pixel);
            var size = measure.MeasureString(text, font);
            return (bgra, width, height, 20, 20 + size.Width);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

/// <summary>
/// A fact that needs a Windows OCR language: skipped, with the reason, when OCR is unavailable or no recognizer language is
/// installed (the server never installs one). <see cref="Language"/> is the language to read with: the profile's, or the
/// first installed one.
/// </summary>
public sealed class OcrLanguageFactAttribute : FactAttribute
{
    private static readonly Lazy<(string? Language, string? Reason)> Probe = new(() =>
    {
        try
        {
            using var wrapper = new OcrEngineWrapper();
            var capabilities = wrapper.GetCapabilities();
            var language = capabilities.ProfileLanguage ?? capabilities.Languages.FirstOrDefault();
            return language == null
                ? (null, "No Windows OCR language is installed (Settings > Time & language > Language).")
                : (language, null);
        }
        catch (OcrUnavailableException ex)
        {
            return (null, ex.Message);
        }
    });

    public OcrLanguageFactAttribute()
    {
        if (Probe.Value.Reason is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>The installed OCR language the tests read with, or null when they are skipped.</summary>
    public static string? Language => Probe.Value.Language;
}
