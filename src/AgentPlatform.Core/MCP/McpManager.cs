using System.Collections.Concurrent;
using AgentPlatform.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core;

public class McpServerStatus
{
    public string Name { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public bool IsEnabled { get; set; }
    public int ToolCount { get; set; }
}

public class McpManager
{
    private readonly Dictionary<string, McpServerConfig> _configs = new();
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new();
    private readonly ConcurrentDictionary<string, int> _toolCounts = new();
    private readonly ILogger _logger;

    public McpManager(List<McpServerConfig> serverConfigs, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var config in serverConfigs)
        {
            _configs[config.Name] = config;
        }
    }

    public async Task StartAllAsync(CancellationToken ct)
    {
        foreach (var config in _configs.Values)
        {
            if (config.Enabled && config.AutoStart)
            {
                await StartServerAsync(config.Name, ct);
            }
        }
    }

    public async Task<bool> StartServerAsync(string name, CancellationToken ct)
    {
        if (!_configs.TryGetValue(name, out var config))
        {
            _logger.LogWarning("MCP server {Name} not found in configuration", name);
            return false;
        }

        if (_clients.TryGetValue(name, out var existing) && existing.IsConnected)
        {
            return true;
        }

        if (existing is not null)
        {
            _clients.TryRemove(name, out _);
            try
            {
                await existing.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP server {Name}: error during cleanup of stale client", name);
            }
        }

        try
        {
            var client = CreateClient(config);
            var connected = await client.ConnectAsync(ct);
            if (connected)
            {
                _clients[name] = client;
                _logger.LogInformation("MCP server {Name} started", name);
            }
            else
            {
                _logger.LogError("MCP server {Name} failed to start", name);
            }
            return connected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP server {Name}: failed to create or connect client", name);
            return false;
        }
    }

    public async Task StopServerAsync(string name)
    {
        if (_clients.TryRemove(name, out var client))
        {
            try
            {
                await client.DisconnectAsync();
                _logger.LogInformation("MCP server {Name} stopped", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server {Name}: error during disconnect", name);
            }
            _toolCounts.TryRemove(name, out _);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var name in _clients.Keys.ToList())
        {
            await StopServerAsync(name);
        }
    }

    public async Task<Dictionary<string, bool>> HealthCheckAllAsync()
    {
        var results = new Dictionary<string, bool>();
        foreach (var kvp in _clients)
        {
            try
            {
                results[kvp.Key] = kvp.Value.IsConnected && await kvp.Value.HealthCheckAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server {Name}: health check failed", kvp.Key);
                results[kvp.Key] = false;
            }
        }
        return results;
    }

    public async Task<List<McpTool>> GetAllToolsAsync()
    {
        var tools = new List<McpTool>();
        foreach (var kvp in _clients)
        {
            if (!kvp.Value.IsConnected)
            {
                continue;
            }

            try
            {
                var serverTools = await kvp.Value.ListToolsAsync();
                _toolCounts[kvp.Key] = serverTools.Count;
                tools.AddRange(serverTools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list tools from server {Name}", kvp.Key);
            }
        }
        return tools;
    }

    public async Task<string> CallToolAsync(string serverName, string toolName, string argumentsJson, CancellationToken ct)
    {
        if (!_clients.TryGetValue(serverName, out var client))
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not running");
        }

        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not connected");
        }

        return await client.CallToolAsync(toolName, argumentsJson, ct);
    }

    public List<McpServerStatus> GetServerStatus()
    {
        var status = new List<McpServerStatus>();
        foreach (var config in _configs.Values)
        {
            var isConnected = _clients.TryGetValue(config.Name, out var client) && client.IsConnected;
            _toolCounts.TryGetValue(config.Name, out var toolCount);
            status.Add(new McpServerStatus
            {
                Name = config.Name,
                IsConnected = isConnected,
                IsEnabled = config.Enabled,
                ToolCount = toolCount
            });
        }
        return status;
    }

    /// <summary>
    /// 运行时添加或更新 MCP 服务器配置
    /// </summary>
    public void AddOrUpdateServerConfig(McpServerConfig config)
    {
        _configs[config.Name] = config;
        _logger.LogInformation("MCP 服务器配置已更新: {Name}", config.Name);
    }

    /// <summary>
    /// 运行时移除 MCP 服务器配置
    /// </summary>
    public async Task RemoveServerConfigAsync(string name)
    {
        await StopServerAsync(name);
        _configs.Remove(name);
    }

    private IMcpClient CreateClient(McpServerConfig config)
    {
        if (config.Transport == "stdio")
        {
            if (string.IsNullOrEmpty(config.Command))
            {
                throw new InvalidOperationException(
                    $"MCP server '{config.Name}': command is required for stdio transport");
            }

            return new StdioMcpClient(
                config.Name,
                config.Command!,
                config.Args,
                config.Env,
                _logger);
        }

        if (config.Transport == "sse" || config.Transport == "http")
        {
            if (string.IsNullOrEmpty(config.Url))
            {
                throw new InvalidOperationException(
                    $"MCP server '{config.Name}': url is required for sse transport");
            }

            return new SseMcpClient(
                config.Name,
                config.Url!,
                config.Headers ?? new Dictionary<string, string>(),
                _logger);
        }

        throw new NotSupportedException(
            $"Transport type '{config.Transport}' is not supported for server '{config.Name}'");
    }
}
