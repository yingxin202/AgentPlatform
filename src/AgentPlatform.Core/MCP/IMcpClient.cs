namespace AgentPlatform.Core;

public interface IMcpClient
{
    string ServerName { get; }
    bool IsConnected { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
    Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default);
    Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default);
    Task DisconnectAsync();
}
