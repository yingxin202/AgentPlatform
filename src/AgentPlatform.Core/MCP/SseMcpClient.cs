using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core;

/// <summary>
/// MCP client that communicates with an MCP server over HTTP/SSE transport.
/// Requests are sent as JSON-RPC 2.0 messages via HTTP POST and the response
/// is read from the HTTP response body. A GET to {url}/sse establishes an SSE
/// stream used to receive server-initiated notifications.
/// </summary>
public class SseMcpClient : IMcpClient, IDisposable
{
    private readonly string _serverName;
    private readonly string _url;
    private readonly Dictionary<string, string> _headers;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    private bool _connected;
    private bool _disposed;
    private int _nextId;
    private CancellationTokenSource _sseCts = new();
    private Task? _sseTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    public string ServerName => _serverName;

    public bool IsConnected => _connected && !_disposed;

    public SseMcpClient(string serverName, string url, Dictionary<string, string> headers, ILogger logger)
    {
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _url = (url ?? string.Empty).TrimEnd('/');
        _headers = headers ?? new Dictionary<string, string>();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var initParams = new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new { name = "AgentPlatform", version = "1.0.0" }
            };

            var result = await PostJsonRpcAsync("initialize", initParams, DefaultTimeout, ct);
            _logger.LogInformation("MCP SSE server {ServerName} initialized: {Result}", _serverName, result.GetRawText());

            _connected = true;

            _sseCts?.Dispose();
            _sseCts = new CancellationTokenSource();
            _sseTask = Task.Run(() => ListenSseAsync(_sseCts.Token), ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP SSE server {ServerName}: connect failed", _serverName);
            _connected = false;
            return false;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return false;
        }

        try
        {
            await PostJsonRpcAsync("ping", null, HealthCheckTimeout, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP SSE server {ServerName}: health check failed", _serverName);
            return false;
        }
    }

    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<McpTool>();
        if (!IsConnected)
        {
            return tools;
        }

        var result = await PostJsonRpcAsync("tools/list", null, DefaultTimeout, ct);

        if (result.TryGetProperty("tools", out var toolsElement))
        {
            foreach (var t in toolsElement.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                var schema = t.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";

                tools.Add(new McpTool
                {
                    Name = name,
                    Description = desc,
                    InputSchema = schema,
                    ServerName = _serverName
                });
            }
        }

        return tools;
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"MCP server {_serverName} is not connected");
        }

        JsonElement arguments = string.IsNullOrWhiteSpace(argumentsJson)
            ? JsonSerializer.Deserialize<JsonElement>("{}")
            : JsonSerializer.Deserialize<JsonElement>(argumentsJson);

        var parameters = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        };

        var result = await PostJsonRpcAsync("tools/call", parameters, DefaultTimeout, ct);

        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var contentArray))
        {
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "text"
                    && item.TryGetProperty("text", out var textEl))
                {
                    sb.Append(textEl.GetString());
                }
            }
        }

        return sb.ToString();
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _sseCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (_sseTask is not null)
        {
            try
            {
                await _sseTask;
            }
            catch
            {
                // ignore SSE task exceptions during disconnect
            }
        }

        _connected = false;
        _logger.LogInformation("MCP SSE server {ServerName} disconnected", _serverName);
    }

    private async Task<JsonElement> PostJsonRpcAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);

        var message = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var endpoint = $"{_url}/{method}";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyHeaders(request);

        using var response = await _httpClient.SendAsync(request, linkedCts.Token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(linkedCts.Token);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            throw new InvalidOperationException($"MCP server {_serverName} returned error: {err.GetRawText()}");
        }

        if (root.TryGetProperty("result", out var result))
        {
            return result.Clone();
        }

        return default;
    }

    private async Task ListenSseAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_url}/sse");
            ApplyHeaders(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                _logger.LogDebug("MCP SSE server {ServerName}: {Line}", _serverName, line);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP SSE server {ServerName}: SSE stream ended", _serverName);
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var header in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connected = false;

        try
        {
            _sseCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
        _sseCts.Dispose();
        _httpClient.Dispose();
    }
}
