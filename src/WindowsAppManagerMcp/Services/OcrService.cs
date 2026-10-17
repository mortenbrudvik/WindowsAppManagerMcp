using System.Drawing;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// read_screen_text and find_text_on_screen (roadmap M6.3, see <see cref="IOcrService"/>). The pipeline: resolve the language
/// (before anything is captured), read the denied windows' areas, capture the raw pixels, black out the denied windows when
/// the pixels are a copy of the screen (a monitor, a region, or a window that did not draw itself) as read before and after
/// the capture, enlarge small text 2x, read the image in tiles when it is larger than OCR takes, map the boxes back to
/// screen pixels, and register the capture.
/// </summary>
public sealed class OcrService : IOcrService
{
    /// <summary>Below this monitor scale, text is small enough in pixels that OCR reads it better at twice the size.</summary>
    public const double UpscaleBelowScale = 1.5;

    /// <summary>
    /// The largest capture enlarged 2x, in pixels (about 3440 x 1440): four times that, held about three times over by the
    /// enlargement and OCR's copies, is the most memory one call takes for it.
    /// </summary>
    public const long UpscaleMaxPixels = 5_000_000;

    /// <summary>What the results say the text came from.</summary>
    public const string Source = "ocr";

    /// <summary>How the user adds an OCR language; the server never installs one.</summary>
    public const string InstallHint =
        "The user adds one in Settings > Time & language > Language & region (a language with its optical character " +
        "recognition feature), or as an administrator with Add-WindowsCapability -Online -Name " +
        "'Language.OCR~~~en-US~0.0.1.0'. The server never installs one.";

    /// <summary>The method of a capture in which the window drew itself, so no other window shows.</summary>
    private const string PrintWindowMethod = "PrintWindow";

    private const string NoMatchHint =
        "OCR can misread l, I and 1 or O and 0, and splits and joins words its own way: try match 'contains' with a shorter " +
        "part of the text, or read_screen_text to see what OCR read.";

    private readonly IScreenshotService _screenshots;
    private readonly IOcrEngineWrapper _engine;
    private readonly INativeWindowWrapper _windows;
    private readonly ICaptureTargetPolicy _capturePolicy;
    private readonly ITargetPolicy? _policy;

    /// <param name="screenshots">The raw pixel seam and the capture registry (CapturePixels, RegisterRaw).</param>
    /// <param name="engine">Windows OCR.</param>
    /// <param name="windows">Lists the top-level windows for the mask of denied windows.</param>
    /// <param name="capturePolicy">The policy's check of each window for the mask, capture_window's.</param>
    /// <param name="policy">The policy itself: one that denies nothing masks nothing. Null: every window is checked.</param>
    public OcrService(IScreenshotService screenshots, IOcrEngineWrapper engine, INativeWindowWrapper windows,
        ICaptureTargetPolicy capturePolicy, ITargetPolicy? policy)
    {
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _capturePolicy = capturePolicy ?? throw new ArgumentNullException(nameof(capturePolicy));
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<ScreenTextResult> ReadTextAsync(
        OcrRequest request, OcrDetail detail, int maxLines, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        Screen screen;
        try
        {
            screen = await ReadScreenAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrFailure failure)
        {
            return new ScreenTextResult(false, Source, null, null, null, null, 0, null, false, WindowHandleOf(request.Target),
                Error: failure.Message, ErrorCode: failure.Code, InstalledLanguages: failure.InstalledLanguages);
        }

        var lines = screen.Lines.Take(maxLines).Select(line => new OcrLineDto(
            line.Text,
            line.Box.ToBounds(screen.Region),
            detail == OcrDetail.Words ? line.Words.Select(w => new OcrWordDto(w.Text, w.Box.ToBounds(screen.Region))).ToArray() : null))
            .ToArray();
        return new ScreenTextResult(
            true, Source, screen.Language, screen.CaptureId, screen.Capture.CapturedRegion, screen.Scale, screen.Lines.Count, lines,
            screen.Lines.Count > maxLines, WindowHandleOf(request.Target), screen.TextAngle, MaskedOrNull(screen),
            WarningsOrNull(screen));
    }

    /// <inheritdoc />
    public async Task<TextFindResult> FindTextAsync(
        OcrRequest request, OcrTextMatcher matcher, int maxResults, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

        Screen screen;
        try
        {
            screen = await ReadScreenAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrFailure failure)
        {
            return new TextFindResult(false, Source, null, null, null, null, 0, null, false, WindowHandleOf(request.Target),
                Error: failure.Message, ErrorCode: failure.Code, InstalledLanguages: failure.InstalledLanguages);
        }

        var origin = screen.Capture.CapturedRegion;
        var matches = new List<OcrMatchDto>();
        foreach (var line in screen.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spans = line.Words.Select(w => (w.Start, w.End)).ToArray();
            foreach (var (start, end) in matcher.Find(line.Text, spans))
            {
                if (MatchBox(line, start, end) is not { } box)
                {
                    continue;
                }

                var bounds = box.ToBounds(screen.Region);
                var click = new ScreenPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                matches.Add(new OcrMatchDto(line.Text[start..end], line.Text, bounds, click,
                    new ScreenPoint(click.X - origin.X, click.Y - origin.Y)));
            }
        }

        var ordered = matches.OrderBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X).ToList();
        return new TextFindResult(
            true, Source, screen.Language, screen.CaptureId, origin, screen.Scale, ordered.Count, ordered.Take(maxResults).ToArray(),
            ordered.Count > maxResults, WindowHandleOf(request.Target), MaskedOrNull(screen), WarningsOrNull(screen),
            ordered.Count == 0 ? NoMatchHint : null);
    }

    /// <summary>A line read from the screen, with its words' character ranges in its text and its box in screen pixels.</summary>
    private sealed record ScreenLine(string Text, ScreenBox Box, IReadOnlyList<ScreenWord> Words);

    private sealed record ScreenWord(string Text, int Start, int End, ScreenBox Box);

    /// <summary>What one OCR pass over the target read, ready for either tool's result.</summary>
    private sealed record Screen(
        RawCapture Capture,
        string Language,
        int Scale,
        double? TextAngle,
        IReadOnlyList<ScreenLine> Lines,
        int MaskedWindows,
        IReadOnlyList<string> Warnings,
        string? CaptureId)
    {
        public Rectangle Region => new(Capture.Pixels.ScreenOriginX, Capture.Pixels.ScreenOriginY, Capture.Pixels.Width, Capture.Pixels.Height);
    }

    /// <summary>A failure the tools report with its code: a capture refusal, or OCR that cannot read.</summary>
    private sealed class OcrFailure(string code, string message, IReadOnlyList<string>? installedLanguages = null) : Exception(message)
    {
        public string Code { get; } = code;

        public IReadOnlyList<string>? InstalledLanguages { get; } = installedLanguages;
    }

    /// <exception cref="OcrFailure">The language, the capture or OCR failed.</exception>
    private async Task<Screen> ReadScreenAsync(OcrRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        OcrCapabilities capabilities;
        try
        {
            capabilities = _engine.GetCapabilities();
        }
        catch (OcrUnavailableException ex)
        {
            throw new OcrFailure(nameof(ScreenshotErrorCode.OcrUnavailable), $"{ex.Message} Nothing was captured.");
        }

        var language = ResolveLanguage(request.Language, capabilities, warnings);

        // The denied windows as they are just before the capture, and again just after it: a window that moves meanwhile is
        // blacked out where it was and where it is
        var mask = new DeniedWindowMask(_windows, _capturePolicy, _policy);
        var deniedBefore = ReadMask(mask);
        RawCapture capture;
        try
        {
            capture = _screenshots.CapturePixels(request.Target);
        }
        catch (CaptureException ex)
        {
            throw new OcrFailure(ex.Code.ToString(), ex.Message);
        }

        var masked = capture.Pixels.Method == PrintWindowMethod ? 0 : DeniedWindowMask.Apply(capture.Pixels, deniedBefore, ReadMask(mask));
        warnings.InsertRange(0, capture.Warnings);

        var pixels = capture.Pixels;
        // An unknown scale (the virtual screen, a region centred on no monitor) is read as captured: at 150% and above 2x only
        // costs time and memory
        var scale = capture.ScaleFactor is < UpscaleBelowScale && (long)pixels.Width * pixels.Height <= UpscaleMaxPixels &&
                    pixels.Width * 2 <= capabilities.MaxImageDimension && pixels.Height * 2 <= capabilities.MaxImageDimension
            ? 2
            : 1;
        var image = scale == 2 ? OcrImage.Upscale2x(pixels.Bgra, pixels.Width, pixels.Height) : pixels.Bgra;
        var (width, height) = (pixels.Width * scale, pixels.Height * scale);
        var tiles = OcrImage.Tiles(width, height, capabilities.MaxImageDimension, OcrImage.OverlapFor(capabilities.MaxImageDimension));
        if (tiles.Count > 1)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.OcrTiled,
                $"the image is larger than Windows OCR reads at once ({capabilities.MaxImageDimension} pixels on an edge), so it " +
                $"was read in {tiles.Count} tiles; a line across a tile's edge can come back as two lines."));
        }

        var origin = new PointF(pixels.ScreenOriginX, pixels.ScreenOriginY);
        var lines = new List<ScreenLine>();
        double? textAngle = null;
        for (var i = 0; i < tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tile = tiles[i];
            var tilePixels = tiles.Count == 1 ? image : OcrImage.Crop(image, width, tile.Area);
            OcrPage page;
            try
            {
                page = await _engine.RecognizeAsync(tilePixels, tile.Area.Width, tile.Area.Height, language, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OcrUnavailableException ex)
            {
                throw new OcrFailure(nameof(ScreenshotErrorCode.OcrUnavailable), $"{ex.Message} Nothing was read.",
                    capabilities.Languages);
            }

            textAngle ??= page.TextAngle;
            lines.AddRange(page.Lines.Select(line => ToScreen(line, tile, origin, scale)).OfType<ScreenLine>());
            progress?.Report(new OperationProgress(i + 1, tiles.Count, $"OCR read {i + 1} of {tiles.Count} parts of the image"));
        }

        // Registered last: the capture's window or screen is compared with how it was when the pixels were read, so the time
        // OCR took does not hide a change made during it
        if (tiles.Count > 1)
        {
            // Each tile's lines came in its own reading order: put the whole image's back top to bottom, then left to right
            lines = lines.OrderBy(line => line.Box.Top).ThenBy(line => line.Box.Left).ToList();
        }

        var captureId = _screenshots.RegisterRaw(capture);
        return new Screen(capture, language, scale, textAngle, lines, masked, warnings, captureId);
    }

    /// <summary>The denied windows' areas, or the read refused when one of them cannot be placed (fail-closed).</summary>
    /// <exception cref="OcrFailure">A denied window is on screen but its rectangle cannot be read.</exception>
    private static IReadOnlyList<DeniedWindowArea> ReadMask(DeniedWindowMask mask)
    {
        try
        {
            return mask.Read();
        }
        catch (DeniedWindowUnlocatedException ex)
        {
            throw new OcrFailure(nameof(ScreenshotErrorCode.CaptureFailed), $"{ex.Message} Nothing was read; try again.");
        }
    }

    /// <summary>
    /// The installed language to read in: the one asked for (by its tag, any case, or by its primary language alone, "en" for
    /// "en-US"), or with none the user's profile language, or failing that the first installed one, with a warning.
    /// </summary>
    /// <exception cref="OcrFailure">The language is not installed, or none is.</exception>
    private static string ResolveLanguage(string? requested, OcrCapabilities capabilities, List<string> warnings)
    {
        var installed = capabilities.Languages;
        if (installed.Count == 0)
        {
            throw new OcrFailure(nameof(ScreenshotErrorCode.OcrUnavailable),
                $"No Windows OCR language is installed, so no text can be read. {InstallHint} Nothing was captured.", installed);
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            if (capabilities.ProfileLanguage is { } profile)
            {
                return profile;
            }

            warnings.Add(CaptureWarnings.Format(CaptureWarnings.OcrLanguageFallback,
                $"none of the user's languages has an OCR recognizer, so the text was read as '{installed[0]}'; pass language " +
                $"to choose another of {string.Join(", ", installed)}."));
            return installed[0];
        }

        var tag = requested.Trim();
        var match = installed.FirstOrDefault(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase)) ??
                    installed.FirstOrDefault(l => !tag.Contains('-') && l.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase));
        return match ?? throw new OcrFailure(nameof(ScreenshotErrorCode.OcrLanguageNotInstalled),
            $"'{tag}' is not an installed OCR language. Installed: {string.Join(", ", installed)}. {InstallHint} Nothing was captured.",
            installed);
    }

    /// <summary>
    /// A line of a tile in screen pixels, with only the words whose centre lies in the tile's core (a word on a tile's edge is
    /// kept whole by the tile next to it), or null when none does.
    /// </summary>
    private static ScreenLine? ToScreen(OcrPageLine line, OcrTile tile, PointF origin, int scale)
    {
        var spans = WordSpans(line);
        var text = spans.Text;
        var kept = new List<ScreenWord>();
        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            // Clamped to the tile, so a box OCR reports past the image's edge still counts for the tile at that edge
            var centreX = Math.Clamp(tile.Area.X + word.X + word.Width / 2, tile.Area.Left, tile.Area.Right - 1);
            var centreY = Math.Clamp(tile.Area.Y + word.Y + word.Height / 2, tile.Area.Top, tile.Area.Bottom - 1);
            if (centreX < tile.Core.Left || centreX >= tile.Core.Right || centreY < tile.Core.Top || centreY >= tile.Core.Bottom)
            {
                continue;
            }

            var box = new ScreenBox(
                origin.X + (tile.Area.X + word.X) / scale,
                origin.Y + (tile.Area.Y + word.Y) / scale,
                origin.X + (tile.Area.X + word.X + word.Width) / scale,
                origin.Y + (tile.Area.Y + word.Y + word.Height) / scale);
            kept.Add(new ScreenWord(word.Text, spans.Ranges[i].Start, spans.Ranges[i].End, box));
        }

        if (kept.Count == 0)
        {
            return null;
        }

        if (kept.Count < line.Words.Count)
        {
            // The part of the line from the first kept word to the last, its words' ranges moved to start at 0
            var from = kept[0].Start;
            text = text[from..kept[^1].End];
            kept = kept.Select(w => w with { Start = w.Start - from, End = w.End - from }).ToList();
        }

        return new ScreenLine(text, kept.Select(w => w.Box).Aggregate(ScreenBox.Union), kept);
    }

    /// <summary>
    /// Each word's [start, end) range in the line's text, found in order; when a word is not where the line's text says
    /// (an engine that joins words differently), the text is rebuilt from the words joined with spaces.
    /// </summary>
    private static (string Text, (int Start, int End)[] Ranges) WordSpans(OcrPageLine line)
    {
        var ranges = new (int Start, int End)[line.Words.Count];
        var position = 0;
        for (var i = 0; i < line.Words.Count; i++)
        {
            var index = line.Text.IndexOf(line.Words[i].Text, position, StringComparison.Ordinal);
            if (index < 0 || line.Words[i].Text.Length == 0)
            {
                return Joined(line.Words);
            }

            ranges[i] = (index, index + line.Words[i].Text.Length);
            position = ranges[i].End;
        }

        return (line.Text, ranges);
    }

    private static (string Text, (int Start, int End)[] Ranges) Joined(IReadOnlyList<OcrPageWord> words)
    {
        var ranges = new (int Start, int End)[words.Count];
        var position = 0;
        for (var i = 0; i < words.Count; i++)
        {
            ranges[i] = (position, position + words[i].Text.Length);
            position = ranges[i].End + 1;
        }

        return (string.Join(' ', words.Select(w => w.Text)), ranges);
    }

    /// <summary>
    /// The box of the characters [start, end) of a line: the union of the words they touch, each word cut to the share of its
    /// width those characters take (OCR gives no box per character), or null when they touch no word.
    /// </summary>
    private static ScreenBox? MatchBox(ScreenLine line, int start, int end)
    {
        ScreenBox? box = null;
        foreach (var word in line.Words.Where(w => w.Start < end && w.End > start))
        {
            var length = Math.Max(1, word.End - word.Start);
            var width = word.Box.Right - word.Box.Left;
            var left = word.Box.Left + width * Math.Max(0, start - word.Start) / length;
            var right = word.Box.Right - width * Math.Max(0, word.End - end) / length;
            var part = word.Box with { Left = left, Right = right };
            box = box is { } union ? ScreenBox.Union(union, part) : part;
        }

        return box;
    }

    private static long? WindowHandleOf(CaptureTarget target) => target is CaptureTarget.Window window ? window.Handle : null;

    private static int? MaskedOrNull(Screen screen) => screen.MaskedWindows > 0 ? screen.MaskedWindows : null;

    private static IReadOnlyList<string>? WarningsOrNull(Screen screen) => screen.Warnings.Count > 0 ? screen.Warnings : null;
}

/// <summary>A box in screen pixels with fractional edges, as OCR's boxes map to the screen.</summary>
internal readonly record struct ScreenBox(double Left, double Top, double Right, double Bottom)
{
    public static ScreenBox Union(ScreenBox a, ScreenBox b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>The box rounded outward to whole pixels, inside <paramref name="region"/>.</summary>
    public BoundsDto ToBounds(Rectangle region)
    {
        var left = Math.Clamp((int)Math.Floor(Left), region.Left, region.Right);
        var top = Math.Clamp((int)Math.Floor(Top), region.Top, region.Bottom);
        var right = Math.Clamp((int)Math.Ceiling(Right), region.Left, region.Right);
        var bottom = Math.Clamp((int)Math.Ceiling(Bottom), region.Top, region.Bottom);
        return new BoundsDto(left, top, right - left, bottom - top);
    }
}
