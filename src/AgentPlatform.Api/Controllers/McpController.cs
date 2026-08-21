using AgentPlatform.Core;
using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Database;
using AgentPlatform.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentPlatform.Api.Controllers;

[ApiController]
[Route("api/mcp")]
public class McpController : ControllerBase
{
    private readonly McpManager _mcpManager;
    private readonly AppConfig _config;
    private readonly DatabaseService _dbService;
    private readonly ILogger<McpController> _logger;

    public McpController(
        McpManager mcpManager,
        AppConfig config,
        DatabaseService dbService,
        ILogger<McpController> logger)
    {
        _mcpManager = mcpManager;
        _config = config;
        _dbService = dbService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有 MCP 服务器状态
    /// </summary>
    [HttpGet("servers")]
    public IActionResult GetServers()
    {
        var status = _mcpManager.GetServerStatus();
        return Ok(status);
    }

    /// <summary>
    /// 添加或更新 MCP 服务器配置
    /// </summary>
    [HttpPost("servers")]
    public IActionResult AddOrUpdateServer([FromBody] McpServerConfig serverConfig)
    {
        var existing = _config.McpServers.FirstOrDefault(s => s.Name == serverConfig.Name);
        if (existing != null)
        {
            var idx = _config.McpServers.IndexOf(existing);
            _config.McpServers[idx] = serverConfig;
        }
        else
        {
            _config.McpServers.Add(serverConfig);
        }

        _dbService.UpsertMcpServer(serverConfig);
        _mcpManager.AddOrUpdateServerConfig(serverConfig);
        _logger.LogInformation("MCP 服务器 {Name} 配置已保存", serverConfig.Name);

        return Ok(new { success = true });
    }

    /// <summary>
    /// 删除 MCP 服务器
    /// </summary>
    [HttpDelete("servers/{name}")]
    public async Task<IActionResult> DeleteServer(string name)
    {
        var server = _config.McpServers.FirstOrDefault(s => s.Name == name);
        if (server == null)
        {
            return NotFound(new { success = false, message = $"服务器 '{name}' 不存在" });
        }

        // 停止服务器并移除配置
        await _mcpManager.RemoveServerConfigAsync(name);

        _config.McpServers.Remove(server);
        _dbService.DeleteMcpServer(name);

        return Ok(new { success = true });
    }

    /// <summary>
    /// 切换 MCP 服务器启用状态
    /// </summary>
    [HttpPut("servers/{name}/toggle")]
    public IActionResult ToggleServer(string name)
    {
        var server = _config.McpServers.FirstOrDefault(s => s.Name == name);
        if (server == null)
        {
            return NotFound(new { success = false, message = $"服务器 '{name}' 不存在" });
        }

        server.Enabled = !server.Enabled;
        _dbService.SetMcpServerEnabled(name, server.Enabled);
        _logger.LogInformation("MCP 服务器 {Name} 启用状态已切换为 {Enabled}", name, server.Enabled);

        return Ok(new { success = true, enabled = server.Enabled });
    }

    /// <summary>
    /// 启动 MCP 服务器
    /// </summary>
    [HttpPost("servers/{name}/start")]
    public async Task<IActionResult> StartServer(string name)
    {
        try
        {
            var success = await _mcpManager.StartServerAsync(name, HttpContext.RequestAborted);
            var status = _mcpManager.GetServerStatus().FirstOrDefault(s => s.Name == name);

            return Ok(new
            {
                success,
                connected = status?.IsConnected ?? false,
                message = success ? "服务器已启动" : "服务器启动失败"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 MCP 服务器 {Name} 失败", name);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 停止 MCP 服务器
    /// </summary>
    [HttpPost("servers/{name}/stop")]
    public async Task<IActionResult> StopServer(string name)
    {
        await _mcpManager.StopServerAsync(name);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 健康检查所有服务器
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> HealthCheck()
    {
        var results = await _mcpManager.HealthCheckAllAsync();
        return Ok(results);
    }

    /// <summary>
    /// 单个服务器健康检查
    /// </summary>
    [HttpGet("servers/{name}/health")]
    public async Task<IActionResult> HealthCheckServer(string name)
    {
        var results = await _mcpManager.HealthCheckAllAsync();
        var healthy = results.TryGetValue(name, out var isHealthy) && isHealthy;
        return Ok(new { name, healthy });
    }

    /// <summary>
    /// 获取所有可用工具
    /// </summary>
    [HttpGet("tools")]
    public async Task<IActionResult> GetTools()
    {
        var tools = await _mcpManager.GetAllToolsAsync();
        return Ok(tools);
    }
}
