using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPlatform.Core.Models;

namespace AgentPlatform.Core.Configuration;

public class AppConfig
{
    public ModelConfig Model { get; set; } = new();
    public List<McpServerConfig> McpServers { get; set; } = new();
    public List<SkillConfig> Skills { get; set; } = new();
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static AppConfig LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public void SaveToFile(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}
