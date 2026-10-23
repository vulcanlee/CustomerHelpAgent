using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 例外分析的 PDF 報告。與 <see cref="AiReportPdfBuilderTests"/> 共用字型註冊，
/// 所以同樣要標 <c>[Collection("PdfFont")]</c>。
/// </summary>
[Collection("PdfFont")]
public sealed class AiExceptionReportPdfBuilderTests
{
    [Fact]
    public void Build_ShouldProducePdfSignature_WithFollowUps()
    {
        var bytes = AiExceptionReportPdfBuilder.Build(CreateRequest());

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Build_ShouldNotThrow_WhenStackTraceMissingAndNoFollowUps()
    {
        var request = CreateRequest() with
        {
            StackTrace = null,
            Exchanges = [new AiChatMessage(AiChatRoles.Assistant, "## 管理者摘要\n影響輕微。")],
        };

        var bytes = AiExceptionReportPdfBuilder.Build(request);

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void BuildMetadataLines_ShouldIncludeModelRoundsAndUsage()
    {
        var lines = AiExceptionReportPdfBuilder.BuildMetadataLines(CreateRequest())
            .ToDictionary(line => line.Key, line => line.Value);

        Assert.Equal("CustomerHelpAgent　版本 0.9.68 (2026/09/27)", lines["系統"]);
        Assert.Equal("admin", lines["操作者"]);
        Assert.Equal("gpt-4o", lines["使用模型"]);
        Assert.Equal("1", lines["追問輪數"]);
        Assert.Contains("輸入 300", lines["累計用量"]);
    }

    [Fact]
    public void TokenUsageSum_ShouldAddOnlyReportedFields()
    {
        var total = AiTokenUsage.Sum(
        [
            new AiTokenUsage { InputCount = 100, OutputCount = 10 },
            null,
            new AiTokenUsage { InputCount = 200, CachedInputCount = 50 },
        ]);

        Assert.NotNull(total);
        Assert.Equal(300, total.InputCount);
        Assert.Equal(10, total.OutputCount);
        Assert.Equal(50, total.CachedInputCount);
        Assert.Null(total.TotalCount);
        Assert.Null(AiTokenUsage.Sum([null, null]));
    }

    private static AiExceptionReportPdfRequest CreateRequest() => new()
    {
        SystemName = "CustomerHelpAgent",
        SystemVersion = "0.9.68 (2026/09/27)",
        OperatorAccount = "admin",
        GeneratedAt = new DateTime(2026, 9, 27, 10, 0, 0),
        DetailLines =
        [
            new("例外類型", "System.InvalidOperationException"),
            new("訊息", "Sequence contains no elements"),
        ],
        StackTrace = "   at Foo.Bar() in Foo.cs:line 42\n   at Foo.Baz()",
        Exchanges =
        [
            new AiChatMessage(AiChatRoles.Assistant, "## 管理者摘要\n影響輕微。\n\n## 根本原因\n`First()` 遇到空集合。"),
            new AiChatMessage(AiChatRoles.User, "第 2 點能再詳細一點嗎？"),
            new AiChatMessage(AiChatRoles.Assistant, "可以，改用 `FirstOrDefault()` 並處理 null。"),
        ],
        ModelName = "gpt-4o",
        Usage = new AiTokenUsage { InputCount = 300, OutputCount = 90 },
    };
}
