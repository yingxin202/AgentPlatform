using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentPlatform.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Core.Tools;

/// <summary>
/// 提供内置的网络工具，让大模型能够搜索互联网和抓取网页内容。
/// 这些工具作为固有能力，始终可用。
/// </summary>
public class WebToolProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebToolProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string WebDownloadImage = "web_download_image";

    public WebToolProvider(ILogger<WebToolProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>
    /// 获取所有网络工具定义
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
                    Name = WebSearch,
                    Description = "搜索互联网获取信息。返回搜索结果列表，每条包含标题、摘要和链接。使用此工具查找最新资讯、技术方案、问题解决方案等。",
                    Parameters = """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"},"count":{"type":"integer","description":"返回结果数量，默认5，最大10"}},"required":["query"]}"""
                }
            },
            new()
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = WebFetch,
                    Description = "抓取指定URL的网页内容，返回纯文本。用于读取网页详情、API文档、技术文章等。会自动去除HTML标签和无关内容。",
                    Parameters = """{"type":"object","properties":{"url":{"type":"string","description":"要抓取的网页URL"},"maxLength":{"type":"integer","description":"返回内容的最大字符数，默认4000"}},"required":["url"]}"""
                }
            },
            new()
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = WebDownloadImage,
                    Description = "从指定URL下载图片并显示在对话中。支持 jpg、png、gif、webp 等常见图片格式。下载完成后图片会直接显示给用户。",
                    Parameters = """{"type":"object","properties":{"url":{"type":"string","description":"图片的URL地址"}},"required":["url"]}"""
                }
            }
        };
    }

    /// <summary>
    /// 判断是否为网络工具
    /// </summary>
    public bool IsWebTool(string toolName)
    {
        return toolName == WebSearch || toolName == WebFetch || toolName == WebDownloadImage;
    }

    /// <summary>
    /// 执行网络工具
    /// </summary>
    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        _logger.LogInformation("执行网络工具: {Tool}", toolName);

        using var args = string.IsNullOrEmpty(argumentsJson)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;

        return toolName switch
        {
            WebSearch => await ExecuteWebSearch(root, ct),
            WebFetch => await ExecuteWebFetch(root, ct),
            WebDownloadImage => await ExecuteWebDownloadImage(root, ct),
            _ => throw new NotSupportedException($"未知的网络工具: {toolName}")
        };
    }

    // ======================== 网络搜索 ========================

    private async Task<string> ExecuteWebSearch(JsonElement root, CancellationToken ct)
    {
        var query = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var count = root.TryGetProperty("count", out var c) ? Math.Min(c.GetInt32(), 10) : 5;

        if (string.IsNullOrEmpty(query))
        {
            return JsonSerializer.Serialize(new { error = "缺少必需参数: query" }, JsonOptions);
        }

        try
        {
            // 使用 DuckDuckGo HTML 搜索
            var searchUrl = $"https://html.duckduckgo.com/html/?q={WebUtility.UrlEncode(query)}";
            var html = await _httpClient.GetStringAsync(searchUrl, ct);

            var results = ParseDuckDuckGoResults(html, count);

            if (results.Count == 0)
            {
                // 备选：尝试 Bing 搜索
                results = await SearchBingAsync(query, count, ct);
            }

            if (results.Count == 0)
            {
                return JsonSerializer.Serialize(new { query, message = "未找到相关结果" }, JsonOptions);
            }

            return JsonSerializer.Serialize(new { query, results }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网络搜索失败: {Query}", query);
            return JsonSerializer.Serialize(new { error = $"搜索失败: {ex.Message}" }, JsonOptions);
        }
    }

    private List<SearchResult> ParseDuckDuckGoResults(string html, int maxCount)
    {
        var results = new List<SearchResult>();

        // 解析 DuckDuckGo HTML 搜索结果
        var linkPattern = new Regex(
            @"<a[^>]+class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline);

        var snippetPattern = new Regex(
            @"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline);

        var linkMatches = linkPattern.Matches(html);
        var snippetMatches = snippetPattern.Matches(html);

        for (int i = 0; i < Math.Min(linkMatches.Count, maxCount); i++)
        {
            var url = linkMatches[i].Groups[1].Value;
            var titleHtml = linkMatches[i].Groups[2].Value;
            var snippetHtml = i < snippetMatches.Count ? snippetMatches[i].Groups[1].Value : "";

            // DuckDuckGo 的链接可能是重定向链接
            url = ExtractDuckDuckGoUrl(url);

            results.Add(new SearchResult
            {
                Title = StripHtml(titleHtml).Trim(),
                Url = url,
                Snippet = StripHtml(snippetHtml).Trim()
            });
        }

        return results;
    }

    private static string ExtractDuckDuckGoUrl(string url)
    {
        // DuckDuckGo 使用 //duckduckgo.com/l/?uddg=ENCODER_URL&rtn=1 格式
        if (url.Contains("uddg="))
        {
            var match = Regex.Match(url, @"uddg=([^&]+)");
            if (match.Success)
            {
                return WebUtility.UrlDecode(match.Groups[1].Value);
            }
        }
        return url;
    }

    private async Task<List<SearchResult>> SearchBingAsync(string query, int maxCount, CancellationToken ct)
    {
        var results = new List<SearchResult>();

        try
        {
            var searchUrl = $"https://www.bing.com/search?q={WebUtility.UrlEncode(query)}&count={maxCount}";
            var html = await _httpClient.GetStringAsync(searchUrl, ct);

            // 解析 Bing 搜索结果
            var resultPattern = new Regex(
                @"<li class=""b_algo"">(.*?)</li>",
                RegexOptions.Singleline);

            var linkPattern = new Regex(
                @"<h2><a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);

            var snippetPattern = new Regex(
                @"<p[^>]*>(.*?)</p>",
                RegexOptions.Singleline);

            var resultMatches = resultPattern.Matches(html);

            foreach (Match resultMatch in resultMatches)
            {
                if (results.Count >= maxCount) break;

                var block = resultMatch.Groups[1].Value;
                var linkMatch = linkPattern.Match(block);
                var snippetMatch = snippetPattern.Match(block);

                if (linkMatch.Success)
                {
                    results.Add(new SearchResult
                    {
                        Title = StripHtml(linkMatch.Groups[2].Value).Trim(),
                        Url = linkMatch.Groups[1].Value,
                        Snippet = snippetMatch.Success ? StripHtml(snippetMatch.Groups[1].Value).Trim() : ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bing 搜索失败");
        }

        return results;
    }

    // ======================== 网页抓取 ========================

    private async Task<string> ExecuteWebFetch(JsonElement root, CancellationToken ct)
    {
        var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var maxLength = root.TryGetProperty("maxLength", out var ml) ? ml.GetInt32() : 4000;

        if (string.IsNullOrEmpty(url))
        {
            return JsonSerializer.Serialize(new { error = "缺少必需参数: url" }, JsonOptions);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return JsonSerializer.Serialize(new { error = "无效的URL格式" }, JsonOptions);
        }

        try
        {
            var response = await _httpClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            string content;
            if (contentType.Contains("json"))
            {
                content = await response.Content.ReadAsStringAsync(ct);
                // 对 JSON 内容进行格式化
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    content = JsonSerializer.Serialize(doc.RootElement, JsonOptions);
                }
                catch { /* 保持原始内容 */ }
            }
            else if (contentType.Contains("html"))
            {
                var html = await response.Content.ReadAsStringAsync(ct);
                content = ExtractTextFromHtml(html);
            }
            else if (contentType.Contains("text") || contentType.Contains("xml"))
            {
                content = await response.Content.ReadAsStringAsync(ct);
            }
            else
            {
                content = $"[不支持的Content-Type: {contentType}]";
            }

            // 截断到最大长度
            if (content.Length > maxLength)
            {
                content = content[..maxLength] + "\n\n... [内容已截断，原始长度: " + content.Length + " 字符]";
            }

            return JsonSerializer.Serialize(new
            {
                url,
                title = ExtractTitleFromHtml(content),
                content
            }, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "网页抓取失败: {Url}", url);
            return JsonSerializer.Serialize(new { error = $"请求失败: {ex.Message}" }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网页抓取异常: {Url}", url);
            return JsonSerializer.Serialize(new { error = $"抓取失败: {ex.Message}" }, JsonOptions);
        }
    }

    // ======================== 图片下载 ========================

    private async Task<string> ExecuteWebDownloadImage(JsonElement root, CancellationToken ct)
    {
        var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(url))
        {
            return JsonSerializer.Serialize(new { success = false, error = "缺少必需参数: url" }, JsonOptions);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return JsonSerializer.Serialize(new { success = false, error = "无效的URL格式" }, JsonOptions);
        }

        try
        {
            _logger.LogInformation("下载图片: {Url}", url);

            var response = await _httpClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            // 验证是否为图片
            var imageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/gif", "image/webp",
                "image/bmp", "image/svg+xml", "image/tiff", "image/x-icon"
            };

            if (!imageTypes.Contains(contentType))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"URL返回的不是图片 (Content-Type: {contentType})。请确认URL直接指向图片文件。"
                }, JsonOptions);
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync(ct);

            if (imageBytes == null || imageBytes.Length == 0)
            {
                return JsonSerializer.Serialize(new { success = false, error = "下载的图片为空" }, JsonOptions);
            }

            // 限制图片大小 (10MB)
            if (imageBytes.Length > 10 * 1024 * 1024)
            {
                return JsonSerializer.Serialize(new { success = false, error = "图片太大 (>10MB)，无法处理" }, JsonOptions);
            }

            var base64 = Convert.ToBase64String(imageBytes);

            _logger.LogInformation("图片下载成功: {Url}, 大小: {Size} KB", url, imageBytes.Length / 1024);

            // 返回 __image__ 格式，复用现有的图片显示机制
            return JsonSerializer.Serialize(new
            {
                __image__ = true,
                success = true,
                data = base64,
                source = "download",
                url = url,
                contentType = contentType,
                size = $"{imageBytes.Length / 1024} KB"
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图片下载失败: {Url}", url);
            return JsonSerializer.Serialize(new { error = $"图片下载失败: {ex.Message}" }, JsonOptions);
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // 移除 script 和 style 标签及内容
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // 移除所有 HTML 标签
        html = Regex.Replace(html, @"<[^>]+>", " ");

        // 解码 HTML 实体
        html = WebUtility.HtmlDecode(html);

        // 清理多余空白
        html = Regex.Replace(html, @"\s+", " ");

        return html.Trim();
    }

    private static string ExtractTextFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // 移除 script 和 style
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[^>]*>.*?</nav>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[^>]*>.*?</footer>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // 提取 body 内容
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (bodyMatch.Success)
        {
            html = bodyMatch.Groups[1].Value;
        }

        // 在块级元素前后添加换行
        html = Regex.Replace(html, @"</?(p|div|h[1-6]|br|li|tr|table)[^>]*>", "\n", RegexOptions.IgnoreCase);

        // 移除所有 HTML 标签
        html = Regex.Replace(html, @"<[^>]+>", "");

        // 解码 HTML 实体
        html = WebUtility.HtmlDecode(html);

        // 清理多余空白，但保留段落结构
        var lines = html.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToArray();

        return string.Join("\n", lines);
    }

    private static string ExtractTitleFromHtml(string content)
    {
        var match = Regex.Match(content, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            return StripHtml(match.Groups[1].Value).Trim();
        }
        return "";
    }

    // ======================== 搜索结果模型 ========================

    private class SearchResult
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Snippet { get; set; } = "";
    }
}
