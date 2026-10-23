using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Token 用量的服務層。
///
/// 重點在四件事：
/// 1. 記錄時把原始 usage JSON 寫到檔案系統，資料表只留可聚合的數值。
/// 2. <b>合計的語意</b>：快取是輸入的折扣子集、推理計入輸出，兩者都不再加進合計。
/// 3. 刪除資料列一定要同時刪掉原始檔，不可留下孤兒檔。
/// 4. <c>RecordAsync</c> <b>絕不拋出</b> —— 記錄用量不可以讓 LLM 呼叫本身失敗。
/// </summary>
public sealed class TokenUsageLogServiceTests
{
    [Fact]
    public async Task RecordAsync_ShouldInsertRowAndWriteRawFile()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(TokenUsageOperations.AiLogAnalysis, row.Operation);
        Assert.Equal(TokenUsageCallKinds.Chat, row.CallKind);
        Assert.Equal("gpt-4o-mini", row.Model);
        Assert.Equal("support", row.Account);
        Assert.Equal(120, row.InputCount);
        Assert.Equal(45, row.OutputCount);
        Assert.Equal(165, row.TotalCount);
        Assert.True(row.Success);

        Assert.NotNull(row.RawUsageFile);
        Assert.True(File.Exists(fixture.FullPath(row.RawUsageFile!)));
        Assert.Contains("prompt_tokens", await File.ReadAllTextAsync(fixture.FullPath(row.RawUsageFile!)));
    }

    [Fact]
    public async Task RecordAsync_ShouldRecordFailuresToo()
    {
        // 「回應成功但內容為空」是付了錢卻沒拿到東西，最值得被看見。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(success: false, failureReason: "EmptyResponse"));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.False(row.Success);
        Assert.Equal("EmptyResponse", row.FailureReason);
        Assert.Equal(165, row.TotalCount);
    }

    [Fact]
    public async Task RecordAsync_ShouldPersistCallId()
    {
        // 0.9.72 起與 AI 對話紀錄以 CallId 關聯；用量明細的「查看對話」靠的就是這一欄。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();
        var callId = Guid.NewGuid();

        await service.RecordAsync(new TokenUsageEntry
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "AzureOpenAI",
            Model = "gpt-4o-mini",
            Success = true,
            CallId = callId,
        });

        Assert.Equal(callId, Assert.Single(await fixture.ListAsync()).CallId);
    }

    [Fact]
    public async Task RecordAsync_WithoutRawJson_ShouldStillInsertRow()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(rawUsageJson: null));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Null(row.RawUsageFile);
    }

    [Fact]
    public async Task RecordAsync_ShouldNeverThrow()
    {
        // 本服務位在 LLM 呼叫路徑上，任何失敗都不得往外拋。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeConnectionAsync();

        var exception = await Record.ExceptionAsync(() => service.RecordAsync(NewEntry()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldNotDoubleCountCachedOrReasoning()
    {
        // 快取屬於輸入、推理屬於輸出，兩者只回報「其中多少」，合計仍以 total_tokens 為準。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());
        await service.RecordAsync(NewEntry());

        var summary = await service.GetSummaryAsync(new TokenUsageQuery());

        Assert.Equal(240, summary.InputCount);
        Assert.Equal(90, summary.OutputCount);
        Assert.Equal(128, summary.CachedInputCount);
        Assert.Equal(64, summary.ReasoningCount);
        Assert.Equal(330, summary.TotalCount);
        Assert.Equal(2, summary.CallCount);
    }

    [Theory]
    [InlineData(TokenUsageGroupBy.Account, "support")]
    [InlineData(TokenUsageGroupBy.Operation, TokenUsageOperations.AiLogAnalysis)]
    [InlineData(TokenUsageGroupBy.Model, "gpt-4o-mini")]
    [InlineData(TokenUsageGroupBy.CallKind, TokenUsageCallKinds.Chat)]
    public async Task GetGroupedAsync_ShouldAggregateByDimension(TokenUsageGroupBy groupBy, string expectedKey)
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());
        await service.RecordAsync(NewEntry());

        var rows = await service.GetGroupedAsync(new TokenUsageQuery(), groupBy);

        var row = Assert.Single(rows);
        Assert.Equal(expectedKey, row.Key);
        Assert.Equal(330, row.TotalCount);
        Assert.Equal(2, row.CallCount);
    }

    [Fact]
    public async Task GetGroupedAsync_ByAccount_ShouldLabelSystemTriggeredCalls()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(account: null));

        var row = Assert.Single(await service.GetGroupedAsync(new TokenUsageQuery(), TokenUsageGroupBy.Account));
        Assert.Equal("（系統自動）", row.Key);
    }

    [Fact]
    public async Task GetAsync_ShouldFilterByDateRangeInclusiveOfEndDay()
    {
        // 結束日必須涵蓋「當天整天」，否則使用者選了今天卻查不到今天的資料。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        var today = DateTime.Today;
        await service.RecordAsync(NewEntry(occurredAt: today.AddHours(23).AddMinutes(59)));
        await service.RecordAsync(NewEntry(occurredAt: today.AddDays(-5)));

        var result = await service.GetAsync(new TokenUsageQuery { StartDate = today, EndDate = today, PageSize = 10 });

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task GetAsync_ShouldFilterByOperationModelAndAccount()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(account: "alice", model: "gpt-4o-mini"));
        await service.RecordAsync(NewEntry(account: "bob", model: "gpt-5"));

        Assert.Equal(1, (await service.GetAsync(new TokenUsageQuery { Account = "alice", PageSize = 10 })).Count);
        Assert.Equal(1, (await service.GetAsync(new TokenUsageQuery { Model = "gpt-5", PageSize = 10 })).Count);
        Assert.Equal(2, (await service.GetAsync(new TokenUsageQuery
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            PageSize = 10,
        })).Count);
    }

    [Fact]
    public async Task GetAsync_ShouldDefaultToNewestFirst()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "old", occurredAt: DateTime.Now.AddHours(-2)));
        await service.RecordAsync(NewEntry(model: "new", occurredAt: DateTime.Now));

        var result = await service.GetAsync(new TokenUsageQuery { PageSize = 10 });

        Assert.Equal("new", result.Result.First().Model);
    }

    [Fact]
    public async Task GetFilterOptionsAsync_ShouldReturnDistinctValues()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "gpt-4o-mini"));
        await service.RecordAsync(NewEntry(model: "gpt-5"));
        await service.RecordAsync(NewEntry(model: "gpt-5"));

        var options = await service.GetFilterOptionsAsync();

        Assert.Equal(["gpt-4o-mini", "gpt-5"], options.Models);
        Assert.Equal([TokenUsageOperations.AiLogAnalysis], options.Operations);
        Assert.Equal([TokenUsageCallKinds.Chat], options.CallKinds);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRowAndRawFile()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        var path = fixture.FullPath(row.RawUsageFile!);
        Assert.True(File.Exists(path));

        var result = await service.DeleteAsync(row.Id);

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ClearAllAsync_ShouldEmptyTableAndDirectory()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry());
        await service.RecordAsync(NewEntry());

        var result = await service.ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.TokenUsagePath));
    }

    [Fact]
    public async Task PurgeBeforeAsync_ShouldRemoveOnlyOlderRowsAndTheirFiles()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "fresh", occurredAt: DateTime.Today.AddHours(9)));
        await service.RecordAsync(NewEntry(model: "stale", occurredAt: DateTime.Today.AddDays(-10)));

        var staleRow = (await fixture.ListAsync()).Single(x => x.Model == "stale");
        var stalePath = fixture.FullPath(staleRow.RawUsageFile!);

        var result = await service.PurgeBeforeAsync(DateTime.Today);

        Assert.True(result.Success);
        var remaining = Assert.Single(await fixture.ListAsync());
        Assert.Equal("fresh", remaining.Model);
        Assert.False(File.Exists(stalePath));
    }

    [Fact]
    public async Task GetRawUsageAsync_MissingFile_ShouldReturnNullRatherThanThrow()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        File.Delete(fixture.FullPath(row.RawUsageFile!));

        Assert.Null(await service.GetRawUsageAsync(row.Id));
    }

    [Fact]
    public async Task RecordAsync_ShouldStoreCostAndRateSnapshot()
    {
        // 輸入 120 其中快取 64、輸出 45；費率設成 1 / 0.1 / 2 美金一顆，匯率 10。
        // 56×1 + 64×0.1 + 45×2 = 152.4 美金 → 1524 台幣。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "priced-model"));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(152.4, row.CostUsd!.Value, 6);
        Assert.Equal(1524, row.CostTwd!.Value, 6);
        Assert.Equal(10, row.CostExchangeRate);
        Assert.Equal("priced-model", row.CostPriceKey);
        Assert.False(row.CostLongContext);
        Assert.Contains("TextInput", row.CostRateSnapshot);
    }

    [Fact]
    public async Task RecordAsync_ShouldLeaveCostNull_WhenModelIsUnpriced()
    {
        // 未定價與「費用為 0」是兩件事：前者要在畫面上看得見，而不是被當成免費。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "model-without-price"));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Null(row.CostUsd);
        Assert.Null(row.CostTwd);
        Assert.Null(row.CostPriceKey);
    }

    [Fact]
    public async Task RecordAsync_ShouldStillInsertRow_WhenCostCalculatorThrows()
    {
        // 用量比費用重要：計算器壞掉只能丟掉金額，絕不能連帶讓整列用量消失。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService(new ThrowingCostCalculator());

        var exception = await Record.ExceptionAsync(() => service.RecordAsync(NewEntry(model: "priced-model")));

        Assert.Null(exception);
        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(165, row.TotalCount);
        Assert.Null(row.CostUsd);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldSumCostAndCountUnpriced()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(model: "priced-model"));
        await service.RecordAsync(NewEntry(model: "priced-model"));
        await service.RecordAsync(NewEntry(model: "model-without-price"));

        var summary = await service.GetSummaryAsync(new TokenUsageQuery());

        Assert.Equal(304.8, summary.CostUsd, 6);
        Assert.Equal(3048, summary.CostTwd, 6);
        Assert.Equal(1, summary.UnpricedCount);
        Assert.Equal(3, summary.CallCount);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnZeroCost_WhenNoRows()
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        var summary = await service.GetSummaryAsync(new TokenUsageQuery());

        Assert.Equal(0, summary.CostUsd);
        Assert.Equal(0, summary.CostTwd);
        Assert.Equal(0, summary.UnpricedCount);
    }

    [Fact]
    public async Task GetGroupedAsync_ShouldSumCostPerGroup()
    {
        // 分組的費用小計加起來要等於總計，否則「誰花最多錢」這個問題就答錯了。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(account: "alice", model: "priced-model"));
        await service.RecordAsync(NewEntry(account: "bob", model: "priced-model"));
        await service.RecordAsync(NewEntry(account: "bob", model: "model-without-price"));

        var rows = await service.GetGroupedAsync(new TokenUsageQuery(), TokenUsageGroupBy.Account);
        var summary = await service.GetSummaryAsync(new TokenUsageQuery());

        Assert.Equal(summary.CostUsd, rows.Sum(x => x.CostUsd), 6);
        Assert.Equal(1, rows.Single(x => x.Key == "bob").UnpricedCount);
        Assert.Equal(0, rows.Single(x => x.Key == "alice").UnpricedCount);
    }

    [Fact]
    public async Task GetDailyAsync_ShouldGroupByLocalDate()
    {
        // 跨午夜的兩筆必須落在不同天。這個測試同時證明 GroupBy(x => x.OccurredAt.Date)
        // 在 SQLite 上譯得出 SQL —— 譯不出來會在這裡就炸，而不是等到畫面才發現。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        var day = DateTime.Today.AddDays(-2);
        await service.RecordAsync(NewEntry(occurredAt: day.AddHours(23).AddMinutes(59)));
        await service.RecordAsync(NewEntry(occurredAt: day.AddDays(1).AddMinutes(1)));

        var rows = await service.GetDailyAsync(new TokenUsageQuery());

        Assert.Equal(2, rows.Count);
        Assert.Equal(day, rows[0].Date);
        Assert.Equal(day.AddDays(1), rows[1].Date);
        Assert.All(rows, row => Assert.Equal(1, row.CallCount));
    }

    [Fact]
    public async Task GetDailyAsync_ShouldSumCostAndCountUnpricedPerDay()
    {
        // 未定價要算進 UnpricedCount，不可以當成 0 併進金額 —— 併進去就變成「這天很便宜」。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        var day = DateTime.Today.AddDays(-1);
        await service.RecordAsync(NewEntry(model: "priced-model", occurredAt: day));
        await service.RecordAsync(NewEntry(model: "priced-model", occurredAt: day.AddHours(3)));
        await service.RecordAsync(NewEntry(model: "model-without-price", occurredAt: day.AddHours(6)));

        var row = Assert.Single(await service.GetDailyAsync(new TokenUsageQuery()));

        Assert.Equal(day, row.Date);
        Assert.Equal(304.8, row.CostUsd, 6);
        Assert.Equal(3048, row.CostTwd, 6);
        Assert.Equal(1, row.UnpricedCount);
        Assert.Equal(3, row.CallCount);
        Assert.Equal(495, row.TotalCount);
    }

    [Fact]
    public async Task GetDailyAsync_ShouldRespectFilters()
    {
        // 摘要卡會套用目前的模型／作業／帳號篩選，只把日期換成自己的窗。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        var day = DateTime.Today.AddDays(-1);
        await service.RecordAsync(NewEntry(model: "priced-model", occurredAt: day));
        await service.RecordAsync(NewEntry(model: "model-without-price", occurredAt: day));

        var row = Assert.Single(await service.GetDailyAsync(new TokenUsageQuery { Model = "priced-model" }));

        Assert.Equal(1, row.CallCount);
        Assert.Equal(0, row.UnpricedCount);
    }

    [Fact]
    public async Task GetDailyAsync_ShouldReturnAscendingByDate()
    {
        // 時間軸一律由舊到新，畫面直接照順序畫，不再自己排一次。
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        foreach (var offset in new[] { -1, -5, -3 })
        {
            await service.RecordAsync(NewEntry(occurredAt: DateTime.Today.AddDays(offset)));
        }

        var rows = await service.GetDailyAsync(new TokenUsageQuery());

        Assert.Equal(new[] { -5, -3, -1 }.Select(offset => DateTime.Today.AddDays(offset)), rows.Select(x => x.Date));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAsync_ShouldSortByCost(bool descending)
    {
        await using var fixture = await TokenUsageFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(account: "cheap", model: "model-without-price"));
        await service.RecordAsync(NewEntry(account: "paid", model: "priced-model"));

        var page = await service.GetAsync(new TokenUsageQuery
        {
            SortField = nameof(TokenUsageLogAdapterModel.CostTwd),
            SortDescending = descending,
        });

        // 未定價是 NULL，SQLite 升冪時排最前面、降冪時排最後面。
        var first = page.Result.First();
        Assert.Equal(descending ? "paid" : "cheap", first.Account);
    }

    /// <summary>驗證「計算器丟例外也不能吃掉整列用量」。</summary>
    private sealed class ThrowingCostCalculator : IAiUsageCostCalculator
    {
        public AiUsageCost? Calculate(TokenUsageEntry entry)
            => throw new InvalidOperationException("boom");
    }

    private static TokenUsageEntry NewEntry(
        string? account = "support",
        string model = "gpt-4o-mini",
        bool success = true,
        string? failureReason = null,
        string? rawUsageJson = """{"prompt_tokens":120,"completion_tokens":45,"total_tokens":165}""",
        DateTime? occurredAt = null)
        => new()
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "AzureOpenAI",
            Model = model,
            Account = account,
            UserId = account is null ? null : 1,
            InputCount = 120,
            OutputCount = 45,
            TotalCount = 165,
            CachedInputCount = 64,
            ReasoningCount = 32,
            ElapsedMilliseconds = 1234,
            Success = success,
            FailureReason = failureReason,
            RawUsageJson = rawUsageJson,
            OccurredAt = occurredAt ?? DateTime.Now,
        };

    private sealed class TokenUsageFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private TokenUsageFixture(SqliteConnection connection, string tokenUsagePath)
        {
            this.connection = connection;
            TokenUsagePath = tokenUsagePath;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public string TokenUsagePath { get; }

        public string FullPath(string relativePath)
            => Path.Combine(TokenUsagePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static async Task<TokenUsageFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var path = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            return new TokenUsageFixture(connection, path);
        }

        public TokenUsageLogService CreateService(IAiUsageCostCalculator? costCalculator = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.TokenUsagePath = TokenUsagePath;

            return new TokenUsageLogService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<TokenUsageLogService>(),
                new TokenUsageRawStore(Options.Create(settings), loggerFactory.CreateLogger<TokenUsageRawStore>()),
                costCalculator ?? CreateCostCalculator(),
                new StubAiQuotaService());
        }

        /// <summary>
        /// 測試用的計價設定：只有 priced-model 有費率，其他模型一律未定價。
        /// 匯率取 10 是為了讓「台幣 = 美金 × 10」一眼就看得出來。
        /// </summary>
        public static IAiUsageCostCalculator CreateCostCalculator()
        {
            var pricing = new AiPricingSettings
            {
                UsdToTwd = 10,
                Models =
                {
                    ["priced-model"] = new AiModelPricing
                    {
                        Rates = new AiModelRates
                        {
                            TextInputPerMillion = 1_000_000,
                            TextCachedInputPerMillion = 100_000,
                            TextOutputPerMillion = 2_000_000,
                        },
                    },
                },
            };

            return new AiUsageCostCalculator(new StaticOptionsMonitor<AiPricingSettings>(pricing));
        }

        public async Task<List<TokenUsageLog>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.TokenUsageLog.AsNoTracking().ToListAsync();
        }

        /// <summary>刻意關閉連線，用來驗證「RecordAsync 不得拋出」。</summary>
        public async Task DisposeConnectionAsync() => await connection.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(TokenUsagePath))
                {
                    Directory.Delete(TokenUsagePath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試殘留目錄不影響結果。
            }
        }
    }
}
