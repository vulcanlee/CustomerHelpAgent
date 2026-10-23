using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Configuration;
using Xunit.Abstractions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Agent Framework 的每一次模型呼叫都要像 AiChatCompletionClient 一樣：先查額度、記 Token 用量、記 AI 對話紀錄。
/// </summary>
public sealed class AuditingChatClientTests(ITestOutputHelper output)
{
    private const string Operation = "測試";

    [Fact]
    public async Task GetResponseAsync_Success_ShouldRecordUsageAndCallLog()
    {
        var convId = Guid.NewGuid();
        var scripted = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            ModelId = "gpt-x",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 },
        });
        var env = new TestEnvironment(scripted);

        var response = await env.Factory.Create(Operation, convId).GetResponseAsync("hi");

        Assert.Equal("ok", response.Text);
        var usage = Assert.Single(env.Usage.Entries);
        Assert.Equal(11, usage.InputCount);
        Assert.Equal(7, usage.OutputCount);
        Assert.Equal(18, usage.TotalCount);
        Assert.True(usage.Success);
        Assert.Equal(Operation, usage.Operation);
        Assert.Equal(7, usage.UserId);
        Assert.Equal("support", usage.Account);
        Assert.Equal("gpt-x", usage.Model);
        Assert.Equal(TokenUsageCallKinds.Chat, usage.CallKind);
        Assert.NotNull(usage.RawUsageJson);

        var log = Assert.Single(env.CallLog.Entries);
        Assert.Equal(convId, log.ConversationId);
        Assert.Equal("ok", log.ResponseText);
        Assert.Equal(200, log.HttpStatus);
        Assert.Equal("stop", log.FinishReason);
        Assert.True(log.Success);
        Assert.Contains("hi", log.RequestBody);
        Assert.Equal(usage.CallId, log.CallId);
    }

    [Fact]
    public async Task GetResponseAsync_QuotaBlocked_ShouldThrowAndAuditWithoutCalling()
    {
        var scripted = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var env = new TestEnvironment(scripted);
        env.Quota.Block = new AiQuotaLimitStatus(
            AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 4), new DateTime(2026, 10, 5));

        var ex = await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => env.Factory.Create(Operation, Guid.NewGuid()).GetResponseAsync("hi"));

        Assert.Contains("已達每人每日 AI 用量上限（NT$ 50，今日已用 NT$ 51.25）", ex.Message);
        Assert.Equal(0, scripted.CallCount);
        Assert.Empty(env.Usage.Entries);
        Assert.Empty(env.CallLog.Entries);
        Assert.Equal([7], env.Quota.Checked);
        var entry = Assert.Single(env.Audit.Entries);
        Assert.Equal((AuditActions.Ai.QuotaBlocked, false, 7, "support"), (entry.Action, entry.Success, entry.ActorUserId, entry.ActorAccount));
        Assert.Equal("operation=測試; limit=50; used=51.25", entry.Detail);
    }

    [Fact]
    public async Task GetResponseAsync_UpstreamFailure_ShouldRecordFailureAndRethrow()
    {
        var scripted = new ScriptedChatClient(new ClientResultException("rate limited", new FakePipelineResponse(429)));
        var env = new TestEnvironment(scripted);

        await Assert.ThrowsAsync<ClientResultException>(
            () => env.Factory.Create(Operation, Guid.NewGuid()).GetResponseAsync("hi"));

        var usage = Assert.Single(env.Usage.Entries);
        Assert.False(usage.Success);
        Assert.Equal("RateLimited", usage.FailureReason);
        var log = Assert.Single(env.CallLog.Entries);
        Assert.Equal(429, log.HttpStatus);
        Assert.False(log.Success);
        Assert.Equal("RateLimited", log.FailureReason);
        Assert.Equal(nameof(ClientResultException), log.ExceptionType);
    }

    [Fact]
    public async Task GetResponseAsync_CallerCanceled_ShouldNotRecordUsage()
    {
        var scripted = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var env = new TestEnvironment(scripted);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => env.Factory.Create(Operation, Guid.NewGuid()).GetResponseAsync("hi", cancellationToken: cts.Token));

        Assert.Empty(env.Usage.Entries);
        var log = Assert.Single(env.CallLog.Entries);
        Assert.False(log.Success);
        Assert.Equal("Canceled", log.FailureReason);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ShouldRecordOnceAfterStreamCompletes()
    {
        var scripted = new ScriptedChatClient(new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, "a"),
            new ChatMessage(ChatRole.Assistant, "b"),
            new ChatMessage(ChatRole.Assistant, "c"),
        ]));
        var env = new TestEnvironment(scripted);

        var texts = new List<string>();
        await foreach (var update in env.Factory.Create(Operation, Guid.NewGuid()).GetStreamingResponseAsync("hi"))
        {
            texts.Add(update.Text);

            // 串流尚未結束前不得記帳。
            Assert.Empty(env.Usage.Entries);
        }

        Assert.Equal("abc", string.Concat(texts));
        Assert.Single(env.Usage.Entries);
        var log = Assert.Single(env.CallLog.Entries);
        Assert.Equal("abc", log.ResponseText);
    }

    [Fact]
    public async Task ChatClientAgent_WithTool_ShouldRecordEachModelCallUnderOneConversation()
    {
        var convId = Guid.NewGuid();
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("call-1", "echo", new Dictionary<string, object?> { ["text"] = "x" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        var env = new TestEnvironment(scripted);

        var agent = new ChatClientAgent(
            env.Factory.Create(Operation, convId),
            instructions: "你是售後客服測試指示",
            tools: [AIFunctionFactory.Create((string text) => text, "echo")]);
        var response = await agent.RunAsync("hi");

        Assert.Equal("done", response.Text);
        Assert.Equal(2, scripted.CallCount);
        Assert.Equal(2, env.Usage.Entries.Count);
        Assert.Equal(2, env.CallLog.Entries.Count);
        Assert.All(env.CallLog.Entries, log => Assert.Equal(convId, log.ConversationId));
        Assert.All(env.CallLog.Entries, log => Assert.Contains("你是售後客服測試指示", log.RequestBody));
        Assert.Contains("echo", env.CallLog.Entries[0].RequestBody);
    }

    /// <summary>
    /// 實機冒煙：只有設定了 CHA_LIVE_AI_* 環境變數才真的連線，否則寫出略過訊息後直接通過。
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSmoke_WhenEnvConfigured_ShouldGetReply()
    {
        var provider = Environment.GetEnvironmentVariable("CHA_LIVE_AI_PROVIDER");
        var endpoint = Environment.GetEnvironmentVariable("CHA_LIVE_AI_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("CHA_LIVE_AI_KEY");
        var model = Environment.GetEnvironmentVariable("CHA_LIVE_AI_MODEL");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
        {
            output.WriteLine("Skipped: CHA_LIVE_AI_* not set (need CHA_LIVE_AI_PROVIDER, CHA_LIVE_AI_KEY, CHA_LIVE_AI_MODEL; CHA_LIVE_AI_ENDPOINT optional for OpenAI).");
            return;
        }

        var env = new TestEnvironment(new AiSettings
        {
            Provider = provider,
            Endpoint = endpoint ?? string.Empty,
            ApiKey = key,
            Model = model,
        });

        var response = await env.Factory.Create(Operation, Guid.NewGuid()).GetResponseAsync("請只回覆：pong");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.True(Assert.Single(env.Usage.Entries).Success);
    }

    private sealed class TestEnvironment
    {
        public TestEnvironment(ScriptedChatClient scripted)
            : this(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = "key",
                Model = "my-deployment",
            })
        {
            Factory.InnerClientFactory = _ => scripted;
        }

        public TestEnvironment(AiSettings settings)
        {
            Factory = new AgentChatClientFactory(
                new StaticOptionsMonitor<AiSettings>(settings),
                new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
                Usage,
                CallLog,
                Quota,
                Audit,
                new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
                NullLoggerFactory.Instance);
        }

        public AgentChatClientFactory Factory { get; }

        public RecordingTokenUsageRecorder Usage { get; } = new();

        public FakeAiCallLogRecorder CallLog { get; } = new();

        public StubAiQuotaService Quota { get; } = new();

        public RecordingAuditLogService Audit { get; } = new();
    }

    /// <summary>依序回傳預設的回應（或丟出預設的例外），並計算被呼叫的次數。</summary>
    private sealed class ScriptedChatClient(params object[] script) : IChatClient
    {
        private readonly Queue<object> script = new(script);

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return script.Dequeue() switch
            {
                ChatResponse response => Task.FromResult(response),
                Exception ex => Task.FromException<ChatResponse>(ex),
                var other => throw new InvalidOperationException($"Unexpected script item {other}"),
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Text) { MessageId = "m1" };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>只為了讓 <see cref="ClientResultException.Status"/> 有值。</summary>
    private sealed class FakePipelineResponse(int status) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Content);

        public override void Dispose()
        {
        }
    }
}
