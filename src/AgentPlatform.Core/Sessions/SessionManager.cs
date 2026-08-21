using System.Collections.Concurrent;
using AgentPlatform.Core.Database;
using AgentPlatform.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Sessions;

public class ChatSession
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = "New Session";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

public class SessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int MessageCount { get; set; }
}

public class SessionManager
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly DatabaseService _dbService;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(DatabaseService dbService, ILogger<SessionManager> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 从数据库加载所有会话
        LoadFromDatabase();
    }

    private void LoadFromDatabase()
    {
        try
        {
            var sessions = _dbService.LoadAllSessions();
            foreach (var session in sessions)
            {
                _sessions[session.Id] = session;
            }
            _logger.LogInformation("从数据库加载了 {Count} 个会话", sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从数据库加载会话失败");
        }
    }

    public string CreateSession()
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString(),
            Title = "New Session",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _sessions[session.Id] = session;

        // 持久化到数据库
        try
        {
            _dbService.SaveSession(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存会话到数据库失败");
        }

        _logger.LogInformation("Created session {Id}", session.Id);
        return session.Id;
    }

    public ChatSession? GetSession(string id)
    {
        _sessions.TryGetValue(id, out var session);
        return session;
    }

    public List<SessionSummary> GetAllSessions()
    {
        return _sessions.Values
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionSummary
            {
                Id = s.Id,
                Title = s.Title,
                CreatedAt = s.CreatedAt,
                MessageCount = s.Messages.Count
            })
            .ToList();
    }

    public bool AddMessage(string sessionId, ChatMessage message)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogWarning("Session {Id} not found", sessionId);
            return false;
        }

        lock (session.Messages)
        {
            session.Messages.Add(message);

            // 持久化消息到数据库
            try
            {
                var seq = session.Messages.Count - 1;
                _dbService.AddMessage(sessionId, message, seq);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存消息到数据库失败");
            }
        }

        return true;
    }

    public List<ChatMessage> GetMessages(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return new List<ChatMessage>();
        }

        lock (session.Messages)
        {
            return session.Messages.ToList();
        }
    }

    public bool ClearSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogWarning("Session {Id} not found", sessionId);
            return false;
        }

        lock (session.Messages)
        {
            session.Messages.Clear();
        }

        // 清空数据库中的消息
        try
        {
            _dbService.ClearSessionMessages(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清空数据库消息失败");
        }

        _logger.LogInformation("Cleared messages for session {Id}", sessionId);
        return true;
    }

    public bool DeleteSession(string id)
    {
        var removed = _sessions.TryRemove(id, out _);
        if (removed)
        {
            // 从数据库删除
            try
            {
                _dbService.DeleteSession(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从数据库删除会话失败");
            }

            _logger.LogInformation("Deleted session {Id}", id);
        }
        return removed;
    }

    public bool UpdateSessionTitle(string id, string title)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return false;
        }

        session.Title = title;

        // 更新数据库
        try
        {
            _dbService.UpdateSessionTitle(id, title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新数据库会话标题失败");
        }

        _logger.LogDebug("Updated title for session {Id}: {Title}", id, title);
        return true;
    }
}
