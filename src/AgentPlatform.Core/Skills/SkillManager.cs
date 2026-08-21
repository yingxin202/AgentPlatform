using System.Diagnostics;
using System.Text.Json;
using AgentPlatform.Core.Models;
using AgentPlatform.Core.Tools;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Skills;

public class SkillConfigWithStatus
{
    public SkillConfig Config { get; set; } = new();
    public string Status { get; set; } = "loaded";
}

public class SkillManager
{
    private readonly McpManager _mcpManager;
    private readonly ConfigToolProvider _configTools;
    private readonly WebToolProvider _webTools;
    private readonly ImageToolProvider _imageTools;
    private readonly ILogger<SkillManager> _logger;
    private readonly List<SkillConfig> _configs = new();
    private readonly Dictionary<string, string> _skillStatuses = new();

    private const string EchoParametersSchema =
        """{"type":"object","properties":{"message":{"type":"string","description":"Message to echo back"}},"required":["message"]}""";

    private const string EmptyParametersSchema =
        """{"type":"object","properties":{}}""";

    public SkillManager(McpManager mcpManager, ConfigToolProvider configTools, WebToolProvider webTools, ImageToolProvider imageTools, ILogger<SkillManager> logger)
    {
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _configTools = configTools ?? throw new ArgumentNullException(nameof(configTools));
        _webTools = webTools ?? throw new ArgumentNullException(nameof(webTools));
        _imageTools = imageTools ?? throw new ArgumentNullException(nameof(imageTools));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task LoadSkillsAsync(List<SkillConfig> configs, CancellationToken ct = default)
    {
        _configs.Clear();
        _skillStatuses.Clear();

        foreach (var config in configs)
        {
            _configs.Add(config);
            _skillStatuses[config.Name] = "loaded";

            if (!config.Enabled)
            {
                _skillStatuses[config.Name] = "disabled";
                _logger.LogInformation("Skill {Name}: disabled", config.Name);
                continue;
            }

            switch (config.Type)
            {
                case "mcp":
                    await VerifyMcpSkillAsync(config, ct);
                    break;
                case "builtin":
                    _skillStatuses[config.Name] = "ready";
                    _logger.LogInformation("Skill {Name}: loaded builtin skill", config.Name);
                    break;
                case "script":
                    if (string.IsNullOrEmpty(config.ScriptPath))
                    {
                        _skillStatuses[config.Name] = "invalid_config";
                        _logger.LogWarning("Skill {Name}: missing ScriptPath", config.Name);
                    }
                    else
                    {
                        _skillStatuses[config.Name] = "ready";
                        _logger.LogInformation("Skill {Name}: loaded script skill at {Path}", config.Name, config.ScriptPath);
                    }
                    break;
                default:
                    _skillStatuses[config.Name] = "unknown_type";
                    _logger.LogWarning("Skill {Name}: unknown type {Type}", config.Name, config.Type);
                    break;
            }
        }
    }

    private async Task VerifyMcpSkillAsync(SkillConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.McpServer) || string.IsNullOrEmpty(config.McpTool))
        {
            _skillStatuses[config.Name] = "invalid_config";
            _logger.LogWarning("Skill {Name}: missing McpServer or McpTool", config.Name);
            return;
        }

        try
        {
            await _mcpManager.StartServerAsync(config.McpServer!, ct);

            var tools = await _mcpManager.GetAllToolsAsync();
            var toolExists = tools.Any(t => t.Name == config.McpTool && t.ServerName == config.McpServer);

            if (!toolExists)
            {
                toolExists = tools.Any(t => t.Name == config.McpTool);
            }

            if (!toolExists)
            {
                _skillStatuses[config.Name] = "tool_not_found";
                _logger.LogWarning(
                    "Skill {Name}: MCP tool {Tool} not found on server {Server}",
                    config.Name, config.McpTool, config.McpServer);
            }
            else
            {
                _skillStatuses[config.Name] = "ready";
                _logger.LogInformation(
                    "Skill {Name}: loaded MCP tool {Tool} from server {Server}",
                    config.Name, config.McpTool, config.McpServer);
            }
        }
        catch (Exception ex)
        {
            _skillStatuses[config.Name] = "error";
            _logger.LogError(ex, "Skill {Name}: failed to verify MCP tool", config.Name);
        }
    }

    public async Task<List<ToolDefinition>> GetEnabledToolsAsync()
    {
        var tools = new List<ToolDefinition>();

        // 始终添加内置配置管理工具（固有能力）
        tools.AddRange(_configTools.GetToolDefinitions());

        // 始终添加内置网络工具（固有能力）
        tools.AddRange(_webTools.GetToolDefinitions());

        // 始终添加内置图片生成工具（固有能力）
        tools.AddRange(_imageTools.GetToolDefinitions());

        foreach (var config in _configs)
        {
            if (!config.Enabled)
            {
                continue;
            }

            if (_skillStatuses.TryGetValue(config.Name, out var status) && status != "ready")
            {
                continue;
            }

            switch (config.Type)
            {
                case "mcp":
                    await AddMcpToolDefinitionAsync(tools, config);
                    break;
                case "builtin":
                    AddBuiltinToolDefinition(tools, config);
                    break;
                case "script":
                    AddScriptToolDefinition(tools, config);
                    break;
            }
        }

        return tools;
    }

    private async Task AddMcpToolDefinitionAsync(List<ToolDefinition> tools, SkillConfig config)
    {
        try
        {
            var allTools = await _mcpManager.GetAllToolsAsync();
            var mcpTool = allTools.FirstOrDefault(t => t.Name == config.McpTool);

            if (mcpTool is null)
            {
                _logger.LogWarning("Skill {Name}: MCP tool {Tool} not found, skipping", config.Name, config.McpTool);
                return;
            }

            tools.Add(new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = config.Name,
                    Description = string.IsNullOrEmpty(mcpTool.Description)
                        ? config.Description
                        : mcpTool.Description,
                    Parameters = string.IsNullOrEmpty(mcpTool.InputSchema)
                        ? EmptyParametersSchema
                        : mcpTool.InputSchema
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tool definition for skill {Name}", config.Name);
        }
    }

    private void AddBuiltinToolDefinition(List<ToolDefinition> tools, SkillConfig config)
    {
        var parameters = config.Name switch
        {
            "echo" => EchoParametersSchema,
            _ => EmptyParametersSchema
        };

        tools.Add(new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = config.Name,
                Description = config.Description,
                Parameters = parameters
            }
        });
    }

    private void AddScriptToolDefinition(List<ToolDefinition> tools, SkillConfig config)
    {
        tools.Add(new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = config.Name,
                Description = config.Description,
                Parameters = EmptyParametersSchema
            }
        });
    }

    public async Task<string> ExecuteSkillAsync(string skillName, string argumentsJson, CancellationToken ct)
    {
        // 内置配置管理工具
        if (_configTools.IsConfigTool(skillName))
        {
            return await _configTools.ExecuteAsync(skillName, argumentsJson, ct);
        }

        // 内置网络工具
        if (_webTools.IsWebTool(skillName))
        {
            return await _webTools.ExecuteAsync(skillName, argumentsJson, ct);
        }

        // 内置图片工具
        if (_imageTools.IsImageTool(skillName))
        {
            return await _imageTools.ExecuteAsync(skillName, argumentsJson, ct);
        }

        var config = _configs.FirstOrDefault(c => c.Name == skillName);
        if (config is null)
        {
            throw new InvalidOperationException($"Skill '{skillName}' not found");
        }

        if (!config.Enabled)
        {
            throw new InvalidOperationException($"Skill '{skillName}' is disabled");
        }

        return config.Type switch
        {
            "mcp" => await ExecuteMcpSkillAsync(config, argumentsJson, ct),
            "builtin" => await ExecuteBuiltinSkillAsync(config, argumentsJson),
            "script" => await ExecuteScriptSkillAsync(config, argumentsJson, ct),
            _ => throw new NotSupportedException($"Skill type '{config.Type}' is not supported")
        };
    }

    private async Task<string> ExecuteMcpSkillAsync(SkillConfig config, string argumentsJson, CancellationToken ct)
    {
        _logger.LogInformation(
            "Executing MCP skill {Name} on server {Server} tool {Tool}",
            config.Name, config.McpServer, config.McpTool);

        return await _mcpManager.CallToolAsync(
            config.McpServer!, config.McpTool!, argumentsJson, ct);
    }

    private Task<string> ExecuteBuiltinSkillAsync(SkillConfig config, string argumentsJson)
    {
        _logger.LogInformation("Executing builtin skill {Name}", config.Name);

        var result = config.Name switch
        {
            "echo" => argumentsJson,
            _ => throw new NotSupportedException($"Builtin skill '{config.Name}' is not supported")
        };

        return Task.FromResult(result);
    }

    private async Task<string> ExecuteScriptSkillAsync(SkillConfig config, string argumentsJson, CancellationToken ct)
    {
        _logger.LogInformation("Executing script skill {Name}: {Path}", config.Name, config.ScriptPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = config.ScriptPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(argumentsJson))
        {
            try
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
                if (args is not null)
                {
                    foreach (var kvp in args)
                    {
                        startInfo.ArgumentList.Add($"--{kvp.Key}");
                        startInfo.ArgumentList.Add(kvp.Value);
                    }
                }
            }
            catch (JsonException)
            {
                startInfo.ArgumentList.Add(argumentsJson);
            }
        }

        foreach (var param in config.Parameters)
        {
            startInfo.ArgumentList.Add($"--{param.Key}");
            startInfo.ArgumentList.Add(param.Value);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Script '{config.ScriptPath}' exited with code {process.ExitCode}. Error: {error}");
        }

        return output;
    }

    public List<SkillConfigWithStatus> GetSkillConfigs()
    {
        return _configs.Select(c => new SkillConfigWithStatus
        {
            Config = c,
            Status = _skillStatuses.TryGetValue(c.Name, out var status) ? status : "unknown"
        }).ToList();
    }
}
