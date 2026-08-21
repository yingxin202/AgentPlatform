using AgentPlatform.Core.Models;

namespace AgentPlatform.Core.LLM;

public static class LLMClientFactory
{
    public static ILLMClient CreateClient(ModelConfig config)
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        return new OpenAICompatibleClient(config, httpClient);
    }
}
