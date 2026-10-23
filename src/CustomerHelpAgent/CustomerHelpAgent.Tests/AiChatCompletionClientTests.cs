using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 共用的 Chat Completions 呼叫核心（0.9.68 起由 AI 日誌分析與 AI 例外分析共用）。
///
/// HTTP 錯誤對應的細部行為由 <see cref="AiLogAnalysisServiceTests"/> 經由日誌分析守門；
/// 這裡只釘住「共用化之後才有的」契約：多訊息、作業名稱、呼叫端提供的提示文字。
/// </summary>
public sealed class AiChatCompletionClientTests
{
    private const string SuccessPayload = """
        {
          "model": "gpt-4o-2024-11-20",
          "choices": [ { "finish_reason": "stop", "message": { "content": "## 管理者摘要\n沒事。" } } ],
          "usage": { "prompt_tokens": 120, "completion_tokens": 45, "total_tokens": 165 }
        }
        """;

    [Fact]
    public async Task CompleteAsync_ShouldSendAllMessagesInOrder()
    {
        var (client, handler, _) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        var result = await client.CompleteAsync(CreateRequest(
            new AiChatMessage(AiChatRoles.System, "sys"),
            new AiChatMessage(AiChatRoles.User, "q1"),
            new AiChatMessage(AiChatRoles.Assistant, "a1"),
            new AiChatMessage(AiChatRoles.User, "q2")));

        Assert.True(result.Success);
        Assert.Equal("## 管理者摘要\n沒事。", result.Markdown);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var roles = document.RootElement.GetProperty("messages").EnumerateArray()
            .Select(item => item.GetProperty("role").GetString());
        Assert.Equal(["system", "user", "assistant", "user"], roles);
    }

    [Fact]
    public async Task CompleteAsync_ShouldRecordUsage_WithCallerOperation()
    {
        var (client, _, recorder) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, entry.Operation);
        Assert.Equal(TokenUsageCallKinds.Chat, entry.CallKind);
        Assert.True(entry.Success);
        Assert.Equal(120, entry.InputCount);
    }

    [Fact]
    public async Task CompleteAsync_ShouldUseCallerMessage_WhenContextLengthExceeded()
    {
        const string payload = """
            { "error": { "message": "too long", "type": "invalid_request_error", "code": "context_length_exceeded" } }
            """;
        var (client, _, _) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, payload));

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(AiAnalysisFailureReason.UpstreamError, result.Reason);
        Assert.Equal("CALLER-CONTEXT-MESSAGE", result.ErrorMessage);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNotRecord_WhenCanceledByCaller()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, recorder) = CreateClient(handler, callLogRecorder: callLog);

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")), cts.Token);

        Assert.Equal(AiAnalysisFailureReason.Canceled, result.Reason);
        Assert.Empty(recorder.Entries);

        // Token 用量不記取消，但請求確實送出過：AI 對話紀錄照記（0.9.72 起）。
        var logged = Assert.Single(callLog.Entries);
        Assert.False(logged.Success);
        Assert.Equal(nameof(AiAnalysisFailureReason.Canceled), logged.FailureReason);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNotSend_WhenSettingsIncomplete()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, recorder) = CreateClient(handler, new AiSettings(), callLog);

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(AiAnalysisFailureReason.NotConfigured, result.Reason);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(recorder.Entries);
        // 沒送出就不記：MarkSent 之前的失敗不留對話紀錄。
        Assert.Empty(callLog.Entries);
    }

    [Fact]
    public async Task CompleteAsync_ShouldRecordFullConversation_WithSameCallIdAsUsage()
    {
        var callLog = new FakeAiCallLogRecorder();
        var (client, handler, recorder) = CreateClient(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), callLogRecorder: callLog);
        var conversationId = Guid.NewGuid();

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")) with
        {
            RelatedInfo = "例外紀錄 #42（NullReferenceException）",
            ConversationId = conversationId,
        });

        var usage = Assert.Single(recorder.Entries);
        var logged = Assert.Single(callLog.Entries);
        Assert.NotNull(usage.CallId);
        Assert.Equal(usage.CallId, logged.CallId);
        Assert.True(logged.Success);
        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, logged.Operation);
        Assert.Equal("例外紀錄 #42（NullReferenceException）", logged.RelatedInfo);
        Assert.Equal(conversationId, logged.ConversationId);
        Assert.Equal(Assert.Single(handler.RequestBodies), logged.RequestBody);
        Assert.Equal(SuccessPayload, logged.ResponseBody);
        Assert.Equal("## 管理者摘要\n沒事。", logged.ResponseText);
        Assert.Equal(200, logged.HttpStatus);
        Assert.Equal("stop", logged.FinishReason);
        Assert.Equal("my-deployment", logged.RequestedModel);
        Assert.Equal("gpt-4o-2024-11-20", logged.Model);
        Assert.Equal("support", logged.Account);
        Assert.Equal(7, logged.UserId);
    }

    [Fact]
    public async Task CompleteAsync_ShouldKeepUpstreamErrorBody_InCallLog()
    {
        const string payload = """
            { "error": { "message": "slow down", "type": "rate_limit", "code": "rate_limit_exceeded" } }
            """;
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, _) = CreateClient(
            StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, payload), callLogRecorder: callLog);

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        var logged = Assert.Single(callLog.Entries);
        Assert.False(logged.Success);
        Assert.Equal(nameof(AiAnalysisFailureReason.RateLimited), logged.FailureReason);
        Assert.Equal(429, logged.HttpStatus);
        Assert.Equal(payload, logged.ResponseBody);
    }

    [Fact]
    public async Task CompleteAsync_HttpFailure_ShouldGiveStatusAndCodes_ButNotUpstreamMessage()
    {
        // 0.9.130 起：管理員按錯誤通知的「複製」會帶上 ErrorDetail。上游的 error.message 可能回吐 prompt，不可放進去。
        const string payload = """
            { "error": { "message": "slow down", "type": "too_many_requests", "code": "no_capacity", "param": null } }
            """;
        var (client, _, _) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, payload));

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal("HTTP 429; code=no_capacity; type=too_many_requests", result.ErrorDetail);
    }

    public static TheoryData<Exception, string> TransportFailures => new()
    {
        { new TaskCanceledException(), nameof(AiAnalysisFailureReason.Timeout) },
        { new HttpRequestException("down"), nameof(AiAnalysisFailureReason.UpstreamError) },
    };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task CompleteAsync_ShouldRecordTransportFailure_WithExceptionType(Exception exception, string expectedReason)
    {
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, recorder) = CreateClient(StubHttpMessageHandler.Throws(exception), callLogRecorder: callLog);

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        var logged = Assert.Single(callLog.Entries);
        Assert.Equal(expectedReason, logged.FailureReason);
        Assert.Equal(exception.GetType().Name, logged.ExceptionType);
        Assert.Null(logged.HttpStatus);
        Assert.Null(logged.ResponseBody);
        Assert.Equal(Assert.Single(recorder.Entries).CallId, logged.CallId);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task CompleteAsync_TransportFailure_ShouldGiveTheExceptionAsDetail(Exception exception, string expectedReason)
    {
        var (client, _, _) = CreateClient(StubHttpMessageHandler.Throws(exception));

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(expectedReason, result.Reason.ToString());
        Assert.Contains(exception.GetType().FullName!, result.ErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ShouldRecordUnexpectedFailure_OnlyInCallLog()
    {
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, recorder) = CreateClient(
            StubHttpMessageHandler.Throws(new InvalidOperationException("boom")), callLogRecorder: callLog);

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(AiAnalysisFailureReason.Unexpected, result.Reason);
        // Token 用量維持既有規則：非預期錯誤不記。
        Assert.Empty(recorder.Entries);
        var logged = Assert.Single(callLog.Entries);
        Assert.Equal(nameof(AiAnalysisFailureReason.Unexpected), logged.FailureReason);
        Assert.Equal(nameof(InvalidOperationException), logged.ExceptionType);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNeverCaptureApiKey()
    {
        const string sentinel = "SENTINEL-API-KEY-7f3a";
        var callLog = new FakeAiCallLogRecorder();
        var (client, _, _) = CreateClient(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload),
            new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com/openai/v1",
                ApiKey = sentinel,
                Model = "my-deployment",
            },
            callLog);

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        var logged = Assert.Single(callLog.Entries);
        Assert.DoesNotContain(sentinel, logged.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, logged.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(logged), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnResult_WhenCallLogRecorderThrows()
    {
        var (client, _, _) = CreateClient(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload),
            callLogRecorder: new ThrowingAiCallLogRecorder());

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        // 記錄點在 finally：丟出的例外若沒被接住，會把這次成功的呼叫變成例外。
        Assert.True(result.Success);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNotRecordCallLog_WhenDisabled()
    {
        var callLog = new FakeAiCallLogRecorder { IsEnabled = false };
        var (client, _, recorder) = CreateClient(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), callLogRecorder: callLog);

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Single(recorder.Entries);
        Assert.Empty(callLog.Entries);
    }

    private static AiChatCompletionRequest CreateRequest(params AiChatMessage[] messages) => new()
    {
        Operation = TokenUsageOperations.AiExceptionAnalysis,
        Messages = messages,
        ContextLengthExceededMessage = "CALLER-CONTEXT-MESSAGE",
        TimeoutHint = "請稍後再試。",
        SubmittedContentLabel = "送出的內容",
    };

    private static (AiChatCompletionClient Client, StubHttpMessageHandler Handler, RecordingTokenUsageRecorder Recorder)
        CreateClient(StubHttpMessageHandler handler, AiSettings? settings = null, IAiCallLogRecorder? callLogRecorder = null)
    {
        var recorder = new RecordingTokenUsageRecorder();
        var client = new AiChatCompletionClient(
            NullLogger<AiChatCompletionClient>.Instance,
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(settings ?? new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com/openai/v1",
                ApiKey = "key",
                Model = "my-deployment",
            }),
            recorder,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            callLogRecorder ?? new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService());

        return (client, handler, recorder);
    }
}

/// <summary>只把收到的項目留下來，不碰資料庫也不碰檔案系統。</summary>
internal sealed class RecordingTokenUsageRecorder : ITokenUsageRecorder
{
    public List<TokenUsageEntry> Entries { get; } = [];

    public Task RecordAsync(TokenUsageEntry entry)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}
