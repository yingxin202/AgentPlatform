using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentPlatform.Core.Models;

namespace AgentPlatform.Core.LLM;

public class OpenAICompatibleClient : ILLMClient
{
    private readonly ModelConfig _config;
    private readonly HttpClient _httpClient;

    public OpenAICompatibleClient(ModelConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(messages, tools, stream: false);
        var request = CreateRequest(requestBody);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"LLM 请求失败: {response.StatusCode} ({(int)response.StatusCode})\n" +
                $"请求URL: {request.RequestUri}\n" +
                $"响应: {errorBody}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseContent);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var messageElement = choices[0].GetProperty("message");
        if (messageElement.TryGetProperty("content", out var contentElement))
        {
            return contentElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public async Task<Stream> StreamChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(messages, tools, stream: true);
        var request = CreateRequest(requestBody);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamChatStreamAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(messages, tools, stream: true);
        var request = CreateRequest(requestBody);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"LLM 请求失败: {response.StatusCode} ({(int)response.StatusCode})\n" +
                $"请求URL: {request.RequestUri}\n" +
                $"响应: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var toolCallAccumulator = new Dictionary<int, ToolCallAccumulator>();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (!line.StartsWith("data: "))
            {
                continue;
            }

            var data = line.Substring(6);
            if (data == "[DONE]")
            {
                break;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var delta = choices[0].GetProperty("delta");

            if (delta.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.GetString();
                if (!string.IsNullOrEmpty(content))
                {
                    yield return content;
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsElement))
            {
                foreach (var tc in toolCallsElement.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();

                    if (!toolCallAccumulator.TryGetValue(index, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        toolCallAccumulator[index] = acc;
                    }

                    if (tc.TryGetProperty("id", out var idElement))
                    {
                        acc.Id = idElement.GetString() ?? acc.Id;
                    }

                    if (tc.TryGetProperty("function", out var functionElement))
                    {
                        if (functionElement.TryGetProperty("name", out var nameElement))
                        {
                            acc.Name = nameElement.GetString() ?? acc.Name;
                        }
                        if (functionElement.TryGetProperty("arguments", out var argsElement))
                        {
                            acc.Arguments += argsElement.GetString() ?? "";
                        }
                    }
                }
            }
        }

        foreach (var kvp in toolCallAccumulator.OrderBy(x => x.Key))
        {
            var acc = kvp.Value;
            if (!string.IsNullOrEmpty(acc.Id))
            {
                var toolCallEvent = new
                {
                    type = "tool_call",
                    id = acc.Id,
                    name = acc.Name,
                    arguments = acc.Arguments
                };
                yield return JsonSerializer.Serialize(toolCallEvent);
            }
        }
    }

    private HttpRequestMessage CreateRequest(string requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl.TrimEnd('/')}/chat/completions");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        return request;
    }

    private string BuildRequestBody(List<ChatMessage> messages, List<ToolDefinition>? tools, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _config.ModelName,
            ["messages"] = messages.Select(BuildMessageObject).ToList(),
            ["temperature"] = _config.Temperature,
            ["max_tokens"] = _config.MaxTokens,
            ["stream"] = stream
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = t.Type,
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Function.Name,
                    ["description"] = t.Function.Description,
                    ["parameters"] = JsonSerializer.Deserialize<JsonElement>(t.Function.Parameters)
                }
            }).ToList();
        }

        return JsonSerializer.Serialize(body);
    }

    private static object BuildMessageObject(ChatMessage message)
    {
        var msg = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };

        if (message.Images != null && message.Images.Count > 0)
        {
            var contentArray = new List<object>();

            if (!string.IsNullOrEmpty(message.Content))
            {
                contentArray.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = message.Content
                });
            }

            foreach (var image in message.Images)
            {
                contentArray.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = image
                    }
                });
            }

            msg["content"] = contentArray;
        }
        else
        {
            msg["content"] = message.Content ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(message.Name))
        {
            msg["name"] = message.Name;
        }

        if (!string.IsNullOrEmpty(message.ToolCallId))
        {
            msg["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            msg["tool_calls"] = message.ToolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tc.Name,
                    ["arguments"] = tc.Arguments
                }
            }).ToList();
        }

        return msg;
    }

    private class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }
}
