namespace AgentPlatform.Core.Models;

public class SkillConfig
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = "mcp";
    public string? McpServer { get; set; }
    public string? McpTool { get; set; }
    public string? ScriptPath { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
}
