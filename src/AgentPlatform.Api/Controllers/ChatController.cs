using AgentPlatform.Core.Agent;
using AgentPlatform.Core.Sessions;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentPlatform.Api.Controllers;

public class ChatRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<string>? Images { get; set; }
}

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<ChatController> _logger;

    // 每个会话的取消令牌
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> CancellationTokenSources = new();

    public ChatController(
        AgentOrchestrator orchestrator,
        SessionManager sessionManager,
        ILogger<ChatController> logger)
    {
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// 创建新会话
    /// </summary>
    [HttpPost("session")]
    public IActionResult CreateSession()
    {
        var sessionId = _sessionManager.CreateSession();
        return Ok(new { sessionId });
    }

    /// <summary>
    /// 获取所有会话列表
    /// </summary>
    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        var sessions = _sessionManager.GetAllSessions();
        return Ok(sessions);
    }

    /// <summary>
    /// 获取会话消息
    /// </summary>
    [HttpGet("messages/{sessionId}")]
    public IActionResult GetMessages(string sessionId)
    {
        var messages = _sessionManager.GetMessages(sessionId);
        return Ok(messages);
    }

    /// <summary>
    /// 删除会话
    /// </summary>
    [HttpDelete("session/{id}")]
    public IActionResult DeleteSession(string id)
    {
        var success = _sessionManager.DeleteSession(id);
        CancellationTokenSources.TryRemove(id, out _);
        return Ok(new { success });
    }

    /// <summary>
    /// 清空会话消息
    /// </summary>
    [HttpPost("clear/{sessionId}")]
    public IActionResult ClearSession(string sessionId)
    {
        var success = _sessionManager.ClearSession(sessionId);
        return Ok(new { success });
    }

    /// <summary>
    /// 停止生成
    /// </summary>
    [HttpPost("stop/{sessionId}")]
    public IActionResult StopGeneration(string sessionId)
    {
        if (CancellationTokenSources.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _logger.LogInformation("已停止会话 {Id} 的生成", sessionId);
            return Ok(new { success = true });
        }
        return Ok(new { success = false, message = "没有正在进行的生成" });
    }

    /// <summary>
    /// 发送消息并流式返回响应 (SSE)
    /// </summary>
    [HttpPost("send")]
    public async Task SendChat([FromBody] ChatRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        // 为当前会话创建取消令牌
        var cts = new CancellationTokenSource();
        CancellationTokenSources[request.SessionId] = cts;

        try
        {
            await _orchestrator.RunChatAsync(
                request.SessionId,
                request.Message,
                request.Images,
                onToken: async (token) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "token", content = token });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                },
                onComplete: async (content) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "complete", content });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                },
                onError: async (error) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "error", content = error });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                },
                ct: cts.Token,
                onToolStart: async (toolName, args) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "tool_start", name = toolName, arguments = args });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                },
                onToolResult: async (toolName, result) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "tool_result", name = toolName, result });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                },
                onImage: async (base64Data, toolName) =>
                {
                    var data = JsonSerializer.Serialize(new { type = "image", data = base64Data, name = toolName });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("会话 {Id} 的生成已被用户取消", request.SessionId);
            var data = JsonSerializer.Serialize(new { type = "complete", content = "[已停止]" });
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话 {Id} 发生错误", request.SessionId);
            var data = JsonSerializer.Serialize(new { type = "error", content = ex.Message });
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }
        finally
        {
            CancellationTokenSources.TryRemove(request.SessionId, out _);
            cts.Dispose();
        }
    }
}
