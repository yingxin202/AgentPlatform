using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core;

/// <summary>
/// MCP client that communicates with an MCP server over stdio (JSON-RPC 2.0
/// over the spawned process's stdin/stdout).
/// </summary>
public class StdioMcpClient : IMcpClient
{
    private readonly string _serverName;
    private readonly string _command;
    private readonly List<string> _args;
    private readonly Dictionary<string, string> _env;
    private readonly ILogger _logger;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private CancellationTokenSource _readerCts = new();
    private Task? _readerTask;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    public string ServerName => _serverName;

    public bool IsConnected => _process is not null && !_process.HasExited;

    public StdioMcpClient(string serverName, string command, List<string> args, Dictionary<string, string> env, ILogger logger)
    {
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _args = args ?? new List<string>();
        _env = env ?? new Dictionary<string, string>();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in _args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            foreach (var kvp in _env)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            _process.ErrorDataReceived += OnErrorData;
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                _logger.LogError("MCP server {ServerName}: failed to start process {Command}", _serverName, _command);
                await CleanupAsync();
                return false;
            }

            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            _stdin.AutoFlush = true;

            _process.BeginErrorReadLine();

            _readerCts?.Dispose();
            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token));

            var initParams = new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new { name = "AgentPlatform", version = "1.0.0" }
            };

            var result = await SendRequestAsync("initialize", initParams, DefaultTimeout, ct);
            _logger.LogInformation("MCP server {ServerName} initialized: {Result}", _serverName, result.GetRawText());

            await SendNotificationAsync("notifications/initialized", null, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP server {ServerName}: connect failed", _serverName);
            await CleanupAsync();
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
            await SendRequestAsync("ping", null, HealthCheckTimeout, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP server {ServerName}: health check failed", _serverName);
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

        var result = await SendRequestAsync("tools/list", null, DefaultTimeout, ct);

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

        var result = await SendRequestAsync("tools/call", parameters, DefaultTimeout, ct);

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
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await SendRequestAsync("shutdown", null, ShutdownTimeout, CancellationToken.None);
                    await SendNotificationAsync("notifications/exit", null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MCP server {ServerName}: graceful shutdown failed, will kill", _serverName);
                }
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var stdin = _stdin;
        if (stdin is null || _process is null || _process.HasExited)
        {
            throw new InvalidOperationException($"MCP server {_serverName} is not connected");
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"MCP server {_serverName} process has exited");
            }

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

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var json = JsonSerializer.Serialize(message, JsonOptions);
            await stdin.WriteLineAsync(json);
            await stdin.FlushAsync(ct);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);

            try
            {
                return await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP request '{method}' to server {_serverName} timed out after {timeout.TotalSeconds}s");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        var stdin = _stdin;
        if (stdin is null)
        {
            return;
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            var message = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            var json = JsonSerializer.Serialize(message, JsonOptions);
            await stdin.WriteLineAsync(json);
            await stdin.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var reader = _stdout;
        if (reader is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server {ServerName}: error reading stdout", _serverName);
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var err))
                        {
                            tcs.TrySetException(new InvalidOperationException(
                                $"MCP server {_serverName} returned error: {err.GetRawText()}"));
                        }
                        else if (root.TryGetProperty("result", out var result))
                        {
                            tcs.TrySetResult(result.Clone());
                        }
                        else
                        {
                            tcs.TrySetResult(default);
                        }
                    }
                }
                else
                {
                    if (root.TryGetProperty("method", out var methodEl))
                    {
                        _logger.LogDebug("MCP server {ServerName}: notification {Method}", _serverName, methodEl.GetString());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server {ServerName}: failed to parse line: {Line}", _serverName, line);
            }
        }

        FailPending(new IOException($"MCP server {_serverName} stdout stream closed"));
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            _logger.LogWarning("MCP server {ServerName} stderr: {Data}", _serverName, e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _logger.LogInformation("MCP server {ServerName} process exited", _serverName);
        FailPending(new IOException($"MCP server {_serverName} process exited"));
    }

    private void FailPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetException(ex);
        }
        _pending.Clear();
    }

    private async Task CleanupAsync()
    {
        try
        {
            _readerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask;
            }
            catch
            {
                // ignore reader task exceptions during cleanup
            }
        }

        _readerCts.Dispose();
        _readerCts = new CancellationTokenSource();

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server {ServerName}: error killing process", _serverName);
            }

            try
            {
                _process.Dispose();
            }
            catch
            {
                // ignore
            }

            _process = null;
        }

        _stdin = null;
        _stdout = null;

        FailPending(new IOException($"MCP server {_serverName} disconnected"));
    }
}
