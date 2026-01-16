using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for interacting with browsers via Chrome DevTools Protocol (CDP).
/// Uses HTTP to query targets and WebSocket to execute commands.
/// </summary>
public class CdpService : ICdpService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public CdpService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsDebugPortAvailableAsync(int port = 9222)
    {
        try
        {
            var response = await _httpClient.GetAsync($"http://localhost:{port}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CdpTarget>> GetTargetsAsync(int port = 9222)
    {
        try
        {
            var response = await _httpClient.GetAsync($"http://localhost:{port}/json");
            if (!response.IsSuccessStatusCode)
                return Array.Empty<CdpTarget>();

            var targets = await response.Content.ReadFromJsonAsync<List<CdpTargetResponse>>(_jsonOptions);
            if (targets == null)
                return Array.Empty<CdpTarget>();

            return targets
                .Where(t => t.Type == "page")
                .Select(t => new CdpTarget(
                    Id: t.Id ?? "",
                    Type: t.Type ?? "",
                    Title: t.Title ?? "",
                    Url: t.Url ?? "",
                    WebSocketDebuggerUrl: t.WebSocketDebuggerUrl))
                .ToList();
        }
        catch
        {
            return Array.Empty<CdpTarget>();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetUrlAsync(string targetId, int port = 9222)
    {
        var targets = await GetTargetsAsync(port);
        var target = targets.FirstOrDefault(t => t.Id == targetId);
        return target?.Url;
    }

    /// <inheritdoc />
    public async Task<string?> GetPageContentAsync(string targetId, int port = 9222, bool textOnly = true)
    {
        try
        {
            var targets = await GetTargetsAsync(port);
            var target = targets.FirstOrDefault(t => t.Id == targetId);

            if (target?.WebSocketDebuggerUrl == null)
                return null;

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), CancellationToken.None);

            string script;
            if (textOnly)
            {
                // Extract text content using innerText
                script = "document.body.innerText";
            }
            else
            {
                // Get full HTML
                script = "document.documentElement.outerHTML";
            }

            var result = await ExecuteScriptAsync(ws, script);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetPageTitleAsync(string targetId, int port = 9222)
    {
        try
        {
            var targets = await GetTargetsAsync(port);
            var target = targets.FirstOrDefault(t => t.Id == targetId);

            if (target?.WebSocketDebuggerUrl == null)
                return target?.Title;

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), CancellationToken.None);

            return await ExecuteScriptAsync(ws, "document.title");
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<CdpTarget?> FindTargetByTitleAsync(string windowTitle, int port = 9222)
    {
        var targets = await GetTargetsAsync(port);

        // Try exact match first
        var match = targets.FirstOrDefault(t =>
            windowTitle.Contains(t.Title, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return match;

        // Try partial match on URL
        match = targets.FirstOrDefault(t =>
            windowTitle.Contains(GetDomainFromUrl(t.Url), StringComparison.OrdinalIgnoreCase));

        return match;
    }

    private static string GetDomainFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }

    private static async Task<string?> ExecuteScriptAsync(ClientWebSocket ws, string script)
    {
        var messageId = 1;
        var command = new
        {
            id = messageId,
            method = "Runtime.evaluate",
            @params = new
            {
                expression = script,
                returnByValue = true
            }
        };

        var json = JsonSerializer.Serialize(command);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        // Receive response
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        var result = new StringBuilder();

        while (true)
        {
            var received = await ws.ReceiveAsync(buffer, CancellationToken.None);
            result.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));

            if (received.EndOfMessage)
                break;
        }

        // Parse response
        try
        {
            using var doc = JsonDocument.Parse(result.ToString());
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var resultProp) &&
                resultProp.TryGetProperty("result", out var valueProp) &&
                valueProp.TryGetProperty("value", out var value))
            {
                return value.GetString();
            }
        }
        catch
        {
            // Parse error
        }

        return null;
    }

    private class CdpTargetResponse
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string? WebSocketDebuggerUrl { get; set; }
    }
}
