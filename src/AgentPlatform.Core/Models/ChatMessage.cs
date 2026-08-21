namespace AgentPlatform.Core.Models;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public List<string> Images { get; set; } = new();
    public string? ToolCallId { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? Name { get; set; }

    public static ChatMessage CreateUser(string content, List<string>? images = null)
    {
        return new ChatMessage
        {
            Role = "user",
            Content = content,
            Images = images ?? new List<string>()
        };
    }

    public static ChatMessage CreateSystem(string content)
    {
        return new ChatMessage
        {
            Role = "system",
            Content = content
        };
    }
}

public record ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}
