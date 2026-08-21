using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Database;
using AgentPlatform.Core.Models;
using AgentPlatform.Core.Skills;
using Microsoft.AspNetCore.Mvc;

namespace AgentPlatform.Api.Controllers;

[ApiController]
[Route("api/skills")]
public class SkillController : ControllerBase
{
    private readonly SkillManager _skillManager;
    private readonly AppConfig _config;
    private readonly DatabaseService _dbService;
    private readonly ILogger<SkillController> _logger;

    public SkillController(
        SkillManager skillManager,
        AppConfig config,
        DatabaseService dbService,
        ILogger<SkillController> logger)
    {
        _skillManager = skillManager;
        _config = config;
        _dbService = dbService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有 Skills 及状态
    /// </summary>
    [HttpGet]
    public IActionResult GetSkills()
    {
        var skills = _skillManager.GetSkillConfigs();
        return Ok(skills);
    }

    /// <summary>
    /// 添加或更新 Skill
    /// </summary>
    [HttpPost]
    public IActionResult AddOrUpdateSkill([FromBody] SkillConfig skillConfig)
    {
        var existing = _config.Skills.FirstOrDefault(s => s.Name == skillConfig.Name);
        if (existing != null)
        {
            var idx = _config.Skills.IndexOf(existing);
            _config.Skills[idx] = skillConfig;
        }
        else
        {
            _config.Skills.Add(skillConfig);
        }

        _dbService.UpsertSkill(skillConfig);
        _logger.LogInformation("Skill {Name} 配置已保存", skillConfig.Name);

        return Ok(new { success = true });
    }

    /// <summary>
    /// 删除 Skill
    /// </summary>
    [HttpDelete("{name}")]
    public IActionResult DeleteSkill(string name)
    {
        var skill = _config.Skills.FirstOrDefault(s => s.Name == name);
        if (skill == null)
        {
            return NotFound(new { success = false, message = $"Skill '{name}' 不存在" });
        }

        _config.Skills.Remove(skill);
        _dbService.DeleteSkill(name);

        return Ok(new { success = true });
    }

    /// <summary>
    /// 切换 Skill 启用状态
    /// </summary>
    [HttpPut("{name}/toggle")]
    public IActionResult ToggleSkill(string name)
    {
        var skill = _config.Skills.FirstOrDefault(s => s.Name == name);
        if (skill == null)
        {
            return NotFound(new { success = false, message = $"Skill '{name}' 不存在" });
        }

        skill.Enabled = !skill.Enabled;
        _dbService.SetSkillEnabled(name, skill.Enabled);
        _logger.LogInformation("Skill {Name} 启用状态已切换为 {Enabled}", name, skill.Enabled);

        return Ok(new { success = true, enabled = skill.Enabled });
    }

    /// <summary>
    /// 重新加载所有 Skills
    /// </summary>
    [HttpPost("reload")]
    public async Task<IActionResult> ReloadSkills()
    {
        try
        {
            await _skillManager.LoadSkillsAsync(_config.Skills, HttpContext.RequestAborted);
            _logger.LogInformation("Skills 已重新加载");
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新加载 Skills 失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
