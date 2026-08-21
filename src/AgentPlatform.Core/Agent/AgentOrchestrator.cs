using System.Text;
using System.Text.Json;
using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.LLM;
using AgentPlatform.Core.Models;
using AgentPlatform.Core.Sessions;
using AgentPlatform.Core.Skills;
using AgentPlatform.Core.Tools;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Agent;

public class AgentOrchestrator
{
    private readonly SessionManager _sessionManager;
    private readonly SkillManager _skillManager;
    private readonly McpManager _mcpManager;
    private readonly AppConfig _config;
    private readonly ILogger<AgentOrchestrator> _logger;

    private const int MaxToolCallRounds = 10;
    private const string DefaultSessionTitle = "New Session";

    public AgentOrchestrator(
        SessionManager sessionManager,
        SkillManager skillManager,
        McpManager mcpManager,
        AppConfig config,
        ILogger<AgentOrchestrator> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _skillManager = skillManager ?? throw new ArgumentNullException(nameof(skillManager));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunChatAsync(
        string sessionId,
        string userInput,
        List<string>? images,
        Func<string, Task> onToken,
        Func<string, Task> onComplete,
        Func<string, Task> onError,
        CancellationToken ct,
        Func<string, string, Task>? onToolStart = null,
        Func<string, string, Task>? onToolResult = null,
        Func<string, string, Task>? onImage = null)
    {
        try
        {
            var session = _sessionManager.GetSession(sessionId);
            if (session is null)
            {
                await onError($"Session '{sessionId}' not found");
                return;
            }

            var userMessage = ChatMessage.CreateUser(userInput, images);
            _sessionManager.AddMessage(sessionId, userMessage);

            if (session.Title == DefaultSessionTitle)
            {
                var title = GenerateTitle(userInput);
                _sessionManager.UpdateSessionTitle(sessionId, title);
            }

            var tools = await _skillManager.GetEnabledToolsAsync();
            var toolDefinitions = tools.Count > 0 ? tools : null;

            var llmClient = LLMClientFactory.CreateClient(_config.Model);

            for (int round = 0; round < MaxToolCallRounds; round++)
            {
                var messages = BuildMessages(sessionId);

                var (content, toolCalls) = await StreamLlmResponseAsync(
                    llmClient, messages, toolDefinitions, onToken, ct);

                if (toolCalls.Count > 0)
                {
                    var assistantMessage = new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.IsNullOrEmpty(content) ? null : content,
                        ToolCalls = toolCalls
                    };
                    _sessionManager.AddMessage(sessionId, assistantMessage);

                    foreach (var toolCall in toolCalls)
                    {
                        _logger.LogInformation(
                            "Executing tool call {Name} (id: {Id})",
                            toolCall.Name, toolCall.Id);

                        // 通知前端：工具开始执行
                        if (onToolStart != null)
                        {
                            await onToolStart(toolCall.Name, toolCall.Arguments);
                        }

                        string toolResult;
                        try
                        {
                            toolResult = await _skillManager.ExecuteSkillAsync(
                                toolCall.Name, toolCall.Arguments, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Tool call {Name} failed", toolCall.Name);
                            toolResult = $"Error: {ex.Message}";
                        }

                        // 通知前端：工具执行完成
                        if (onToolResult != null)
                        {
                            var displayResult = ImageToolProvider.IsImageResult(toolResult)
                                ? "图片已生成"
                                : (toolResult.Length > 500 ? toolResult[..500] + "...(已截断)" : toolResult);
                            await onToolResult(toolCall.Name, displayResult);
                        }

                        // 如果是图片结果，发送图片到前端
                        if (onImage != null && ImageToolProvider.IsImageResult(toolResult))
                        {
                            var base64 = ImageToolProvider.ExtractBase64Image(toolResult);
                            if (!string.IsNullOrEmpty(base64))
                            {
                                await onImage(base64, toolCall.Name);
                            }
                        }

                        var toolMessage = new ChatMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = toolCall.Id,
                            Name = toolCall.Name
                        };
                        _sessionManager.AddMessage(sessionId, toolMessage);
                    }

                    continue;
                }

                var finalMessage = new ChatMessage
                {
                    Role = "assistant",
                    Content = content
                };
                _sessionManager.AddMessage(sessionId, finalMessage);

                await onComplete(content);
                return;
            }

            _logger.LogWarning(
                "Max tool call rounds ({Max}) reached for session {Id}",
                MaxToolCallRounds, sessionId);
            await onComplete(string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RunChatAsync for session {Id}", sessionId);
            await onError(ex.Message);
        }
    }

    private async Task<(string content, List<ToolCall> toolCalls)> StreamLlmResponseAsync(
        ILLMClient client,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Func<string, Task> onToken,
        CancellationToken ct)
    {
        var contentBuilder = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        await foreach (var token in client.StreamChatStreamAsync(messages, tools, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (TryParseToolCallEvent(token, out var toolCall))
            {
                toolCalls.Add(toolCall!);
                _logger.LogDebug("Detected tool call event: {Name}", toolCall!.Name);
            }
            else
            {
                contentBuilder.Append(token);
                await onToken(token);
            }
        }

        return (contentBuilder.ToString(), toolCalls);
    }

    private static bool TryParseToolCallEvent(string token, out ToolCall? toolCall)
    {
        toolCall = null;

        if (!token.StartsWith("{"))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(token);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "tool_call")
            {
                toolCall = new ToolCall
                {
                    Id = GetStringProperty(doc.RootElement, "id"),
                    Name = GetStringProperty(doc.RootElement, "name"),
                    Arguments = GetStringProperty(doc.RootElement, "arguments")
                };
                return true;
            }
        }
        catch
        {
            // Not valid JSON or not a tool call event
        }

        return false;
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private List<ChatMessage> BuildMessages(string sessionId)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystem(_config.SystemPrompt)
        };

        var sessionMessages = _sessionManager.GetMessages(sessionId);

        // 替换图片结果中的 base64 数据，避免浪费 LLM Token
        foreach (var msg in sessionMessages)
        {
            if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.Content) && ImageToolProvider.IsImageResult(msg.Content))
            {
                msg.Content = "{\"__image__\":true,\"success\":true,\"message\":\"图片已生成并显示给用户\"}";
            }
        }

        messages.AddRange(sessionMessages);

        return messages;
    }

    private static string GenerateTitle(string userInput)
    {
        var title = userInput.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return "New Chat";
        }

        if (title.Length > 50)
        {
            title = title.Substring(0, 50) + "...";
        }

        var newlineIndex = title.IndexOf('\n');
        if (newlineIndex > 0)
        {
            title = title.Substring(0, newlineIndex);
        }

        return title;
    }
}
