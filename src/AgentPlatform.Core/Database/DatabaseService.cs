using System.Text.Json;
using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Models;
using AgentPlatform.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace AgentPlatform.Core.Database;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public DatabaseService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    public void Initialize()
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS mcp_servers (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT UNIQUE NOT NULL,
                        transport TEXT DEFAULT 'stdio',
                        command TEXT,
                        args TEXT DEFAULT '[]',
                        env TEXT DEFAULT '{}',
                        url TEXT,
                        headers TEXT DEFAULT '{}',
                        enabled INTEGER DEFAULT 1,
                        auto_start INTEGER DEFAULT 1,
                        created_at TEXT DEFAULT (datetime('now')),
                        updated_at TEXT DEFAULT (datetime('now'))
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS skills (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT UNIQUE NOT NULL,
                        description TEXT DEFAULT '',
                        type TEXT DEFAULT 'mcp',
                        mcp_server TEXT,
                        mcp_tool TEXT,
                        script_path TEXT,
                        parameters TEXT DEFAULT '{}',
                        enabled INTEGER DEFAULT 1,
                        created_at TEXT DEFAULT (datetime('now')),
                        updated_at TEXT DEFAULT (datetime('now'))
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS model_config (
                        id INTEGER PRIMARY KEY DEFAULT 1,
                        provider TEXT DEFAULT 'openai',
                        base_url TEXT DEFAULT 'https://api.openai.com/v1',
                        api_key TEXT DEFAULT '',
                        model_name TEXT DEFAULT 'gpt-4o',
                        temperature REAL DEFAULT 0.7,
                        max_tokens INTEGER DEFAULT 4096,
                        enable_vision INTEGER DEFAULT 1,
                        timeout_seconds INTEGER DEFAULT 120,
                        system_prompt TEXT DEFAULT 'You are a helpful AI assistant.'
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Insert default model config if empty
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM model_config WHERE id = 1;";
                var count = Convert.ToInt32(cmd.ExecuteScalar());
                if (count == 0)
                {
                    cmd.CommandText = """
                        INSERT INTO model_config (id, provider, base_url, api_key, model_name, temperature, max_tokens, enable_vision, timeout_seconds, system_prompt)
                        VALUES (1, 'openai', 'https://api.openai.com/v1', '', 'gpt-4o', 0.7, 4096, 1, 120, 'You are a helpful AI assistant.');
                        """;
                    cmd.ExecuteNonQuery();
                }
            }

            // Chat sessions table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_sessions (
                        id TEXT PRIMARY KEY,
                        title TEXT DEFAULT 'New Session',
                        created_at TEXT,
                        is_active INTEGER DEFAULT 1
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Chat messages table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_messages (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        role TEXT,
                        content TEXT,
                        images TEXT DEFAULT '[]',
                        tool_call_id TEXT,
                        tool_calls TEXT,
                        name TEXT,
                        seq INTEGER DEFAULT 0,
                        created_at TEXT DEFAULT (datetime('now')),
                        FOREIGN KEY (session_id) REFERENCES chat_sessions(id) ON DELETE CASCADE
                    );
                    """;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ======================== MCP Servers ========================

    public List<McpServerConfig> GetMcpServers()
    {
        var result = new List<McpServerConfig>();

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name, transport, command, args, env, url, headers, enabled, auto_start FROM mcp_servers ORDER BY id;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new McpServerConfig
                {
                    Name = reader.GetString(0),
                    Transport = reader.IsDBNull(1) ? "stdio" : reader.GetString(1),
                    Command = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Args = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new(),
                    Env = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new(),
                    Url = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new(),
                    Enabled = reader.GetInt32(7) == 1,
                    AutoStart = reader.GetInt32(8) == 1
                });
            }
        }

        return result;
    }

    public McpServerConfig? GetMcpServer(string name)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name, transport, command, args, env, url, headers, enabled, auto_start FROM mcp_servers WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", name);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new McpServerConfig
                {
                    Name = reader.GetString(0),
                    Transport = reader.IsDBNull(1) ? "stdio" : reader.GetString(1),
                    Command = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Args = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new(),
                    Env = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new(),
                    Url = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new(),
                    Enabled = reader.GetInt32(7) == 1,
                    AutoStart = reader.GetInt32(8) == 1
                };
            }
        }

        return null;
    }

    public void UpsertMcpServer(McpServerConfig config)
    {
        var argsJson = JsonSerializer.Serialize(config.Args, JsonOptions);
        var envJson = JsonSerializer.Serialize(config.Env, JsonOptions);
        var headersJson = JsonSerializer.Serialize(config.Headers, JsonOptions);
        var enabled = config.Enabled ? 1 : 0;
        var autoStart = config.AutoStart ? 1 : 0;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mcp_servers (name, transport, command, args, env, url, headers, enabled, auto_start, updated_at)
                VALUES (@name, @transport, @command, @args, @env, @url, @headers, @enabled, @autoStart, datetime('now'))
                ON CONFLICT(name) DO UPDATE SET
                    transport = @transport,
                    command = @command,
                    args = @args,
                    env = @env,
                    url = @url,
                    headers = @headers,
                    enabled = @enabled,
                    auto_start = @autoStart,
                    updated_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("@name", config.Name);
            cmd.Parameters.AddWithValue("@transport", config.Transport ?? "stdio");
            cmd.Parameters.AddWithValue("@command", (object?)config.Command ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@args", argsJson);
            cmd.Parameters.AddWithValue("@env", envJson);
            cmd.Parameters.AddWithValue("@url", (object?)config.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@headers", headersJson);
            cmd.Parameters.AddWithValue("@enabled", enabled);
            cmd.Parameters.AddWithValue("@autoStart", autoStart);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteMcpServer(string name)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM mcp_servers WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetMcpServerEnabled(string name, bool enabled)
    {
        var value = enabled ? 1 : 0;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE mcp_servers SET enabled = @enabled, updated_at = datetime('now') WHERE name = @name;";
            cmd.Parameters.AddWithValue("@enabled", value);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }
    }

    // ======================== Skills ========================

    public List<SkillConfig> GetSkills()
    {
        var result = new List<SkillConfig>();

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name, description, type, mcp_server, mcp_tool, script_path, parameters, enabled FROM skills ORDER BY id;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SkillConfig
                {
                    Name = reader.GetString(0),
                    Description = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Type = reader.IsDBNull(2) ? "mcp" : reader.GetString(2),
                    McpServer = reader.IsDBNull(3) ? null : reader.GetString(3),
                    McpTool = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ScriptPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new(),
                    Enabled = reader.GetInt32(7) == 1
                });
            }
        }

        return result;
    }

    public void UpsertSkill(SkillConfig config)
    {
        var parametersJson = JsonSerializer.Serialize(config.Parameters, JsonOptions);
        var enabled = config.Enabled ? 1 : 0;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO skills (name, description, type, mcp_server, mcp_tool, script_path, parameters, enabled, updated_at)
                VALUES (@name, @description, @type, @mcpServer, @mcpTool, @scriptPath, @parameters, @enabled, datetime('now'))
                ON CONFLICT(name) DO UPDATE SET
                    description = @description,
                    type = @type,
                    mcp_server = @mcpServer,
                    mcp_tool = @mcpTool,
                    script_path = @scriptPath,
                    parameters = @parameters,
                    enabled = @enabled,
                    updated_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("@name", config.Name);
            cmd.Parameters.AddWithValue("@description", config.Description ?? "");
            cmd.Parameters.AddWithValue("@type", config.Type ?? "mcp");
            cmd.Parameters.AddWithValue("@mcpServer", (object?)config.McpServer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mcpTool", (object?)config.McpTool ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@scriptPath", (object?)config.ScriptPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@parameters", parametersJson);
            cmd.Parameters.AddWithValue("@enabled", enabled);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteSkill(string name)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM skills WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetSkillEnabled(string name, bool enabled)
    {
        var value = enabled ? 1 : 0;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE skills SET enabled = @enabled, updated_at = datetime('now') WHERE name = @name;";
            cmd.Parameters.AddWithValue("@enabled", value);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }
    }

    // ======================== Model Config ========================

    public ModelConfig GetModelConfig()
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT provider, base_url, api_key, model_name, temperature, max_tokens, enable_vision, timeout_seconds FROM model_config WHERE id = 1;";

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new ModelConfig
                {
                    Provider = reader.IsDBNull(0) ? "openai" : reader.GetString(0),
                    BaseUrl = reader.IsDBNull(1) ? "https://api.openai.com/v1" : reader.GetString(1),
                    ApiKey = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ModelName = reader.IsDBNull(3) ? "gpt-4o" : reader.GetString(3),
                    Temperature = reader.IsDBNull(4) ? 0.7 : reader.GetDouble(4),
                    MaxTokens = reader.IsDBNull(5) ? 4096 : reader.GetInt32(5),
                    EnableVision = reader.IsDBNull(6) ? true : reader.GetInt32(6) == 1,
                    TimeoutSeconds = reader.IsDBNull(7) ? 120 : reader.GetInt32(7)
                };
            }
        }

        return new ModelConfig();
    }

    public void SaveModelConfig(ModelConfig config)
    {
        var enableVision = config.EnableVision ? 1 : 0;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Ensure the singleton row exists (preserves system_prompt if already set)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO model_config (id, provider, base_url, api_key, model_name, temperature, max_tokens, enable_vision, timeout_seconds, system_prompt)
                    VALUES (1, 'openai', 'https://api.openai.com/v1', '', 'gpt-4o', 0.7, 4096, 1, 120, 'You are a helpful AI assistant.');
                    """;
                cmd.ExecuteNonQuery();
            }

            // Update only model-related fields (system_prompt is left untouched)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE model_config SET
                        provider = @provider,
                        base_url = @baseUrl,
                        api_key = @apiKey,
                        model_name = @modelName,
                        temperature = @temperature,
                        max_tokens = @maxTokens,
                        enable_vision = @enableVision,
                        timeout_seconds = @timeoutSeconds
                    WHERE id = 1;
                    """;
                cmd.Parameters.AddWithValue("@provider", config.Provider ?? "openai");
                cmd.Parameters.AddWithValue("@baseUrl", config.BaseUrl ?? "https://api.openai.com/v1");
                cmd.Parameters.AddWithValue("@apiKey", config.ApiKey ?? "");
                cmd.Parameters.AddWithValue("@modelName", config.ModelName ?? "gpt-4o");
                cmd.Parameters.AddWithValue("@temperature", config.Temperature);
                cmd.Parameters.AddWithValue("@maxTokens", config.MaxTokens);
                cmd.Parameters.AddWithValue("@enableVision", enableVision);
                cmd.Parameters.AddWithValue("@timeoutSeconds", config.TimeoutSeconds);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ======================== System Prompt ========================

    public string GetSystemPrompt()
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT system_prompt FROM model_config WHERE id = 1;";

            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                return (string)result;
            }
        }

        return "You are a helpful AI assistant.";
    }

    public void SaveSystemPrompt(string prompt)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE model_config SET system_prompt = @prompt WHERE id = 1;";
            cmd.Parameters.AddWithValue("@prompt", prompt ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    // ======================== Migration ========================

    public void MigrateFromAppConfig(AppConfig appConfig)
    {
        // Migrate MCP servers
        foreach (var server in appConfig.McpServers)
        {
            UpsertMcpServer(server);
        }

        // Migrate skills
        foreach (var skill in appConfig.Skills)
        {
            UpsertSkill(skill);
        }

        // Migrate model config
        if (appConfig.Model != null)
        {
            SaveModelConfig(appConfig.Model);
        }

        // Migrate system prompt
        SaveSystemPrompt(appConfig.SystemPrompt ?? "You are a helpful AI assistant.");
    }

    /// <summary>
    /// Returns true if all data tables are empty (no MCP servers, no skills).
    /// Used to determine whether migration from config.json is needed.
    /// </summary>
    public bool IsEmpty()
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM mcp_servers) + (SELECT COUNT(*) FROM skills);";
            return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
        }
    }

    /// <summary>
    /// Loads all data from the database into an AppConfig object.
    /// </summary>
    public AppConfig LoadAppConfig()
    {
        return new AppConfig
        {
            Model = GetModelConfig(),
            SystemPrompt = GetSystemPrompt(),
            McpServers = GetMcpServers(),
            Skills = GetSkills()
        };
    }

    // ======================== Chat Sessions & Messages ========================

    public void SaveSession(ChatSession session)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO chat_sessions (id, title, created_at, is_active)
                VALUES (@id, @title, @createdAt, @isActive)
                ON CONFLICT(id) DO UPDATE SET
                    title = @title,
                    is_active = @isActive;
                """;
            cmd.Parameters.AddWithValue("@id", session.Id);
            cmd.Parameters.AddWithValue("@title", session.Title ?? "New Session");
            cmd.Parameters.AddWithValue("@createdAt", session.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            cmd.Parameters.AddWithValue("@isActive", session.IsActive ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateSessionTitle(string id, string title)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE chat_sessions SET title = @title WHERE id = @id;";
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteSession(string id)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chat_messages WHERE session_id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DELETE FROM chat_sessions WHERE id = @id;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearSessionMessages(string sessionId)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chat_messages WHERE session_id = @sessionId;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddMessage(string sessionId, ChatMessage message, int seq)
    {
        var imagesJson = JsonSerializer.Serialize(message.Images ?? new List<string>(), JsonOptions);
        var toolCallsJson = message.ToolCalls != null
            ? JsonSerializer.Serialize(message.ToolCalls, JsonOptions)
            : null;

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO chat_messages (session_id, role, content, images, tool_call_id, tool_calls, name, seq)
                VALUES (@sessionId, @role, @content, @images, @toolCallId, @toolCalls, @name, @seq);
                """;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@role", message.Role ?? "user");
            cmd.Parameters.AddWithValue("@content", (object?)message.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@images", imagesJson);
            cmd.Parameters.AddWithValue("@toolCallId", (object?)message.ToolCallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@toolCalls", (object?)toolCallsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@name", (object?)message.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@seq", seq);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ChatSession> LoadAllSessions()
    {
        var sessions = new List<ChatSession>();

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, title, created_at, is_active FROM chat_sessions ORDER BY created_at DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new ChatSession
                {
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "New Session" : reader.GetString(1),
                    CreatedAt = DateTime.TryParse(reader.GetString(2), out var dt) ? dt : DateTime.UtcNow,
                    IsActive = reader.IsDBNull(3) || reader.GetInt32(3) == 1,
                    Messages = new List<ChatMessage>()
                });
            }
        }

        // Load messages for each session
        foreach (var session in sessions)
        {
            session.Messages = LoadMessages(session.Id);
        }

        return sessions;
    }

    public List<ChatMessage> LoadMessages(string sessionId)
    {
        var messages = new List<ChatMessage>();

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT role, content, images, tool_call_id, tool_calls, name FROM chat_messages WHERE session_id = @sessionId ORDER BY seq ASC;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msg = new ChatMessage
                {
                    Role = reader.IsDBNull(0) ? "user" : reader.GetString(0),
                    Content = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Images = reader.IsDBNull(2)
                        ? new List<string>()
                        : (JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new()),
                    ToolCallId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ToolCalls = reader.IsDBNull(4)
                        ? null
                        : JsonSerializer.Deserialize<List<ToolCall>>(reader.GetString(4), JsonOptions),
                    Name = reader.IsDBNull(5) ? null : reader.GetString(5)
                };
                messages.Add(msg);
            }
        }

        return messages;
    }
}
