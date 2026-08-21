namespace AgentPlatform.Core.Models;

public class McpServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = true;
}
