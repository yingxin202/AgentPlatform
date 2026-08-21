using System.Net.Http;
using System.Text.Json;
using AgentPlatform.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Tools;

/// <summary>
/// 提供内置的图片生成工具，让大模型能够生成图片并发送给用户。
/// 使用 Pollinations.ai 免费服务，无需 API Key。
/// </summary>
public class ImageToolProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageToolProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public const string GenerateImage = "generate_image";

    // 图片结果标记前缀，用于在 Orchestrator 中识别图片类型的结果
    public const string ImageResultMarker = "{\"__image__":true";

    public ImageToolProvider(ILogger<ImageToolProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// 获取图片工具定义
    /// </summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        return new List<ToolDefinition>
        {
            new()
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = GenerateImage,
                    Description = "根据文字描述生成图片。调用此工具后，生成的图片会直接显示在对话中。可以用于生成插画、图表、示意图、艺术图片等。",
                    Parameters = """{"type":"object","properties":{"prompt":{"type":"string","description":"图片描述，越详细越好。建议用英文描述以获得更好效果。例如：a cute cat sitting on a windowsill with sunlight"},"width":{"type":"integer","description":"图片宽度，默认1024"},"height":{"type":"integer","description":"图片高度，默认1024"}},"required":["prompt"]}"""
                }
            }
        };
    }

    /// <summary>
    /// 判断是否为图片工具
    /// </summary>
    public bool IsImageTool(string toolName)
    {
        return toolName == GenerateImage;
    }

    /// <summary>
    /// 判断工具结果是否为图片类型
    /// </summary>
    public static bool IsImageResult(string result)
    {
        return !string.IsNullOrEmpty(result) && result.Contains("\"__image__\"");
    }

    /// <summary>
    /// 从结果中提取 base64 图片数据
    /// </summary>
    public static string? ExtractBase64Image(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("data", out var dataEl))
            {
                return dataEl.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 执行图片工具
    /// </summary>
    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        _logger.LogInformation("执行图片工具: {Tool}", toolName);

        using var args = string.IsNullOrEmpty(argumentsJson)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;

        return toolName switch
        {
            GenerateImage => await ExecuteGenerateImage(root, ct),
            _ => throw new NotSupportedException($"未知的图片工具: {toolName}")
        };
    }

    private async Task<string> ExecuteGenerateImage(JsonElement root, CancellationToken ct)
    {
        var prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
        var width = root.TryGetProperty("width", out var w) ? w.GetInt32() : 1024;
        var height = root.TryGetProperty("height", out var h) ? h.GetInt32() : 1024;

        if (string.IsNullOrEmpty(prompt))
        {
            return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: prompt" }, JsonOptions);
        }

        // 限制尺寸
        width = Math.Clamp(width, 256, 2048);
        height = Math.Clamp(height, 256, 2048);

        try
        {
            // 使用 Pollinations.ai 免费图片生成服务
            var encodedPrompt = Uri.EscapeDataString(prompt);
            var imageUrl = $"https://image.pollinations.ai/prompt/{encodedPrompt}?width={width}&height={height}&nologo=true&model=flux";

            _logger.LogInformation("正在生成图片: {Url}", imageUrl);

            // 下载图片
            var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl, ct);

            if (imageBytes == null || imageBytes.Length == 0)
            {
                return JsonSerializer.Serialize(new { success = false, error = "图片生成返回空数据" }, JsonOptions);
            }

            var base64 = Convert.ToBase64String(imageBytes);

            _logger.LogInformation("图片生成成功，大小: {Size} KB", imageBytes.Length / 1024);

            // 返回特殊格式，Orchestrator 会检测 __image__ 标记
            return JsonSerializer.Serialize(new
            {
                __image__ = true,
                success = true,
                data = base64,
                prompt = prompt,
                size = $"{width}x{height}"
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图片生成失败: {Prompt}", prompt);
            return JsonSerializer.Serialize(new { success = false, error = $"图片生成失败: {ex.Message}" }, JsonOptions);
        }
    }
}
