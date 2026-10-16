using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A separate, unowned top-level window that draws <see cref="CanvasLines"/> as pixels only (roadmap M6.3): UI Automation
/// finds one element there, <see cref="Ids.CanvasSurface"/>, with no name, value, text pattern or children, so only OCR can
/// read the text. When it has rendered it reports where each line and word landed (<see cref="Events.CanvasOpened"/>), and
/// it logs each press with the word under it (<see cref="Events.CanvasMouseDown"/>). It follows --other-scale-monitor as
/// the main window does.
/// </summary>
internal sealed class CanvasWindow : Window
{
    private readonly FixtureEventLog _events;
    private readonly TextCanvas _canvas = new();
    private NoActivateWhileShown? _noActivateWhileShown;
    private bool _reported;

    public CanvasWindow(FixtureOptions options, FixtureEventLog events)
    {
        _events = events;

        Title = CanvasTitle(options.Instance);
        Width = 640;
        Height = 360;
        FixturePlacement.CenterOnPrimaryMonitor(this);

        // As the main window: on a monitor at another scale WPF's DPI change inside Show would activate it
        if (options.OtherScaleMonitor && FixturePlacement.CenterOnMonitorAtAnotherScale(this) && options.NoActivate)
        {
            _noActivateWhileShown = new NoActivateWhileShown();
        }
        ShowActivated = !options.NoActivate;
        UseLayoutRounding = true;
        Background = Brushes.White;

        AutomationProperties.SetAutomationId(_canvas, Ids.CanvasSurface);
        _canvas.PreviewMouseDown += OnPreviewMouseDown;
        Content = _canvas;
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _noActivateWhileShown?.Release();
        _noActivateWhileShown = null;

        if (_reported)
        {
            return;
        }
        _reported = true;

        var lines = new JsonArray();
        foreach (var line in _canvas.Layout())
        {
            var words = new JsonArray();
            foreach (var (word, box) in line.Words)
            {
                words.Add(new JsonObject { ["text"] = word, ["rect"] = FixtureEventLog.ScreenRect(_canvas, box) });
            }

            lines.Add(new JsonObject
            {
                ["text"] = line.Line.Text,
                ["rect"] = FixtureEventLog.ScreenRect(_canvas, line.Box),
                ["words"] = words
            });
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        _events.Write(
            Events.CanvasOpened,
            ("hwnd", (long)hwnd),
            ("title", Title),
            ("dpi", (int)NativeMethods.GetDpiForWindow(hwnd)),
            ("lines", lines));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The button message's own point, as the main window logs it
        var position = NativeMethods.GetMessagePos();
        _events.Write(
            Events.CanvasMouseDown,
            ("button", MainWindow.ButtonName(e.ChangedButton)),
            ("x", (int)(short)(position & 0xFFFF)),
            ("y", (int)(short)(position >> 16)),
            ("word", _canvas.WordAt(e.GetPosition(_canvas))));
    }

    /// <summary>
    /// Draws the lines in OnRender with FormattedText, so no control holds them. Display text formatting and grayscale
    /// rendering keep the glyphs on whole pixels without ClearType's colored fringes.
    /// </summary>
    private sealed class TextCanvas : FrameworkElement
    {
        private const double Inset = 24;
        private const double LineGap = 16;
        private const double BandPadding = 8;

        private static readonly Typeface Face = new("Segoe UI");
        private static readonly Brush DarkText = Frozen(0x20, 0x20, 0x20);
        private static readonly Brush LightText = Frozen(0xF4, 0xF4, 0xF4);
        private static readonly Brush DarkBand = Frozen(0x30, 0x30, 0x40);

        public TextCanvas()
        {
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        }

        /// <summary>
        /// Where each line goes at the current DPI, in this element's device-independent coordinates. A line's and a word's
        /// box is the text's cell box (as a selection would highlight it: the line's full height), not just its ink.
        /// </summary>
        public IReadOnlyList<LaidOutLine> Layout()
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var lines = new List<LaidOutLine>();
            var y = Inset;
            foreach (var line in CanvasLines)
            {
                var text = new FormattedText(
                    line.Text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Face,
                    line.FontSize,
                    line.LightOnDark ? LightText : DarkText,
                    null,
                    TextFormattingMode.Display,
                    pixelsPerDip);
                var padding = line.LightOnDark ? BandPadding : 0;
                var origin = new Point(Inset, y + padding);
                var words = CanvasWordSpans(line.Text)
                    .Select(span => (line.Text.Substring(span.Start, span.Length),
                        text.BuildHighlightGeometry(origin, span.Start, span.Length).Bounds))
                    .ToList();
                var box = text.BuildHighlightGeometry(origin, 0, line.Text.Length).Bounds;
                var band = new Rect(0, y, Math.Max(RenderSize.Width, 0), text.Height + 2 * padding);
                lines.Add(new LaidOutLine(line, text, origin, box, band, words));
                y += text.Height + 2 * padding + LineGap;
            }

            return lines;
        }

        /// <summary>The canvas word at <paramref name="point"/> (this element's coordinates), or empty.</summary>
        public string WordAt(Point point) =>
            Layout().SelectMany(l => l.Words).Where(w => w.Box.Contains(point)).Select(w => w.Word).FirstOrDefault() ?? "";

        protected override void OnRender(DrawingContext drawingContext)
        {
            // A background of its own, so the whole canvas takes presses
            drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
            foreach (var line in Layout())
            {
                if (line.Line.LightOnDark)
                {
                    drawingContext.DrawRectangle(DarkBand, null, line.Band);
                }
                drawingContext.DrawText(line.Text, line.Origin);
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new TextlessAutomationPeer(this);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>One laid-out canvas line: what to draw where, its box, its dark band (if any) and its words' boxes.</summary>
    private sealed record LaidOutLine(
        CanvasLine Line,
        FormattedText Text,
        Point Origin,
        Rect Box,
        Rect Band,
        IReadOnlyList<(string Word, Rect Box)> Words);

    /// <summary>
    /// The canvas's automation peer: a pane with its AutomationId, but no name, no children and no text or value pattern
    /// (FrameworkElementAutomationPeer offers none), so UI Automation has nothing to read there.
    /// </summary>
    private sealed class TextlessAutomationPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => "WamFixtureTextCanvas";

        protected override string GetNameCore() => "";

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
