namespace AgentPlatform.Core.Models;

public class ModelConfig
{
    public string Provider { get; set; } = "openai";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = "gpt-4o";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public bool EnableVision { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 120;
}
