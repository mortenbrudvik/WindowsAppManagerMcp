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

    [OcrLanguageFact(Requires = "ja")]
    public Task RecognizeAsync_Japanese_IsFoundByTheMatcher_WhateverSpacesOcrPutsBetweenItsWords() =>
        ReadAndFindAsync("ja", "保存するファイル", "Yu Gothic UI");

    [OcrLanguageFact(Requires = "zh")]
    public Task RecognizeAsync_Chinese_IsFoundByTheMatcher_WhateverSpacesOcrPutsBetweenItsWords() =>
        ReadAndFindAsync("zh", "保存文件到桌面", "Microsoft YaHei UI");

    /// <summary>
    /// Real OCR of a line of a script written without spaces, and find_text_on_screen's matcher on what it read: the line
    /// as drawn must match with contains and exact, however OCR spaced its words (roadmap M6.3 review).
    /// </summary>
    private static async Task ReadAndFindAsync(string language, string text, string fontName)
    {
        using var sut = new OcrEngineWrapper();
        var (bgra, width, height, _, _) = Draw(text, fontName, 40);

        var page = await sut.RecognizeAsync(bgra, width, height, OcrLanguageFactAttribute.InstalledTag(language)!, CancellationToken.None);

        var line = page.Lines.Should().ContainSingle().Subject;
        var words = new List<(int Start, int End)>();
        var position = 0;
        foreach (var word in line.Words)
        {
            var index = line.Text.IndexOf(word.Text, position, StringComparison.Ordinal);
            index.Should().BeGreaterThanOrEqualTo(0, $"OCR's word '{word.Text}' is in its line '{line.Text}'");
            words.Add((index, index + word.Text.Length));
            position = index + word.Text.Length;
        }

        foreach (var match in new[] { OcrTextMatch.Contains, OcrTextMatch.Exact })
        {
            var matcher = OcrTextMatcher.TryCreate(text, match, caseSensitive: false, out _)!;
            matcher.Find(line.Text, words).Should().ContainSingle($"'{text}' ({match}) is in OCR's line '{line.Text}'");
        }
    }

    /// <summary>Black text on white, as GDI captures it: top-down BGRA rows with an undefined 4th byte.</summary>
    private static (byte[] Bgra, int Width, int Height, double TextLeft, double TextRight) Draw(string text) => Draw(text, "Segoe UI", 30);

    private static (byte[] Bgra, int Width, int Height, double TextLeft, double TextRight) Draw(string text, string fontName, int fontPixels)
    {
        const int width = 720;
        const int height = 120;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(fontName, fontPixels, GraphicsUnit.Pixel))
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
            using var font = new Font(fontName, fontPixels, GraphicsUnit.Pixel);
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
    private static readonly Lazy<(string? Language, IReadOnlyList<string> Installed, string? Reason)> Probe = new(() =>
    {
        try
        {
            using var wrapper = new OcrEngineWrapper();
            var capabilities = wrapper.GetCapabilities();
            var language = capabilities.ProfileLanguage ?? capabilities.Languages.FirstOrDefault();
            return language == null
                ? (null, [], "No Windows OCR language is installed (Settings > Time & language > Language).")
                : (language, capabilities.Languages, null);
        }
        catch (OcrUnavailableException ex)
        {
            return (null, [], ex.Message);
        }
    });

    private string? _requires;

    public OcrLanguageFactAttribute()
    {
        if (Probe.Value.Reason is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>
    /// A language the test needs installed, such as "ja" (any installed tag of that language): skipped without one. The
    /// server never installs one, and neither do the tests.
    /// </summary>
    public string? Requires
    {
        get => _requires;
        set
        {
            _requires = value;
            if (Skip == null && value != null && InstalledTag(value) == null)
            {
                Skip = $"No Windows OCR language '{value}' is installed (installed: {string.Join(", ", Probe.Value.Installed)}).";
            }
        }
    }

    /// <summary>The installed tag of <paramref name="language"/> ("ja" finds "ja-JP"), or null.</summary>
    public static string? InstalledTag(string language) =>
        Probe.Value.Installed.FirstOrDefault(tag =>
            string.Equals(tag, language, StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed OCR language the tests read with, or null when they are skipped.</summary>
    public static string? Language => Probe.Value.Language;
}
