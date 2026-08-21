using AgentPlatform.Core.Models;

namespace AgentPlatform.Core.LLM;

public interface ILLMClient
{
    Task<Stream> StreamChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken cancellationToken);

    Task<string> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken cancellationToken);
}
