using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 例外分析服務。兩條紅線：追問超過上限、設定不完整時都<b>不可</b>發出 HTTP（會白花錢）。
/// </summary>
public sealed class AiExceptionAnalysisServiceTests
{
    private const string SuccessPayload = """
        {
          "model": "gpt-4o-2024-11-20",
          "choices": [ { "finish_reason": "stop", "message": { "content": "## 管理者摘要\n影響輕微。" } } ],
          "usage": { "prompt_tokens": 120, "completion_tokens": 45, "total_tokens": 165 }
        }
        """;

    [Fact]
    public async Task AskAsync_ShouldPrependSystemPrompt_AndKeepHistoryOrder()
    {
        var (service, handler, _) = CreateService(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        var result = await service.AskAsync(
        [
            new AiChatMessage(AiChatRoles.User, "detail"),
            new AiChatMessage(AiChatRoles.Assistant, "report"),
            new AiChatMessage(AiChatRoles.User, "follow-up"),
        ]);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(4, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(
            AiPromptGuardrails.Compose(PromptTemplateKeys.ExceptionAnalysis, AiExceptionPromptDefaults.Instructions),
            messages[0].GetProperty("content").GetString());
        Assert.Equal(
            ["detail", "report", "follow-up"],
            messages.Skip(1).Select(message => message.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task AskAsync_ShouldRecordUsage_AsAiExceptionAnalysis()
    {
        var (service, _, recorder) = CreateService(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        await service.AskAsync([new AiChatMessage(AiChatRoles.User, "detail")]);

        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, Assert.Single(recorder.Entries).Operation);
    }

    [Fact]
    public async Task AskAsync_ShouldThreadAnalysisAndFollowUps_InOneConversation()
    {
        var callLog = new FakeAiCallLogRecorder();
        var (service, _, _) = CreateService(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), callLogRecorder: callLog);
        var context = new AiExceptionCallContext(42, "NullReferenceException", Guid.NewGuid());

        await service.AskAsync([new AiChatMessage(AiChatRoles.User, "detail")], context);
        await service.AskAsync(
        [
            new AiChatMessage(AiChatRoles.User, "detail"),
            new AiChatMessage(AiChatRoles.Assistant, "report"),
            new AiChatMessage(AiChatRoles.User, "follow-up"),
        ],
            context);

        Assert.Equal(2, callLog.Entries.Count);
        Assert.All(callLog.Entries, entry =>
        {
            Assert.Equal(context.ConversationId, entry.ConversationId);
            Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, entry.Operation);
        });
        Assert.Equal("例外紀錄 #42（NullReferenceException）", callLog.Entries[0].RelatedInfo);
        Assert.Equal("例外紀錄 #42，追問第 1 輪", callLog.Entries[1].RelatedInfo);
        Assert.NotEqual(callLog.Entries[0].CallId, callLog.Entries[1].CallId);
    }

    [Fact]
    public async Task AskAsync_ShouldLeaveCallLogUnthreaded_WithoutContext()
    {
        var callLog = new FakeAiCallLogRecorder();
        var (service, _, _) = CreateService(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), callLogRecorder: callLog);

        await service.AskAsync([new AiChatMessage(AiChatRoles.User, "detail")]);

        var entry = Assert.Single(callLog.Entries);
        Assert.Null(entry.ConversationId);
        Assert.Null(entry.RelatedInfo);
    }

    [Fact]
    public async Task AskAsync_ShouldRefuseWithoutSending_WhenFollowUpLimitExceeded()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (service, _, recorder) = CreateService(handler, maxFollowUpRounds: 1);

        // 初始 1 則 + 追問 2 則 = 追問 2 輪，超過上限 1。
        var result = await service.AskAsync(
        [
            new AiChatMessage(AiChatRoles.User, "detail"),
            new AiChatMessage(AiChatRoles.Assistant, "report"),
            new AiChatMessage(AiChatRoles.User, "q1"),
            new AiChatMessage(AiChatRoles.Assistant, "a1"),
            new AiChatMessage(AiChatRoles.User, "q2"),
        ]);

        Assert.Equal(AiAnalysisFailureReason.FollowUpLimitReached, result.Reason);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task AskAsync_ShouldSend_WhenFollowUpWithinLimit()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (service, _, _) = CreateService(handler, maxFollowUpRounds: 1);

        var result = await service.AskAsync(
        [
            new AiChatMessage(AiChatRoles.User, "detail"),
            new AiChatMessage(AiChatRoles.Assistant, "report"),
            new AiChatMessage(AiChatRoles.User, "q1"),
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_ShouldNotSend_WhenSettingsIncomplete()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (service, _, _) = CreateService(handler, settings: new AiSettings());

        var result = await service.AskAsync([new AiChatMessage(AiChatRoles.User, "detail")]);

        Assert.Equal(AiAnalysisFailureReason.NotConfigured, result.Reason);
        Assert.Equal(0, handler.CallCount);
        Assert.False(service.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(service.UnavailableReason));
    }

    [Fact]
    public async Task AskAsync_ShouldReturnNoData_WhenConversationEmpty()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (service, _, _) = CreateService(handler);

        var result = await service.AskAsync([]);

        Assert.Equal(AiAnalysisFailureReason.NoData, result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(5, 5)]
    public void MaxFollowUpRounds_ShouldClampNegativeToZero(int configured, int expected)
    {
        var (service, _, _) = CreateService(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), maxFollowUpRounds: configured);

        Assert.Equal(expected, service.MaxFollowUpRounds);
    }

    private static (AiExceptionAnalysisService Service, StubHttpMessageHandler Handler, RecordingTokenUsageRecorder Recorder)
        CreateService(
            StubHttpMessageHandler handler,
            int maxFollowUpRounds = 10,
            AiSettings? settings = null,
            IAiCallLogRecorder? callLogRecorder = null)
    {
        settings ??= new AiSettings
        {
            Provider = nameof(AiProvider.AzureOpenAI),
            Endpoint = "https://unit-test.openai.azure.com/openai/v1",
            ApiKey = "key",
            Model = "my-deployment",
        };
        settings.MaxFollowUpRounds = maxFollowUpRounds;

        var recorder = new RecordingTokenUsageRecorder();
        var optionsMonitor = new StaticOptionsMonitor<AiSettings>(settings);
        var service = new AiExceptionAnalysisService(
            optionsMonitor,
            new AiChatCompletionClient(
                NullLogger<AiChatCompletionClient>.Instance,
                new StubHttpClientFactory(handler),
                optionsMonitor,
                recorder,
                new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin" } },
                callLogRecorder ?? new FakeAiCallLogRecorder(),
                new StubAiQuotaService(),
                new RecordingAuditLogService()),
            new StubSystemPromptProvider());

        return (service, handler, recorder);
    }
}
