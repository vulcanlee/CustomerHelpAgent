using PdfSharp.Pdf.IO;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Token 用量 PDF 報表測試。
///
/// ⚠️ 必須標 <c>[Collection("PdfFont")]</c>，理由同 <see cref="AiReportPdfBuilderTests"/>：
/// <c>GlobalFontSettings.FontResolver</c> 是 process 全域 static 且 write-once，
/// 兩個 PDF 測試若落在不同的 test collection 會被 xUnit 平行執行而互撞。
///
/// 與 <see cref="AiReportPdfBuilderTests"/> 一樣刻意不解析 PDF 內容驗文字
/// （方案內沒有輕量的 PDF 文字抽取相依）；中文能不能印出來由
/// <c>AiReportPdfBuilderTests.Font_ShouldBeEmbeddedInWebAssembly</c> 這一支共同守住 ——
/// 兩個產生器用的是同一個 <see cref="EmbeddedFontResolver"/>。
/// </summary>
[Collection("PdfFont")]
public sealed class TokenUsageReportPdfBuilderTests
{
    [Fact]
    public void Build_ShouldProducePdfSignature()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest());

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    /// <summary>一筆資料都沒有時仍要印得出一份（只有標題與篩選條件）報表，不可丟例外。</summary>
    [Fact]
    public void Build_ShouldNotThrow_WhenThereIsNoData()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(new TokenUsageReportRequest
        {
            SystemName = "企業管理平台",
            SystemVersion = "0.9.14 (2026/09/16)",
            OperatorAccount = "support",
            GeneratedAt = new DateTime(2026, 9, 16, 15, 30, 0),
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>
    /// 明細有上限（200 筆），超過的部分不印。
    ///
    /// 上限存在的理由是 PDF 拿來看趨勢、逐筆對帳請用 CSV；這支測試守的是
    /// 「超量時不會爆掉，也不會真的把幾千列全排進去」。
    /// </summary>
    [Fact]
    public void Build_ShouldCapDetailRows()
    {
        var capped = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details = CreateDetails(200),
        });

        var overCap = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details = CreateDetails(600),
        });

        // 超過上限的部分沒有被排版，檔案大小不會再成長（差異只來自「已截斷」提示字串）。
        Assert.True(overCap.Length < capped.Length * 1.2);
    }

    /// <summary>依時長計費（沒有 token 數）的呼叫也要排得出來。</summary>
    [Fact]
    public void Build_ShouldNotThrow_ForDurationBilledRows()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details =
            [
                new TokenUsageLogAdapterModel
                {
                    Id = 1,
                    OccurredAt = new DateTime(2026, 9, 16, 12, 5, 0),
                    Operation = "語音轉檔",
                    CallKind = "Transcription",
                    Provider = "OpenAI",
                    Model = "whisper-2",
                    Account = "support",
                    DurationSeconds = 184,
                    ElapsedMilliseconds = 5200,
                    Success = true,
                },
            ],
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>失敗的呼叫帶著失敗原因，也要印得出來。</summary>
    [Fact]
    public void Build_ShouldNotThrow_ForFailedRows()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details =
            [
                new TokenUsageLogAdapterModel
                {
                    Id = 2,
                    OccurredAt = new DateTime(2026, 9, 16, 10, 15, 0),
                    Operation = "AI 日誌分析",
                    CallKind = "Chat",
                    Provider = "AzureOpenAI",
                    Model = "gpt-5.6-sol",
                    Account = null,
                    InputCount = 6400,
                    OutputCount = 0,
                    TotalCount = 6400,
                    ElapsedMilliseconds = 9100,
                    Success = false,
                    FailureReason = "EmptyResponse",
                },
            ],
        });

        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void Build_ShouldThrow_WhenRequestIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => TokenUsageReportPdfBuilder.Build(null!));
    }

    [Fact]
    public void Build_ShouldSucceed_WhenEveryDetailIsUnpriced()
    {
        // 0.9.17 之前的紀錄沒有單價快照，整份報表的費用欄都會是 null ——
        // 那是常態，不是例外，不能讓匯出炸掉。
        var request = CreateRequest();
        request = request with
        {
            Summary = new TokenUsageSummary { CallCount = 12, UnpricedCount = 12 },
            Details = CreateDetails(12, priced: false),
        };

        var bytes = TokenUsageReportPdfBuilder.Build(request);

        Assert.NotEmpty(bytes);
    }

    /// <summary>每日趨勢那一段真的有排進報表，而不是被悄悄忽略。</summary>
    [Fact]
    public void Build_ShouldRenderDailySection()
    {
        var withDaily = TokenUsageReportPdfBuilder.Build(CreateRequest());
        var withoutDaily = TokenUsageReportPdfBuilder.Build(CreateRequest() with { Daily = [] });

        Assert.True(withDaily.Length > withoutDaily.Length);
    }

    /// <summary>趨勢圖那一段真的有畫進報表：同樣只匯出趨勢圖，有資料的一定比沒資料的大。</summary>
    [Fact]
    public void Build_ShouldRenderTrendSection()
    {
        var withTrend = TokenUsageReportPdfBuilder.Build(CreateRequest() with { Scope = TokenUsageReportScope.Trend });
        var withoutTrend = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Scope = TokenUsageReportScope.Trend,
            Daily = [],
        });

        Assert.True(withTrend.Length > withoutTrend.Length);
    }

    /// <summary>
    /// 趨勢圖的邊界：沒資料、只有一個點、全部是 0（Y 軸分母為 0）、180 天（按週）。
    /// 都要印得出來，不可丟例外。
    /// </summary>
    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(1, 1.5)]
    [InlineData(10, 0)]
    [InlineData(180, 1.5)]
    public void Build_Trend_ShouldNotThrow_ForEdgeCases(int days, double costPerDay)
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Scope = TokenUsageReportScope.Trend,
            Daily = CreateDaily(days, costPerDay),
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>折線圖先畫成一份單頁 PDF 再嵌回報表；這一份本身必須是合法的單頁 PDF。</summary>
    [Fact]
    public void RenderTrendChart_ShouldProduceSinglePagePdf()
    {
        EmbeddedFontResolver.EnsureRegistered();
        var bytes = TokenUsageReportPdfBuilder.RenderTrendChart(TokenUsageTrendChart.Bucket(CreateDaily(30)));

        Assert.Equal(1, CountPages(bytes));
    }

    /// <summary>趨勢沒有資料時走「（無資料）」，不可丟例外。</summary>
    [Fact]
    public void Build_ShouldNotThrow_WhenDailyIsEmpty()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Scope = TokenUsageReportScope.Daily,
            Daily = [],
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>
    /// ⭐ 整段期間都沒花到錢（或全部未定價）時，長條的分母是 0。
    /// 這支守的就是「不可以除以零」—— 期望整欄留白而不是當掉。
    /// </summary>
    [Fact]
    public void Build_ShouldNotThrow_WhenEveryDailyCostIsZero()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Scope = TokenUsageReportScope.Daily,
            Daily = CreateDaily(10, costPerDay: 0),
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>每一種單頁籤都產得出報表，而且一定比整份小（只排了其中一個區塊）。</summary>
    [Theory]
    [InlineData(TokenUsageReportScope.Trend)]
    [InlineData(TokenUsageReportScope.Daily)]
    [InlineData(TokenUsageReportScope.Account)]
    [InlineData(TokenUsageReportScope.Operation)]
    [InlineData(TokenUsageReportScope.Model)]
    [InlineData(TokenUsageReportScope.CallKind)]
    [InlineData(TokenUsageReportScope.Detail)]
    public void Build_WithScope_ShouldBeSmallerThanAll(TokenUsageReportScope scope)
    {
        var all = TokenUsageReportPdfBuilder.Build(CreateRequest());
        var scoped = TokenUsageReportPdfBuilder.Build(CreateRequest() with { Scope = scope });

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(scoped, 0, 5));
        Assert.True(scoped.Length < all.Length);
    }

    /// <summary>
    /// ⭐ 單頁籤報表<b>仍然含條件區</b>。需求明說「PDF 內要看得到當時設定的條件」，
    /// 這一段不能因為只匯出一個頁籤就被切掉。
    ///
    /// 驗法沿用本檔既有的「位元組數比較」層級（方案內沒有輕量 PDF 文字抽取相依）：
    /// 條件區若被 Scope 切掉，有填篩選與沒填篩選的兩份就會一樣大。
    /// </summary>
    [Fact]
    public void Build_WithScope_ShouldStillIncludeFilters()
    {
        var scoped = CreateRequest() with { Scope = TokenUsageReportScope.Model };

        var withoutFilters = TokenUsageReportPdfBuilder.Build(scoped with
        {
            StartDate = null,
            EndDate = null,
            Account = string.Empty,
            Operation = string.Empty,
            CallKind = string.Empty,
            Model = string.Empty,
        });

        var withFilters = TokenUsageReportPdfBuilder.Build(scoped with
        {
            Account = "support",
            Operation = "AI 日誌分析",
            CallKind = "Chat",
            Model = "gpt-5.6-sol",
        });

        Assert.True(withFilters.Length > withoutFilters.Length);
    }

    /// <summary>每個 Scope 的標題後綴都不一樣，否則拿到報表看不出涵蓋哪一個頁籤。</summary>
    [Fact]
    public void DescribeScope_ShouldGiveEveryScopeItsOwnLabel()
    {
        var labels = Enum.GetValues<TokenUsageReportScope>()
            .Select(TokenUsageReportPdfBuilder.DescribeScope)
            .ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.DoesNotContain(labels, string.IsNullOrWhiteSpace);
    }

    /// <summary>
    /// ⭐ 長中文的自由文字必須換行、往下流，而不是排成一條衝出紙外的長線。
    ///
    /// 走的是真實路徑：<c>Account</c> 是操作者自己輸入的篩選值，會原樣印進條件區。
    /// 修正之前整串排成一行（頁數不變、超出紙張的字直接不見），修好之後會換行並撐出頁數差。
    /// </summary>
    [Fact]
    public void Build_ShouldWrapLongChineseFilterValue()
    {
        var shortValue = TokenUsageReportPdfBuilder.Build(CreateRequest() with { Account = "support" });
        var longValue = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Account = string.Concat(Enumerable.Repeat("這是一個很長的中文帳號查詢條件並且完全沒有空格", 150)),
        });

        Assert.True(
            CountPages(longValue) > CountPages(shortValue),
            "長中文篩選值沒有換行，整串仍排成一行並溢出頁面。");
    }

    /// <summary>用既有的 PDFsharp 相依數頁數，不必為了驗證再引入 PDF 解析套件。</summary>
    private static int CountPages(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static TokenUsageReportRequest CreateRequest() => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.14 (2026/09/16)",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 16, 15, 30, 0),
        StartDate = new DateTime(2026, 8, 1),
        EndDate = new DateTime(2026, 9, 16),
        Account = "support",
        Summary = new TokenUsageSummary
        {
            InputCount = 32832,
            OutputCount = 6197,
            CachedInputCount = 9648,
            ReasoningCount = 1609,
            TotalCount = 39029,
            CostUsd = 1.234567,
            CostTwd = 38.888,
            UnpricedCount = 1,
            CallCount = 7,
        },
        ByAccount = [Group("support", 5), Group("vulcan", 2)],
        ByOperation = [Group("AI 日誌分析", 4), Group("文件摘要", 2), Group("語音轉檔", 1)],
        ByModel = [Group("gpt-5.6-sol", 3), Group("gpt-5.4", 3), Group("whisper-2", 1)],
        ByCallKind = [Group("Chat", 6), Group("Transcription", 1)],
        Details = CreateDetails(12),
        Daily = CreateDaily(14),
    };

    /// <summary>
    /// 趨勢測試資料。刻意讓其中幾天是 0（<c>index % 3</c>），因為畫面補零之後
    /// 真的會有整天沒有呼叫的列，那些列的長條必須是空的而不是壞掉。
    /// </summary>
    private static List<TokenUsageDailyRow> CreateDaily(int count, double costPerDay = 1.5) =>
        [.. Enumerable.Range(0, count).Select(index => new TokenUsageDailyRow
        {
            Date = new DateTime(2026, 9, 1).AddDays(index),
            CallCount = index % 3,
            TotalCount = 1200 * (index % 3),
            CostUsd = costPerDay * (index % 3) / 31.5,
            CostTwd = costPerDay * (index % 3),
            UnpricedCount = index % 5 == 0 ? 1 : 0,
        })];

    private static TokenUsageGroupRow Group(string key, int callCount) => new()
    {
        Key = key,
        InputCount = 1000 * callCount,
        OutputCount = 200 * callCount,
        CachedInputCount = 300 * callCount,
        ReasoningCount = 50 * callCount,
        TotalCount = 1200 * callCount,
        CostUsd = 0.05 * callCount,
        CostTwd = 1.575 * callCount,
        CallCount = callCount,
    };

    private static List<TokenUsageLogAdapterModel> CreateDetails(int count, bool priced = true) =>
        [.. Enumerable.Range(1, count).Select(index => new TokenUsageLogAdapterModel
        {
            Id = index,
            OccurredAt = new DateTime(2026, 9, 16, 0, 0, 0).AddMinutes(index),
            Operation = "AI 日誌分析",
            CallKind = "Chat",
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            Account = "support",
            InputCount = 1000 + index,
            OutputCount = 200 + index,
            TotalCount = 1200 + (index * 2),
            CachedInputCount = 100,
            ReasoningCount = 20,
            ElapsedMilliseconds = 12000 + index,
            Success = true,
            CostUsd = priced ? 0.001 * index : null,
            CostTwd = priced ? 0.0315 * index : null,
            CostExchangeRate = priced ? 31.5 : null,
            CostPriceKey = priced ? "gpt-5.6-sol" : null,
        })];
}
