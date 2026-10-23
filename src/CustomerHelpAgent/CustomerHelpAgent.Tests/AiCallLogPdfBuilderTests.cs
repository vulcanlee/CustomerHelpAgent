using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 對話紀錄 PDF（0.9.72 起）：中繼資料、角色標示與各種結果都排得出來。
/// 標 <c>[Collection("PdfFont")]</c> 的理由同 <see cref="AiReportPdfBuilderTests"/>（字型註冊是 process 全域）。
/// </summary>
[Collection("PdfFont")]
public sealed class AiCallLogPdfBuilderTests
{
    [Fact]
    public void Build_ShouldProducePdf_ForFullConversation()
    {
        var bytes = AiCallLogPdfBuilder.Build(CreateRequest(success: true, responseText: "## 結論\n\n一切正常。"));

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Build_ShouldProducePdf_WhenNothingWasReceived()
    {
        var bytes = AiCallLogPdfBuilder.Build(CreateRequest(success: false, responseText: string.Empty) with { Messages = [] });

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void BuildMetadataLines_ShouldIncludeOperationAccountUsageAndCallId()
    {
        var request = CreateRequest(success: true, responseText: "ok");

        var lines = AiCallLogPdfBuilder.BuildMetadataLines(request).ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, lines["作業"]);
        Assert.Equal("chen", lines["發起者"]);
        Assert.Equal("例外紀錄 #12（NullReferenceException）", lines["關聯說明"]);
        Assert.Contains("NT$ 1.50", lines["用量"], StringComparison.Ordinal);
        Assert.Equal(request.Item.CallId.ToString(), lines["呼叫識別碼"]);
    }

    [Fact]
    public void BuildMetadataLines_ShouldSayUnpriced_WhenUsageHasNoCost()
    {
        var request = CreateRequest(success: true, responseText: "ok") with
        {
            Usage = new TokenUsageLogAdapterModel { InputCount = 100, OutputCount = 50 },
        };

        var lines = AiCallLogPdfBuilder.BuildMetadataLines(request).ToDictionary(x => x.Key, x => x.Value);

        Assert.EndsWith("，未定價", lines["用量"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("system", "系統提示（System）")]
    [InlineData("user", "使用者（User）")]
    [InlineData("assistant", "AI 助理（Assistant）")]
    [InlineData("tool", "tool")]
    [InlineData("", "（未標示角色）")]
    public void RoleLabel_ShouldNameEachRole(string role, string expected)
        => Assert.Equal(expected, AiCallLogPdfBuilder.RoleLabel(role));

    [Fact]
    public void DescribeOutcome_ShouldDistinguishCanceledFromFailed()
    {
        Assert.Equal("成功", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { Success = true }));
        Assert.Equal("已取消", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { FailureReason = "Canceled" }));
        Assert.Equal("失敗（Timeout）", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { FailureReason = "Timeout" }));
    }

    private static AiCallLogPdfRequest CreateRequest(bool success, string responseText) => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.72",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 30, 10, 0, 0),
        Item = new AiCallLogAdapterModel
        {
            Id = 1,
            CallId = Guid.NewGuid(),
            OccurredAt = new DateTime(2026, 9, 30, 9, 30, 0),
            Operation = TokenUsageOperations.AiExceptionAnalysis,
            Provider = "AzureOpenAI",
            Model = "gpt-a",
            Account = "chen",
            Success = success,
            FailureReason = success ? null : "Timeout",
            ElapsedMilliseconds = 4321,
            RelatedInfo = "例外紀錄 #12（NullReferenceException）",
        },
        Messages =
        [
            new AiCallLogMessage("system", "你是資深的 .NET 維運工程師。"),
            new AiCallLogMessage("user", "例外類型：NullReferenceException\n  at Foo.Bar()"),
        ],
        ResponseText = responseText,
        Usage = new TokenUsageLogAdapterModel { InputCount = 100, OutputCount = 50, CostTwd = 1.5, CostUsd = 0.05 },
    };
}
