using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>Agent Framework 用的 IChatClient 工廠：端點正規化與實際 HTTP 路徑。</summary>
public sealed class AgentChatClientFactoryTests
{
    private const string ChatCompletionJson =
        """{"id":"c1","object":"chat.completion","created":1,"model":"my-deployment","choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Theory]
    [InlineData("https://x.openai.azure.com")]
    [InlineData("https://x.openai.azure.com/")]
    [InlineData("https://x.openai.azure.com/openai/v1")]
    [InlineData("https://x.openai.azure.com/openai/v1/")]
    public void CreateBaseUri_Azure_WithOrWithoutSuffix_ShouldNormalize(string endpoint)
    {
        var uri = AiChatEndpoint.CreateBaseUri(new AiSettings
        {
            Provider = nameof(AiProvider.AzureOpenAI),
            Endpoint = endpoint,
        });

        Assert.Equal("https://x.openai.azure.com/openai/v1/", uri.ToString());
    }

    [Fact]
    public void CreateBaseUri_OpenAi_EmptyEndpoint_ShouldUseDefault()
    {
        var uri = AiChatEndpoint.CreateBaseUri(new AiSettings
        {
            Provider = nameof(AiProvider.OpenAI),
            Endpoint = "",
        });

        Assert.Equal("https://api.openai.com/v1/", uri.ToString());
    }

    [Fact]
    public async Task CreateOpenAiChatClient_Azure_ShouldPostToV1ChatCompletions()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, ChatCompletionJson);
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Provider = nameof(AiProvider.AzureOpenAI),
            Endpoint = "https://unit-test.openai.azure.com",
            ApiKey = "key",
            Model = "my-deployment",
        };

        var client = AgentChatClientFactory.CreateOpenAiChatClient(settings, http);
        var response = await client.GetResponseAsync("hi");

        Assert.Equal(
            "https://unit-test.openai.azure.com/openai/v1/chat/completions",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("\"model\":\"my-deployment\"", handler.RequestBodies[0]);
        Assert.Equal("pong", response.Text);
    }

    [Fact]
    public void Create_WhenSettingsIncomplete_ShouldThrow()
    {
        var factory = new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = "",
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            new RecordingTokenUsageRecorder(),
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            new CurrentUserService(),
            NullLoggerFactory.Instance);

        Assert.Throws<InvalidOperationException>(() => factory.Create("test", Guid.NewGuid()));
    }
}
