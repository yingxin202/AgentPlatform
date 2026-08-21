using System.Text.Json;
using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Database;
using AgentPlatform.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Tools;

/// <summary>
/// 提供内置的配置管理工具，让大模型能够通过对话直接读写本地配置。
/// 这些工具作为固有能力，始终可用，不依赖 Skill 配置。
/// </summary>
public class ConfigToolProvider
{
    private readonly DatabaseService _dbService;
    private readonly AppConfig _appConfig;
    private readonly ILogger<ConfigToolProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public const string GetModelConfig = "config_get_model_config";
    public const string UpdateModelConfig = "config_update_model_config";
    public const string GetSystemPrompt = "config_get_system_prompt";
    public const string UpdateSystemPrompt = "config_update_system_prompt";
    public const string ListMcpServers = "config_list_mcp_servers";
    public const string AddMcpServer = "config_add_mcp_server";
    public const string RemoveMcpServer = "config_remove_mcp_server";
    public const string ToggleMcpServer = "config_toggle_mcp_server";
    public const string ListSkills = "config_list_skills";
    public const string AddSkill = "config_add_skill";
    public const string RemoveSkill = "config_remove_skill";

    public ConfigToolProvider(DatabaseService dbService, AppConfig appConfig, ILogger<ConfigToolProvider> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<ToolDefinition> GetToolDefinitions()
    {
        return new List<ToolDefinition>
        {
            new() { Type = "function", Function = new FunctionDefinition { Name = GetModelConfig, Description = "获取当前大模型配置，包括服务商、BaseUrl、API Key、模型名称、Temperature等参数。", Parameters = """{"type":"object","properties":{}}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = UpdateModelConfig, Description = "更新大模型配置。只需传入需要修改的字段，未传入的字段保持不变。修改后立即生效。", Parameters = """{"type":"object","properties":{"provider":{"type":"string","description":"服务商，如 openai、azure、custom"},"baseUrl":{"type":"string","description":"API基础地址，如 https://api.openai.com/v1"},"apiKey":{"type":"string","description":"API密钥"},"modelName":{"type":"string","description":"模型名称，如 gpt-4o"},"temperature":{"type":"number","description":"温度参数 0-2"},"maxTokens":{"type":"integer","description":"最大Token数"},"enableVision":{"type":"boolean","description":"是否启用视觉能力"},"timeoutSeconds":{"type":"integer","description":"超时时间（秒）"}}}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = GetSystemPrompt, Description = "获取当前系统提示词。", Parameters = """{"type":"object","properties":{}}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = UpdateSystemPrompt, Description = "更新系统提示词。这会影响后续所有对话的AI行为。", Parameters = """{"type":"object","properties":{"prompt":{"type":"string","description":"新的系统提示词内容"}},"required":["prompt"]}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = ListMcpServers, Description = "列出所有已配置的MCP服务器及其状态。", Parameters = """{"type":"object","properties":{}}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = AddMcpServer, Description = "添加一个新的MCP服务器配置。", Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"服务器名称"},"transport":{"type":"string","description":"传输方式：stdio 或 sse","enum":["stdio","sse"]},"command":{"type":"string","description":"启动命令"},"args":{"type":"array","items":{"type":"string"},"description":"命令参数数组"},"env":{"type":"object","description":"环境变量","additionalProperties":{"type":"string"}},"url":{"type":"string","description":"服务器URL"},"enabled":{"type":"boolean","description":"是否启用"},"autoStart":{"type":"boolean","description":"是否自动启动"}},"required":["name","transport"]}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = RemoveMcpServer, Description = "删除一个已配置的MCP服务器。", Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"要删除的服务器名称"}},"required":["name"]}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = ToggleMcpServer, Description = "启用或禁用一个MCP服务器。", Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"服务器名称"},"enabled":{"type":"boolean","description":"true=启用"}},"required":["name","enabled"]}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = ListSkills, Description = "列出所有已配置的技能及其状态。", Parameters = """{"type":"object","properties":{}}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = AddSkill, Description = "添加一个新的技能配置。", Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"技能名称"},"description":{"type":"string","description":"技能描述"},"type":{"type":"string","description":"技能类型：mcp 或 script","enum":["mcp","script"]},"mcpServer":{"type":"string","description":"MCP服务器名称"},"mcpTool":{"type":"string","description":"MCP工具名称"},"scriptPath":{"type":"string","description":"脚本文件路径"},"enabled":{"type":"boolean","description":"是否启用"}},"required":["name","type"]}""" } },
            new() { Type = "function", Function = new FunctionDefinition { Name = RemoveSkill, Description = "删除一个已配置的技能。", Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"要删除的技能名称"}},"required":["name"]}""" } },
        };
    }

    public bool IsConfigTool(string toolName) => toolName.StartsWith("config_");

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        _logger.LogInformation("执行配置工具: {Tool}", toolName);
        using var args = string.IsNullOrEmpty(argumentsJson) ? JsonDocument.Parse("{}") : JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;
        return toolName switch
        {
            GetModelConfig => ExecuteGetModelConfig(),
            UpdateModelConfig => ExecuteUpdateModelConfig(root),
            GetSystemPrompt => ExecuteGetSystemPrompt(),
            UpdateSystemPrompt => ExecuteUpdateSystemPrompt(root),
            ListMcpServers => ExecuteListMcpServers(),
            AddMcpServer => ExecuteAddMcpServer(root),
            RemoveMcpServer => ExecuteRemoveMcpServer(root),
            ToggleMcpServer => ExecuteToggleMcpServer(root),
            ListSkills => ExecuteListSkills(),
            AddSkill => ExecuteAddSkill(root),
            RemoveSkill => ExecuteRemoveSkill(root),
            _ => throw new NotSupportedException($"未知的配置工具: {toolName}")
        };
    }

    private string ExecuteGetModelConfig()
    {
        var config = _dbService.GetModelConfig();
        var safeConfig = new { provider = config.Provider, baseUrl = config.BaseUrl, apiKey = MaskApiKey(config.ApiKey), modelName = config.ModelName, temperature = config.Temperature, maxTokens = config.MaxTokens, enableVision = config.EnableVision, timeoutSeconds = config.TimeoutSeconds };
        return JsonSerializer.Serialize(safeConfig, JsonOptions);
    }

    private string ExecuteUpdateModelConfig(JsonElement root)
    {
        var config = _dbService.GetModelConfig();
        if (root.TryGetProperty("provider", out var provider)) config.Provider = provider.GetString() ?? "openai";
        if (root.TryGetProperty("baseUrl", out var baseUrl)) config.BaseUrl = baseUrl.GetString() ?? "";
        if (root.TryGetProperty("apiKey", out var apiKey)) config.ApiKey = apiKey.GetString() ?? "";
        if (root.TryGetProperty("modelName", out var modelName)) config.ModelName = modelName.GetString() ?? "";
        if (root.TryGetProperty("temperature", out var temp)) config.Temperature = temp.GetDouble();
        if (root.TryGetProperty("maxTokens", out var maxTokens)) config.MaxTokens = maxTokens.GetInt32();
        if (root.TryGetProperty("enableVision", out var vision)) config.EnableVision = vision.GetBoolean();
        if (root.TryGetProperty("timeoutSeconds", out var timeout)) config.TimeoutSeconds = timeout.GetInt32();
        _dbService.SaveModelConfig(config);
        SyncToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = "模型配置已更新", config = new { provider = config.Provider, baseUrl = config.BaseUrl, modelName = config.ModelName } }, JsonOptions);
    }

    private string ExecuteGetSystemPrompt()
    {
        var prompt = _dbService.GetSystemPrompt();
        return JsonSerializer.Serialize(new { systemPrompt = prompt }, JsonOptions);
    }

    private string ExecuteUpdateSystemPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("prompt", out var promptEl)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: prompt" }, JsonOptions);
        var prompt = promptEl.GetString() ?? "";
        _dbService.SaveSystemPrompt(prompt);
        _appConfig.SystemPrompt = prompt;
        return JsonSerializer.Serialize(new { success = true, message = "系统提示词已更新" }, JsonOptions);
    }

    private string ExecuteListMcpServers()
    {
        var servers = _dbService.GetMcpServers();
        var result = servers.Select(s => new { name = s.Name, transport = s.Transport, command = s.Command, args = s.Args, url = s.Url, enabled = s.Enabled, autoStart = s.AutoStart });
        return JsonSerializer.Serialize(new { servers = result }, JsonOptions);
    }

    private string ExecuteAddMcpServer(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: name" }, JsonOptions);
        var existing = _dbService.GetMcpServer(name);
        if (existing != null) return JsonSerializer.Serialize(new { success = false, error = $"MCP服务器 '{name}' 已存在" }, JsonOptions);
        var config = new McpServerConfig
        {
            Name = name,
            Transport = root.TryGetProperty("transport", out var t) ? t.GetString() ?? "stdio" : "stdio",
            Command = root.TryGetProperty("command", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
            Args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>(),
            Env = ParseStringDict(root, "env"),
            Url = root.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null,
            Headers = ParseStringDict(root, "headers"),
            Enabled = root.TryGetProperty("enabled", out var e) ? e.GetBoolean() : true,
            AutoStart = root.TryGetProperty("autoStart", out var asEl) ? asEl.GetBoolean() : true
        };
        _dbService.UpsertMcpServer(config);
        SyncMcpServersToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = $"MCP服务器 '{name}' 已添加" }, JsonOptions);
    }

    private string ExecuteRemoveMcpServer(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: name" }, JsonOptions);
        var existing = _dbService.GetMcpServer(name);
        if (existing == null) return JsonSerializer.Serialize(new { success = false, error = $"MCP服务器 '{name}' 不存在" }, JsonOptions);
        _dbService.DeleteMcpServer(name);
        SyncMcpServersToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = $"MCP服务器 '{name}' 已删除" }, JsonOptions);
    }

    private string ExecuteToggleMcpServer(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean();
        if (string.IsNullOrEmpty(name)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: name" }, JsonOptions);
        var existing = _dbService.GetMcpServer(name);
        if (existing == null) return JsonSerializer.Serialize(new { success = false, error = $"MCP服务器 '{name}' 不存在" }, JsonOptions);
        _dbService.SetMcpServerEnabled(name, enabled);
        existing.Enabled = enabled;
        SyncMcpServersToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = $"MCP服务器 '{name}' 已{(enabled ? "启用" : "禁用")}" }, JsonOptions);
    }

    private string ExecuteListSkills()
    {
        var skills = _dbService.GetSkills();
        var result = skills.Select(s => new { name = s.Name, description = s.Description, type = s.Type, mcpServer = s.McpServer, mcpTool = s.McpTool, scriptPath = s.ScriptPath, enabled = s.Enabled });
        return JsonSerializer.Serialize(new { skills = result }, JsonOptions);
    }

    private string ExecuteAddSkill(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: name" }, JsonOptions);
        var config = new SkillConfig
        {
            Name = name,
            Description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            Type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "mcp" : "mcp",
            McpServer = root.TryGetProperty("mcpServer", out var ms) && ms.ValueKind != JsonValueKind.Null ? ms.GetString() : null,
            McpTool = root.TryGetProperty("mcpTool", out var mt) && mt.ValueKind != JsonValueKind.Null ? mt.GetString() : null,
            ScriptPath = root.TryGetProperty("scriptPath", out var sp) && sp.ValueKind != JsonValueKind.Null ? sp.GetString() : null,
            Enabled = root.TryGetProperty("enabled", out var e) ? e.GetBoolean() : true
        };
        _dbService.UpsertSkill(config);
        SyncSkillsToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = $"技能 '{name}' 已添加" }, JsonOptions);
    }

    private string ExecuteRemoveSkill(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: name" }, JsonOptions);
        var skills = _dbService.GetSkills();
        if (!skills.Any(s => s.Name == name)) return JsonSerializer.Serialize(new { success = false, error = $"技能 '{name}' 不存在" }, JsonOptions);
        _dbService.DeleteSkill(name);
        SyncSkillsToAppConfig();
        return JsonSerializer.Serialize(new { success = true, message = $"技能 '{name}' 已删除" }, JsonOptions);
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8) return new string('*', apiKey?.Length ?? 0);
        return apiKey[..4] + "****" + apiKey[^4..];
    }

    private static Dictionary<string, string> ParseStringDict(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>();
            foreach (var prop in el.EnumerateObject()) dict[prop.Name] = prop.Value.GetString() ?? "";
            return dict;
        }
        return new Dictionary<string, string>();
    }

    private void SyncToAppConfig() { _appConfig.Model = _dbService.GetModelConfig(); _appConfig.SystemPrompt = _dbService.GetSystemPrompt(); }
    private void SyncMcpServersToAppConfig() { _appConfig.McpServers = _dbService.GetMcpServers(); }
    private void SyncSkillsToAppConfig() { _appConfig.Skills = _dbService.GetSkills(); }
}
