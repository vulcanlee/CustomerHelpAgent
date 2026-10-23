using System.Net;
using System.Text.Json;
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
    private const string ResponsesJson =
        """{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"my-deployment","output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","annotations":[],"text":"pong"}]}],"parallel_tool_calls":true,"tools":[],"store":false,"usage":{"input_tokens":1,"input_tokens_details":{"cached_tokens":0},"output_tokens":1,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":2}}""";

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

        var client = AgentChatClientFactory.CreateOpenAiChatClient(settings, AgentApiStyle.ChatCompletions, http);
        var response = await client.GetResponseAsync("hi");

        Assert.Equal(
            "https://unit-test.openai.azure.com/openai/v1/chat/completions",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("\"model\":\"my-deployment\"", handler.RequestBodies[0]);
        Assert.Equal("pong", response.Text);
    }

    [Fact]
    public async Task Create_Responses_ShouldPostToV1Responses_WithoutServerStorage_AndWithReasoningEffort()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, ResponsesJson);
        using var http = new HttpClient(handler);

        var client = CreateFactory(http).Create("test", Guid.NewGuid(), AgentApiStyle.Responses, "low");
        var response = await client.GetResponseAsync("hi", new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "noop")] });

        Assert.Equal("https://unit-test.openai.azure.com/openai/v1/responses", handler.Requests[0].RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal("my-deployment", root.GetProperty("model").GetString());
        // 不用服務端儲存：會話歷史由 MAF AgentSession 保存，回應也就不會帶回對話 Id。
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal("noop", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("pong", response.Text);
        Assert.Null(response.ConversationId);
    }

    [Fact]
    public async Task Create_ChatCompletions_WithReasoningEffort_ShouldSendReasoningEffort()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, ChatCompletionJson);
        using var http = new HttpClient(handler);

        var client = CreateFactory(http).Create("test", Guid.NewGuid(), AgentApiStyle.ChatCompletions, "none");
        await client.GetResponseAsync("hi", new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "noop")] });

        Assert.Equal("https://unit-test.openai.azure.com/openai/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData(AgentApiStyle.Responses, "reasoning")]
    [InlineData(AgentApiStyle.ChatCompletions, "reasoning_effort")]
    public async Task Create_EmptyReasoningEffort_ShouldNotSendReasoning(AgentApiStyle apiStyle, string property)
    {
        var handler = StubHttpMessageHandler.Json(
            HttpStatusCode.OK, apiStyle == AgentApiStyle.Responses ? ResponsesJson : ChatCompletionJson);
        using var http = new HttpClient(handler);

        await CreateFactory(http).Create("test", Guid.NewGuid(), apiStyle, "").GetResponseAsync("hi");

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty(property, out _));
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

    private static AgentChatClientFactory CreateFactory(HttpClient http)
        => new(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = "key",
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            new RecordingTokenUsageRecorder(),
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            new CurrentUserService(),
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (settings, apiStyle) => AgentChatClientFactory.CreateOpenAiChatClient(settings, apiStyle, http),
        };
}
