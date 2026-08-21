using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Database;
using Microsoft.AspNetCore.Mvc;

namespace AgentPlatform.Api.Controllers;

public class SettingsRequest
{
    public Core.Models.ModelConfig? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public List<Core.Models.McpServerConfig>? McpServers { get; set; }
    public List<Core.Models.SkillConfig>? Skills { get; set; }
}

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly AppConfig _config;
    private readonly DatabaseService _dbService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(AppConfig config, DatabaseService dbService, ILogger<SettingsController> logger)
    {
        _config = config;
        _dbService = dbService;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前配置
    /// </summary>
    [HttpGet]
    public IActionResult GetSettings()
    {
        return Ok(new
        {
            model = _dbService.GetModelConfig(),
            systemPrompt = _dbService.GetSystemPrompt(),
            mcpServers = _dbService.GetMcpServers(),
            skills = _dbService.GetSkills()
        });
    }

    /// <summary>
    /// 更新配置
    /// </summary>
    [HttpPut]
    public IActionResult UpdateSettings([FromBody] SettingsRequest request)
    {
        try
        {
            if (request.Model != null)
            {
                _config.Model = request.Model;
                _dbService.SaveModelConfig(request.Model);
            }

            if (request.SystemPrompt != null)
            {
                _config.SystemPrompt = request.SystemPrompt;
                _dbService.SaveSystemPrompt(request.SystemPrompt);
            }

            if (request.McpServers != null)
            {
                _config.McpServers = request.McpServers;
                foreach (var server in request.McpServers)
                {
                    _dbService.UpsertMcpServer(server);
                }
            }

            if (request.Skills != null)
            {
                _config.Skills = request.Skills;
                foreach (var skill in request.Skills)
                {
                    _dbService.UpsertSkill(skill);
                }
            }

            _logger.LogInformation("配置已更新并保存到数据库");

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 仅更新模型配置
    /// </summary>
    [HttpPut("model")]
    public IActionResult UpdateModel([FromBody] Core.Models.ModelConfig model)
    {
        _config.Model = model;
        _dbService.SaveModelConfig(model);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 仅更新系统提示词
    /// </summary>
    [HttpPut("system-prompt")]
    public IActionResult UpdateSystemPrompt([FromBody] string systemPrompt)
    {
        _config.SystemPrompt = systemPrompt;
        _dbService.SaveSystemPrompt(systemPrompt);
        return Ok(new { success = true });
    }
}
