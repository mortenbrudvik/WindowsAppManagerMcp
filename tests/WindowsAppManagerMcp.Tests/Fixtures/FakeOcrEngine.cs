namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-memory <see cref="IOcrEngineWrapper"/> (roadmap M6.3): the installed languages and image limit a test sets, and
/// pages that <see cref="Read"/> makes from the image it is given (by default <see cref="TypicalPage"/>). Every recognition is
/// recorded with a copy of its pixels.
/// </summary>
public sealed class FakeOcrEngine : IOcrEngineWrapper
{
    /// <summary>The first line of <see cref="TypicalPage"/>, which find_text_on_screen's typical call finds.</summary>
    public const string TypicalTitle = "Quarterly planning - Canvas board";

    public IReadOnlyList<string> Languages { get; set; } = ["en-US", "nb"];

    public string? ProfileLanguage { get; set; } = "en-US";

    public int MaxImageDimension { get; set; } = 10_000;

    /// <summary>Thrown by <see cref="GetCapabilities"/> when set: Windows OCR is not available.</summary>
    public OcrUnavailableException? Unavailable { get; set; }

    /// <summary>Makes the page of an image: its pixels, width, height and language.</summary>
    public Func<byte[], int, int, string, OcrPage> Read { get; set; } = (_, width, height, _) => TypicalPage(width, height);

    /// <summary>
    /// Runs before each recognition returns, with the call's token: it can throw, or cancel the token for the next tile (the
    /// recognition it runs in still returns its page).
    /// </summary>
    public Action<CancellationToken>? OnRecognize { get; set; }

    public List<Recognition> Recognitions { get; } = [];

    public sealed record Recognition(byte[] Bgra, int Width, int Height, string Language);

    public OcrCapabilities GetCapabilities() =>
        Unavailable != null ? throw Unavailable : new OcrCapabilities(Languages, ProfileLanguage, MaxImageDimension);

    public Task<OcrPage> RecognizeAsync(byte[] bgra, int width, int height, string languageTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width > MaxImageDimension || height > MaxImageDimension)
        {
            throw new OcrUnavailableException($"The image is {width}x{height}, more than {MaxImageDimension} on an edge.");
        }

        lock (Recognitions)
        {
            Recognitions.Add(new Recognition((byte[])bgra.Clone(), width, height, languageTag));
        }

        OnRecognize?.Invoke(cancellationToken);
        return Task.FromResult(Read(bgra, width, height, languageTag));
    }

    /// <summary>A line of words at (x, y) in pixels of the image, each word 10 pixels a character and 20 high, one space apart.</summary>
    public static OcrPageLine Line(double x, double y, params string[] words)
    {
        var placed = new List<OcrPageWord>();
        foreach (var word in words)
        {
            placed.Add(new OcrPageWord(word, x, y, word.Length * 10, 20));
            x += (word.Length + 1) * 10;
        }

        return new OcrPageLine(string.Join(' ', words), placed);
    }

    /// <summary>
    /// A window of text as OCR reads it: <see cref="TypicalTitle"/>, then 39 lines of the typical article of up to 60
    /// characters, 22 pixels apart, scaled to the image (a 2x image gets boxes twice as large).
    /// </summary>
    public static OcrPage TypicalPage(int width, int height)
    {
        var scale = width >= 3000 ? 2 : 1;
        var texts = new List<string> { TypicalTitle };
        var line = new List<string>();
        foreach (var word in TypicalDesktop.Article.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Join(' ', line.Append(word)).Length > 60)
            {
                texts.Add(string.Join(' ', line));
                line.Clear();
                if (texts.Count == 40)
                {
                    break;
                }
            }

            line.Add(word);
        }

        var lines = texts.Select((text, i) =>
        {
            var plain = Line(12, 8 + i * 22, text.Split(' '));
            return new OcrPageLine(plain.Text, plain.Words.Select(w => w with
            {
                X = w.X * scale, Y = w.Y * scale, Width = w.Width * scale, Height = w.Height * scale
            }).ToArray());
        }).Where(l => l.Words.All(w => w.X + w.Width <= width && w.Y + w.Height <= height)).ToArray();
        return new OcrPage(null, lines);
    }
}
