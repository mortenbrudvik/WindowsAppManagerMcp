using System.Net;
using System.Text;
using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-memory debug browser for the window binding (roadmap M5.3): /json/version and /json/list over a fake HTTP
/// handler, and a browser-level WebSocket that answers Browser.getWindowForTarget from the windows its tabs are in, and
/// Target.activateTarget and Target.createTarget for the tab tools (roadmap M5.5). Each connection to a tab's own URL gets
/// a page socket of its own, which reads the tab's document.visibilityState (<see cref="Visibility"/>) in an isolated world
/// only; in the main world a page can say what it likes (<see cref="MainWorldVisibility"/>). No network is used.
/// </summary>
public sealed class FakeCdpBrowser
{
    /// <summary>The execution context id Page.createIsolatedWorld answers with.</summary>
    public const int IsolatedContextId = 31;

    private readonly int _port;
    private readonly List<(string Id, string Title, string Url, int? WindowId)> _tabs = new();
    private readonly Dictionary<int, (int Left, int Top, int Width, int Height, string State)> _windows = new();
    private readonly Dictionary<string, string> _visibility = new(StringComparer.Ordinal);
    private readonly List<(string TabId, JsonElement Command)> _pageCommands = new();

    public FakeCdpBrowser(int port = 9222)
    {
        _port = port;
        Socket = new FakeCdpWebSocket(Respond);
        Sockets = new FakeCdpBrowserSockets(this);
        Http = new FakeCdpHttpHandler(Answer);
    }

    /// <summary>The browser-level DevTools connection's socket.</summary>
    public FakeCdpWebSocket Socket { get; }

    /// <summary>Hands out <see cref="Socket"/> for the browser's URL, and a new page socket for each connection to a tab.</summary>
    public FakeCdpBrowserSockets Sockets { get; }

    /// <summary>
    /// What a page that redefines document.visibilityState (Object.defineProperty) reports in its main world; null reports
    /// the tab's own <see cref="Visibility"/> there too.
    /// </summary>
    public string? MainWorldVisibility { get; set; } = "visible";

    /// <summary>The tabs whose page answers nothing on its own connection (a page blocked by a dialog, or busy).</summary>
    public HashSet<string> PagesUnanswered { get; } = new(StringComparer.Ordinal);

    /// <summary>The commands sent on the tabs' own connections so far, with the tab, in order (a copy).</summary>
    public IReadOnlyList<(string TabId, JsonElement Command)> PageCommands
    {
        get
        {
            lock (_pageCommands)
            {
                return _pageCommands.ToList();
            }
        }
    }

    /// <summary>Answers /json/version and /json/list.</summary>
    public FakeCdpHttpHandler Http { get; }

    /// <summary>When true, /json/version answers as a program that is not a browser's DevTools endpoint.</summary>
    public bool NotABrowsersVersion { get; set; }

    /// <summary>
    /// The tabs whose Browser.getWindowForTarget is never answered (a reply lost to the time budget); null leaves every
    /// one unanswered, as a browser connection that stalls.
    /// </summary>
    public HashSet<string>? Unanswered { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How the browser answers Browser.close (roadmap M5.4).</summary>
    public FakeBrowserClose CloseAnswer { get; set; } = FakeBrowserClose.ReplyThenClose;

    /// <summary>The Browser.close commands sent so far.</summary>
    public int CloseCommands => Socket.SentCommands.Count(c => c.GetProperty("method").GetString() == "Browser.close");

    /// <summary>The browser WebSocket URL /json/version lists.</summary>
    public string BrowserUrl => $"ws://127.0.0.1:{_port}/devtools/browser/B1";

    /// <summary>The tabs Target.activateTarget was sent for, in order.</summary>
    public List<string> Activated { get; } = new();

    /// <summary>The window Target.createTarget puts a new tab in: the browser's most recently active one.</summary>
    public int? LastActiveWindow { get; set; }

    /// <summary>When true, a tab Target.createTarget opens is not listed by /json/list (it closed at once).</summary>
    public bool HideCreatedTabs { get; set; }

    /// <summary>The URLs Target.createTarget was sent with, in order.</summary>
    public List<string> Created { get; } = new();

    /// <summary>The Browser.getWindowForTarget commands sent so far.</summary>
    public int WindowQueries => Socket.SentCommands.Count(c => c.GetProperty("method").GetString() == "Browser.getWindowForTarget");

    /// <summary>Adds a browser window: its bounds in DIPs (the restored ones when it is not normal) and its state.</summary>
    public FakeCdpBrowser Window(int windowId, int left, int top, int width, int height, string state = "normal")
    {
        _windows[windowId] = (left, top, width, height, state);
        return this;
    }

    /// <summary>Adds a page target in window <paramref name="windowId"/>, or in none (not a tab of a tab strip).</summary>
    public FakeCdpBrowser Tab(string id, string title, int? windowId, string? url = null)
    {
        _tabs.Add((id, title, url ?? $"https://example.com/{id}", windowId));
        return this;
    }

    /// <summary>
    /// Gives tab <paramref name="id"/> a page that answers on its own connection: Page.getFrameTree, Page.createIsolatedWorld
    /// in its main frame, and Runtime.evaluate, which reads <paramref name="state"/> as its visibilityState in that world. A
    /// tab without one refuses every command on its own connection.
    /// </summary>
    public FakeCdpBrowser Visibility(string id, string state)
    {
        _visibility[id] = state;
        return this;
    }

    /// <summary>The page socket for a new connection to tab <paramref name="tabId"/>'s own URL.</summary>
    internal FakeCdpWebSocket PageSocket(string tabId) => new((socket, command) => RespondAsPage(tabId, socket, command));

    private void RespondAsPage(string tabId, FakeCdpWebSocket socket, JsonElement command)
    {
        lock (_pageCommands)
        {
            _pageCommands.Add((tabId, command.Clone()));
        }

        if (PagesUnanswered.Contains(tabId))
            return;

        var method = command.GetProperty("method").GetString();
        if (!_visibility.TryGetValue(tabId, out var visibility))
        {
            socket.ReplyError(command, -32601, "'" + method + "' wasn't found");
            return;
        }

        var id = command.GetProperty("id").GetInt32();
        var parameters = command.TryGetProperty("params", out var given) ? given : default;
        switch (method)
        {
            case "Page.getFrameTree":
                socket.Enqueue(JsonSerializer.Serialize(new
                {
                    id,
                    result = new { frameTree = new { frame = new { id = "F-" + tabId, loaderId = "L-" + tabId, url = $"https://example.com/{tabId}" } } }
                }));
                break;

            case "Page.createIsolatedWorld" when parameters.TryGetProperty("frameId", out var frameId) && frameId.GetString() == "F-" + tabId:
                socket.Enqueue(JsonSerializer.Serialize(new { id, result = new { executionContextId = IsolatedContextId } }));
                break;

            case "Page.createIsolatedWorld":
                socket.ReplyError(command, -32602, "No frame for given id found");
                break;

            case "Runtime.evaluate":
                var isolated = parameters.TryGetProperty("contextId", out var context) && context.GetInt32() == IsolatedContextId;
                socket.Enqueue(JsonSerializer.Serialize(new
                {
                    id,
                    result = new { result = new { type = "string", value = isolated ? visibility : MainWorldVisibility ?? visibility } }
                }));
                break;

            default:
                socket.ReplyError(command, -32601, "'" + method + "' wasn't found");
                break;
        }
    }

    private Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        object body = path switch
        {
            "/json/version" when NotABrowsersVersion => new { hello = "not DevTools" },
            "/json/version" => new Dictionary<string, string>
            {
                ["Browser"] = "Chrome/155.0.0.0",
                ["Protocol-Version"] = "1.3",
                ["webSocketDebuggerUrl"] = BrowserUrl
            },
            "/json/list" => _tabs.Select(t => new
            {
                id = t.Id,
                type = "page",
                title = t.Title,
                url = t.Url,
                webSocketDebuggerUrl = $"ws://127.0.0.1:{_port}/devtools/page/{t.Id}"
            }).ToList(),
            _ => new { }
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });
    }

    private void Respond(FakeCdpWebSocket socket, JsonElement command)
    {
        var method = command.GetProperty("method").GetString();
        if (method == "Browser.close")
        {
            switch (CloseAnswer)
            {
                case FakeBrowserClose.ReplyThenClose:
                    socket.ReplyEmpty(command);
                    socket.EnqueueClose();
                    break;
                case FakeBrowserClose.CloseWithoutReply:
                    socket.EnqueueClose();
                    break;
                case FakeBrowserClose.Refuse:
                    socket.ReplyError(command, -32000, "Browser.close is not allowed");
                    break;
            }

            return;
        }

        if (method == "Target.activateTarget")
        {
            var id = command.GetProperty("params").GetProperty("targetId").GetString()!;
            if (_tabs.Any(t => t.Id == id))
            {
                Activated.Add(id);
                socket.ReplyEmpty(command);
            }
            else
            {
                socket.ReplyError(command, -32602, "No target with given id found");
            }

            return;
        }

        if (method == "Target.createTarget")
        {
            var url = command.GetProperty("params").GetProperty("url").GetString()!;
            Created.Add(url);
            var id = $"NEW{Created.Count}";
            if (!HideCreatedTabs)
                _tabs.Add((id, "", url, LastActiveWindow));

            socket.Enqueue(JsonSerializer.Serialize(new { id = command.GetProperty("id").GetInt32(), result = new { targetId = id } }));
            return;
        }

        if (method != "Browser.getWindowForTarget")
        {
            socket.ReplyError(command, -32601, "'" + method + "' wasn't found");
            return;
        }

        var targetId = command.GetProperty("params").GetProperty("targetId").GetString();
        if (Unanswered == null || Unanswered.Contains(targetId!))
            return;

        var tab = _tabs.FirstOrDefault(t => t.Id == targetId);
        if (tab.WindowId is not int windowId || !_windows.TryGetValue(windowId, out var window))
        {
            socket.ReplyError(command, -32000, "Browser window not found");
            return;
        }

        socket.Enqueue(JsonSerializer.Serialize(new
        {
            id = command.GetProperty("id").GetInt32(),
            result = new
            {
                windowId,
                bounds = new { left = window.Left, top = window.Top, width = window.Width, height = window.Height, windowState = window.State }
            }
        }));
    }
}

/// <summary>
/// The sockets of a <see cref="FakeCdpBrowser"/>: its browser socket for the browser's own URL (and any other), and a new
/// page socket for every connection to a tab's URL, as a browser opens one DevTools session per connection.
/// </summary>
public sealed class FakeCdpBrowserSockets : ICdpWebSocketFactory
{
    private const string PagePath = "/devtools/page/";

    private readonly FakeCdpBrowser _browser;
    private readonly List<Uri> _connectedUris = new();

    internal FakeCdpBrowserSockets(FakeCdpBrowser browser)
    {
        _browser = browser;
    }

    /// <summary>Every URL connected to, in order.</summary>
    public IReadOnlyList<Uri> ConnectedUris
    {
        get
        {
            lock (_connectedUris)
            {
                return _connectedUris.ToList();
            }
        }
    }

    public Task<System.Net.WebSockets.WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        lock (_connectedUris)
        {
            _connectedUris.Add(uri);
        }

        System.Net.WebSockets.WebSocket socket = uri.AbsolutePath.StartsWith(PagePath, StringComparison.Ordinal)
            ? _browser.PageSocket(uri.AbsolutePath[PagePath.Length..])
            : _browser.Socket;
        return Task.FromResult(socket);
    }
}

/// <summary>How <see cref="FakeCdpBrowser"/> answers Browser.close.</summary>
public enum FakeBrowserClose
{
    /// <summary>Answers, then ends the connection as it closes.</summary>
    ReplyThenClose,

    /// <summary>Ends the connection before it answers, as a browser that closes at once may.</summary>
    CloseWithoutReply,

    /// <summary>Answers with a CDP error.</summary>
    Refuse,

    /// <summary>Never answers.</summary>
    Never
}
