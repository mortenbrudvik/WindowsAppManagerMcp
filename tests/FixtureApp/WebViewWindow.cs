using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A separate, unowned top-level window holding a WebView2 pane (roadmap M3.F4), opened only with --open-web-view, so an
/// ordinary start loads no WebView2 and needs no runtime. The pane shows local HTML (NavigateToString: no network, no
/// debug port) in a Chrome_RenderWidgetHostHWND of msedgewebview2.exe inside this window, which is what the accessibility
/// warm-up (roadmap M3.3) looks for: Chromium builds the page's accessibility tree only once a UI Automation client asks.
/// </summary>
/// <remarks>
/// The browser's user data goes into the fixture's own folder (the folder of its event log, which the harness deletes),
/// not next to the exe. Without the Evergreen WebView2 runtime the window stays empty and the event log says why
/// (<see cref="Events.WebViewFailed"/>).
/// </remarks>
internal sealed class WebViewWindow : Window
{
    private const string Html =
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>WAM fixture web content</title></head><body>" +
        "<h1>" + WebHeading + "</h1>" +
        "<p>Local HTML shown by NavigateToString: no network.</p>" +
        "<button id=\"" + Ids.WebButton + "\" type=\"button\">" + WebButtonName + "</button>" +
        "</body></html>";

    private readonly FixtureOptions _options;
    private readonly FixtureEventLog _events;
    private readonly WebView2 _webView = new();
    private bool _reported;

    public WebViewWindow(FixtureOptions options, FixtureEventLog events)
    {
        _options = options;
        _events = events;

        Title = WebTitle(options.Instance);
        Width = 560;
        Height = 380;
        FixturePlacement.CenterOnPrimaryMonitor(this);
        ShowActivated = !options.NoActivate;
        AutomationProperties.SetAutomationId(_webView, Ids.WebView);
        Content = _webView;
        Loaded += OnLoaded;
    }

    private nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>The browser's user data folder: in the fixture's own folder, else in the temp folder.</summary>
    private string UserDataFolder =>
        Path.Combine(FixtureSaveFolder.For(_options) ?? Path.Combine(Path.GetTempPath(), "WamFixtureApp"), "WebView2");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string? version;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Fail(runtimeMissing: true, ex.Message);
            return;
        }

        if (string.IsNullOrEmpty(version))
        {
            Fail(runtimeMissing: true, "No WebView2 runtime is installed.");
            return;
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: UserDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    Report(Events.WebViewReady, ("hwnd", (long)Handle), ("runtimeVersion", version));
                }
                else
                {
                    Fail(runtimeMissing: false, $"The local HTML did not load: {args.WebErrorStatus}.");
                }
            };
            _webView.NavigateToString(Html);
        }
        catch (Exception ex)
        {
            // Whatever WebView2 throws (an async void handler must not let it reach the dispatcher unreported): the log says
            // why, so the scenario fails with the reason instead of waiting for the page
            Fail(ex is WebView2RuntimeNotFoundException, ex.Message);
        }
    }

    private void Fail(bool runtimeMissing, string message) =>
        Report(Events.WebViewFailed, ("runtimeMissing", runtimeMissing), ("message", message));

    /// <summary>Writes the first outcome only: a later navigation event says nothing new.</summary>
    private void Report(string name, params (string Key, JsonNode? Value)[] fields)
    {
        if (_reported)
        {
            return;
        }

        _reported = true;
        _events.Write(name, fields);
    }
}
