using AgentPlatform.Core;
using AgentPlatform.Core.Agent;
using AgentPlatform.Core.Configuration;
using AgentPlatform.Core.Database;
using AgentPlatform.Core.Sessions;
using AgentPlatform.Core.Skills;
using AgentPlatform.Core.Tools;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// 配置文件路径
var configPath = Path.Combine(builder.Environment.ContentRootPath, "config.json");
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "agentplatform.db");

// 创建并初始化数据库
var databaseService = new DatabaseService(dbPath);
databaseService.Initialize();

// 如果 config.json 存在且数据库为空，则从 config.json 迁移数据
if (File.Exists(configPath) && databaseService.IsEmpty())
{
    var fileConfig = AppConfig.LoadFromFile(configPath);
    databaseService.MigrateFromAppConfig(fileConfig);
    Console.WriteLine("已从 config.json 迁移配置到数据库");
}

// 从数据库加载配置
var appConfig = databaseService.LoadAppConfig();

// 注册服务
builder.Services.AddSingleton(databaseService);
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<ConfigToolProvider>();
builder.Services.AddSingleton<WebToolProvider>();
builder.Services.AddSingleton<ImageToolProvider>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<McpManager>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<McpManager>>();
    return new McpManager(sp.GetRequiredService<AppConfig>().McpServers, logger);
});
builder.Services.AddSingleton<SkillManager>();
builder.Services.AddSingleton<AgentOrchestrator>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddOpenApi();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");

// 启动时自动加载 MCP 服务和 Skills
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var config = sp.GetRequiredService<AppConfig>();
    var mcpManager = sp.GetRequiredService<McpManager>();
    var skillManager = sp.GetRequiredService<SkillManager>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    // 启动所有自动启动的 MCP 服务
    try
    {
        await mcpManager.StartAllAsync(CancellationToken.None);
        var status = mcpManager.GetServerStatus();
        foreach (var s in status)
        {
            logger.LogInformation("MCP 服务器 {Name}: 连接={Connected}, 启用={Enabled}, 工具数={ToolCount}",
                s.Name, s.IsConnected, s.IsEnabled, s.ToolCount);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "启动 MCP 服务时出错");
    }

    // 加载 Skills
    try
    {
        await skillManager.LoadSkillsAsync(config.Skills, CancellationToken.None);
        logger.LogInformation("已加载 {Count} 个 Skills", config.Skills.Count);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "加载 Skills 时出错");
    }
}

app.Run();
