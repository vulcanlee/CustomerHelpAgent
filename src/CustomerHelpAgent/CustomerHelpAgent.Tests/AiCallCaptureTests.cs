using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 對話紀錄的擷取狀態（0.9.72 起）：結果預設值、送出旗標、端點不帶查詢字串，
/// 以及從原始請求解析訊息。
/// </summary>
public sealed class AiCallCaptureTests
{
    [Fact]
    public void ToEntry_ShouldDefaultToUnexpectedFailure_WhenOutcomeWasNeverSet()
    {
        var entry = Create().ToEntry(TimeSpan.FromMilliseconds(250));

        Assert.False(entry.Success);
        Assert.Equal(nameof(AiAnalysisFailureReason.Unexpected), entry.FailureReason);
        Assert.Equal(250, entry.ElapsedMilliseconds);
    }

    [Fact]
    public void ToEntry_ShouldPreferResponseModel_AndClearReasonOnSuccess()
    {
        var capture = Create();
        capture.ResponseModel = "gpt-a-2026-08-01";
        capture.SetOutcome(true, "ignored");

        var entry = capture.ToEntry(TimeSpan.Zero);

        Assert.True(entry.Success);
        Assert.Null(entry.FailureReason);
        Assert.Equal("gpt-a", entry.RequestedModel);
        Assert.Equal("gpt-a-2026-08-01", entry.Model);
    }

    [Fact]
    public void ToEntry_ShouldFallBackToRequestedModel_WhenResponseHasNone()
    {
        var entry = Create().ToEntry(TimeSpan.Zero);

        Assert.Equal("gpt-a", entry.Model);
    }

    [Fact]
    public async Task RecordSafelyAsync_ShouldSkip_WhenNotSent()
    {
        var recorder = new FakeAiCallLogRecorder();

        await AiCallCapture.RecordSafelyAsync(recorder, Create(), TimeSpan.Zero, NullLoggerFor.Instance);

        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task RecordSafelyAsync_ShouldRecord_OnceSent()
    {
        var recorder = new FakeAiCallLogRecorder();
        var capture = Create();
        capture.MarkSent();

        await AiCallCapture.RecordSafelyAsync(recorder, capture, TimeSpan.Zero, NullLoggerFor.Instance);

        Assert.Equal(capture.CallId, Assert.Single(recorder.Entries).CallId);
    }

    [Fact]
    public async Task RecordSafelyAsync_ShouldSwallowRecorderFailure()
    {
        var capture = Create();
        capture.MarkSent();

        // 在呼叫點的 finally 裡執行：丟出去就會蓋掉原本的回傳值。
        var exception = await Record.ExceptionAsync(() =>
            AiCallCapture.RecordSafelyAsync(new ThrowingAiCallLogRecorder(), capture, TimeSpan.Zero, NullLoggerFor.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_ShouldStripQueryStringFromEndpoint_AndNormalizeAnonymousUser()
    {
        var capture = new AiCallCapture(
            "作業", "OpenAI", "gpt-a", " ", 0,
            new Uri("https://example.invalid:8443/v1/chat/completions?api-key=secret"),
            "{}", null, null);

        Assert.Equal("https://example.invalid:8443/v1/chat/completions", capture.Endpoint);
        Assert.Null(capture.Account);
        Assert.Null(capture.UserId);
    }

    [Fact]
    public void Messages_ShouldParseRolesInOrder()
    {
        const string body = """
            {"model":"m","messages":[
              {"role":"system","content":"S"},
              {"role":"user","content":"U"},
              {"role":"assistant","content":"A"},
              {"role":"user","content":[{"type":"text","text":"parts"}]}
            ]}
            """;

        var messages = AiCallLogMessages.Parse(body);

        Assert.NotNull(messages);
        Assert.Equal(["system", "user", "assistant", "user"], messages!.Select(x => x.Role).ToList());
        Assert.Equal("A", messages[2].Content);
        Assert.Contains("parts", messages[3].Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"model\":\"m\"}")]
    public void Messages_ShouldReturnNull_WhenBodyIsNotAChatRequest(string? body)
        => Assert.Null(AiCallLogMessages.Parse(body));

    private static AiCallCapture Create()
        => new(
            TokenUsageOperations.AiLogAnalysis, "AzureOpenAI", "gpt-a", "support", 7,
            new Uri("https://example.invalid/openai/v1/chat/completions"),
            "{}", "日誌 …", null);

    private static class NullLoggerFor
    {
        public static readonly Microsoft.Extensions.Logging.ILogger Instance =
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
