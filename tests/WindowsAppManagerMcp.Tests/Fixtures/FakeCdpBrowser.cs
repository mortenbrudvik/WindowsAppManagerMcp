using System.Net;
using System.Text;
using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-memory debug browser for the window binding (roadmap M5.3): /json/version and /json/list over a fake HTTP
/// handler, and a browser-level WebSocket that answers Browser.getWindowForTarget from the windows its tabs are in. No
/// network is used.
/// </summary>
public sealed class FakeCdpBrowser
{
    private readonly int _port;
    private readonly List<(string Id, string Title, string Url, int? WindowId)> _tabs = new();
    private readonly Dictionary<int, (int Left, int Top, int Width, int Height, string State)> _windows = new();

    public FakeCdpBrowser(int port = 9222)
    {
        _port = port;
        Socket = new FakeCdpWebSocket(Respond);
        Sockets = new FakeCdpWebSocketFactory(Socket);
        Http = new FakeCdpHttpHandler(Answer);
    }

    /// <summary>The browser-level DevTools connection's socket.</summary>
    public FakeCdpWebSocket Socket { get; }

    /// <summary>Hands out <see cref="Socket"/>.</summary>
    public FakeCdpWebSocketFactory Sockets { get; }

    /// <summary>Answers /json/version and /json/list.</summary>
    public FakeCdpHttpHandler Http { get; }

    /// <summary>When true, /json/version answers as a program that is not a browser's DevTools endpoint.</summary>
    public bool NotABrowsersVersion { get; set; }

    /// <summary>
    /// The tabs whose Browser.getWindowForTarget is never answered (a reply lost to the time budget); null leaves every
    /// one unanswered, as a browser connection that stalls.
    /// </summary>
    public HashSet<string>? Unanswered { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The browser WebSocket URL /json/version lists.</summary>
    public string BrowserUrl => $"ws://127.0.0.1:{_port}/devtools/browser/B1";

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
        if (command.GetProperty("method").GetString() != "Browser.getWindowForTarget")
        {
            socket.ReplyError(command, -32601, "'" + command.GetProperty("method").GetString() + "' wasn't found");
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
