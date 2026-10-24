using System.Text.Json;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 效益對照的指標計算（<see cref="BenefitMetricsService"/>，M6）：每次劇本執行的實測值、依家族 × 流程的平均、
/// 回放排除、示範重置後數字不變，以及 AI 成本與「Token 用量」頁對得起來。資料直接寫進保留的紀錄表（埋點、軌跡、用量、對話紀錄）。
/// </summary>
public sealed class BenefitMetricsServiceTests : IAsyncLifetime
{
    private static readonly DateTime Day = new(2026, 10, 24);

    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private BenefitMetricsService Service() => new(factory, NullLogger<BenefitMetricsService>.Instance);

    private Task<BenefitComparison> CompareDayAsync() => Service().GetComparisonAsync(Day, Day.AddDays(1));

    private static DateTime At(int hour, int minute, int second = 0) => Day.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private async Task<int> AddRunAsync(string code, string family, ScenarioFlow flow, DateTime startedAt, bool isReplay = false)
    {
        await using var db = factory.CreateDbContext();
        var run = new DemoScenarioRun
        {
            ScenarioCode = code,
            Family = family,
            Flow = flow,
            Account = DemoSeedData.Customer1,
            IsReplay = isReplay,
            StartedAt = startedAt,
        };
        db.DemoScenarioRun.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static ProcessMetric Metric(int? runId, Guid caseKey, ProcessActor actor, string action, DateTime at,
        AfterSalesChannel channel = AfterSalesChannel.Agent, int? requestId = null)
        => new()
        {
            CaseKey = caseKey,
            Channel = channel,
            Actor = actor,
            Action = action,
            UserId = 1,
            AfterSalesRequestId = requestId,
            OccurredAt = at,
            DemoRunId = runId,
        };

    private static ProcessMetric Customer(int? runId, Guid caseKey, string action, DateTime at, AfterSalesChannel channel = AfterSalesChannel.Agent)
        => Metric(runId, caseKey, ProcessActor.Customer, action, at, channel);

    private static ProcessMetric Staff(int? runId, Guid caseKey, string action, DateTime at, int requestId, AfterSalesChannel channel = AfterSalesChannel.Agent)
        => Metric(runId, caseKey, ProcessActor.Staff, action, at, channel, requestId);

    private static AgentRunTrace Trace(Guid sessionKey, DateTime at, AgentStopReason? reason, int? replayRecordingId = null)
        => new()
        {
            SessionKey = sessionKey,
            CustomerUserId = 1,
            StartedAt = at,
            EndedAt = at.AddSeconds(5),
            StopReason = reason,
            ModelCallCount = 9,
            ReplayRecordingId = replayRecordingId,
        };

    private async Task AddAsync(params object[] entities)
    {
        await using var db = factory.CreateDbContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>一次 AI 呼叫：用量一列，<paramref name="withCallLog"/> 時加一列對話紀錄（以 CallId 關聯、帶會話識別碼）。</summary>
    private async Task AddCallAsync(Guid? conversationId, string operation, DateTime at, double? costTwd, bool withCallLog = true)
    {
        var callId = Guid.NewGuid();
        var entities = new List<object>
        {
            new TokenUsageLog
            {
                OccurredAt = at,
                Operation = operation,
                CallKind = TokenUsageCallKinds.Chat,
                Provider = "AzureOpenAI",
                Model = "gpt-test",
                InputCount = 100,
                OutputCount = 10,
                TotalCount = 110,
                Success = true,
                CostUsd = costTwd / 30,
                CostTwd = costTwd,
                CallId = callId,
            },
        };
        if (withCallLog)
        {
            entities.Add(new AiCallLog
            {
                CallId = callId,
                OccurredAt = at,
                Operation = operation,
                Provider = "AzureOpenAI",
                Model = "gpt-test",
                Success = true,
                ConversationId = conversationId,
            });
        }

        await AddAsync([.. entities]);
    }

    [Fact]
    public async Task CustomerSteps_CollapseConsecutiveFillDetail()
    {
        var run = await AddRunAsync("A-T", "換尺碼", ScenarioFlow.Traditional, At(10, 0));
        var form1 = Guid.NewGuid();
        var form2 = Guid.NewGuid();
        const AfterSalesChannel T = AfterSalesChannel.Traditional;
        await AddAsync(
            Customer(run, form1, ProcessMetricActions.OpenForm, At(10, 1), T),
            Customer(run, form1, ProcessMetricActions.SelectOrderItem, At(10, 2), T),
            Customer(run, form1, ProcessMetricActions.SelectType, At(10, 3), T),
            // 每改一個欄位記一次：連續三筆只算 1 步。
            Customer(run, form1, ProcessMetricActions.FillDetail, At(10, 4), T),
            Customer(run, form1, ProcessMetricActions.FillDetail, At(10, 5), T),
            Customer(run, form1, ProcessMetricActions.FillDetail, At(10, 6), T),
            Customer(run, form1, ProcessMetricActions.UploadPhoto, At(10, 7), T),
            // 中間隔了上傳照片：再算 1 步。
            Customer(run, form1, ProcessMetricActions.FillDetail, At(10, 8), T),
            // 另一張表的填寫細節不與上一張合併（各 1 步，同一張的連續兩筆合併）。
            Customer(run, form2, ProcessMetricActions.FillDetail, At(10, 8, 20), T),
            Customer(run, form2, ProcessMetricActions.FillDetail, At(10, 8, 40), T),
            Customer(run, form1, ProcessMetricActions.Submit, At(10, 9), T),
            Staff(run, form1, ProcessMetricActions.OpenRequest, At(10, 20), 1, T),
            Staff(run, form1, ProcessMetricActions.ViewOrder, At(10, 21), 1, T),
            Staff(run, form1, ProcessMetricActions.Approve, At(10, 25), 1, T));

        var result = await CompareDayAsync();

        var metrics = Assert.Single(result.Runs);
        // 開啟表單不算步數（裁定 R11，與 AI 打開線上客服不記錄對等）。
        Assert.Equal(7, metrics.CustomerSteps);
        Assert.Equal(3, metrics.StaffSteps);
        Assert.Equal(1, metrics.RequestCount);
        Assert.Equal(2, metrics.CaseCount);
        var flow = Assert.Single(Assert.Single(result.Families).Flows);
        Assert.Equal(ScenarioFlow.Traditional, flow.Flow);
        Assert.Equal(7, flow.CustomerSteps);
    }

    [Fact]
    public async Task Replay_RunsAndCases_Excluded()
    {
        var live = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        var replayMode = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(11, 0), isReplay: true);
        var replayTrace = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(12, 0));
        Guid k1 = Guid.NewGuid(), k2 = Guid.NewGuid(), k3 = Guid.NewGuid(), other = Guid.NewGuid(), otherReplay = Guid.NewGuid();
        await AddAsync(
            Customer(live, k1, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(live, k1, ProcessMetricActions.AgentRequestCreated, At(10, 3)),
            Trace(k1, At(10, 1), AgentStopReason.Answered),
            // 回放模式建立的執行：模型沒被呼叫、成本 0。
            Customer(replayMode, k2, ProcessMetricActions.AgentSendMessage, At(11, 1)),
            Customer(replayMode, k2, ProcessMetricActions.AgentSendMessage, At(11, 2)),
            Trace(k2, At(11, 1), AgentStopReason.Answered),
            // 執行建立時是即時，但軌跡是回放（之後切到回放模式）：同樣整筆排除。
            Customer(replayTrace, k3, ProcessMetricActions.AgentSendMessage, At(12, 1)),
            Customer(replayTrace, k3, ProcessMetricActions.AgentSendMessage, At(12, 2)),
            Trace(k3, At(12, 1), AgentStopReason.Answered, replayRecordingId: 7),
            // 示範模式外的案件：一筆即時、一筆回放。
            Customer(null, other, ProcessMetricActions.AgentSendMessage, At(13, 0)),
            Customer(null, otherReplay, ProcessMetricActions.AgentSendMessage, At(13, 30)),
            Trace(otherReplay, At(13, 30), AgentStopReason.Answered, replayRecordingId: 8));
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(10, 2), 2.0);
        await AddCallAsync(other, TokenUsageOperations.AfterSalesAgent, At(13, 1), 1.0);

        var result = await CompareDayAsync();

        Assert.Equal([false, true, true], result.Runs.Select(x => x.IsReplay));
        var flow = Assert.Single(Assert.Single(result.Families).Flows);
        Assert.Equal(1, flow.RunCount);
        Assert.Equal(2, flow.ReplayExcludedCount);
        Assert.Equal(2.0, flow.AiCostTwd);
        Assert.Equal(1, flow.CustomerSteps);
        Assert.Equal(1, flow.ModelCallCount);

        var channel = Assert.Single(result.Others);
        Assert.Equal(AfterSalesChannel.Agent, channel.Channel);
        Assert.Equal(1, channel.RunCount);
        Assert.Equal(1, channel.ReplayExcludedCount);
        Assert.Equal(1.0, channel.AiCostTwd);
    }

    [Fact]
    public async Task RunWindow_RecordsAfterNextRunIgnored()
    {
        // 裁定 R1：執行只算 [開始, 下一次執行開始) 之內的紀錄。下一次執行開始時目前的執行編號已換掉，
        // 實務上不會再有蓋舊編號的埋點，這裡的埋點裁切是防禦；真正會越界的是 AI 呼叫（例如舊案件的初審晚跑完），
        // 它們依時間歸屬（AiCost_MatchesTokenUsageSummary 也涵蓋）。
        var first = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        await AddRunAsync("A-T", "換尺碼", ScenarioFlow.Traditional, At(11, 0));
        var key = Guid.NewGuid();
        await AddAsync(
            Customer(first, key, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(first, key, ProcessMetricActions.AgentSendMessage, At(11, 5)),
            Trace(key, At(10, 1), AgentStopReason.AwaitingUserReply),
            Trace(key, At(11, 5), AgentStopReason.AwaitingUserReply));
        await AddCallAsync(key, TokenUsageOperations.AfterSalesAgent, At(10, 2), 1.0);
        await AddCallAsync(key, TokenUsageOperations.AfterSalesAgent, At(11, 6), 4.0);

        var result = await CompareDayAsync();

        var run = result.Runs.Single(x => x.RunId == first);
        Assert.Equal(At(10, 0), run.StartedAt);
        Assert.Equal(At(11, 0), run.EndBoundary);
        Assert.Equal(At(10, 2), run.LastActivityAt);
        Assert.Equal(1, run.CustomerSteps);
        Assert.Equal(1, run.StopToAskCount);
        Assert.Equal(1.0, run.AiCostTwd);
        Assert.Equal(1, run.ModelCallCount);
        // 最後一次執行的區間終點是查詢範圍終點；區間外的成本歸「對應不到」。
        Assert.Equal(Day.AddDays(1), result.Runs.Last().EndBoundary);
        Assert.Equal(4.0, result.Cost.UnattributedTwd);
    }

    [Fact]
    public async Task EndToEnd_UndecidedRunExcludedFromAverage()
    {
        var decided = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        var undecided = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(11, 0));
        Guid k1 = Guid.NewGuid(), k2 = Guid.NewGuid();
        await AddAsync(
            Customer(decided, k1, ProcessMetricActions.AgentSendMessage, At(10, 0, 30)),
            Customer(decided, k1, ProcessMetricActions.AgentConfirm, At(10, 5)),
            Customer(decided, k1, ProcessMetricActions.AgentRequestCreated, At(10, 5, 10)),
            Staff(decided, k1, ProcessMetricActions.OpenRequest, At(10, 20, 30), 1),
            Staff(decided, k1, ProcessMetricActions.Approve, At(10, 30, 30), 1),
            // 決策之後再按一次（例如另一個決策）：只取第一個。
            Staff(decided, k1, ProcessMetricActions.Reject, At(10, 40), 1),
            Customer(undecided, k2, ProcessMetricActions.AgentSendMessage, At(11, 1)),
            Customer(undecided, k2, ProcessMetricActions.AgentConfirm, At(11, 2)),
            Customer(undecided, k2, ProcessMetricActions.AgentRequestCreated, At(11, 2, 10)),
            Staff(undecided, k2, ProcessMetricActions.OpenRequest, At(11, 10), 2));

        var result = await CompareDayAsync();

        Assert.Equal(30, result.Runs[0].EndToEndMinutes);
        Assert.Equal(10, result.Runs[0].StaffHandlingMinutes);
        Assert.Null(result.Runs[1].EndToEndMinutes);
        Assert.Null(result.Runs[1].StaffHandlingMinutes);
        var flow = Assert.Single(Assert.Single(result.Families).Flows);
        Assert.Equal(2, flow.RunCount);
        Assert.Equal(30, flow.EndToEndMinutes);
        Assert.Equal(1, flow.EndToEndSampleCount);
        Assert.Equal(10, flow.StaffHandlingMinutes);
        Assert.Equal(1, flow.StaffHandlingSampleCount);
        Assert.Equal(1, flow.RequestCount);
        Assert.Equal(0, flow.ExcludedRunCount);
        Assert.Equal(1, flow.CompletedRunCount);
        Assert.True(result.Runs[0].IsCompleted);
        Assert.False(result.Runs[1].IsCompleted);
    }

    [Fact]
    public async Task EndToEnd_TraditionalStartsAfterAutoRecordedOpenForm()
    {
        // 裁定 R10：開啟表單是頁面載入時自動記錄的（展示者此時可能還在講解），端到端從第一個不是開啟表單的顧客操作算起，
        // 與 AI 從第一句話算起對等；開啟表單也不算步數（裁定 R11）。
        var run = await AddRunAsync("A-T", "換尺碼", ScenarioFlow.Traditional, At(10, 0));
        var form = Guid.NewGuid();
        const AfterSalesChannel T = AfterSalesChannel.Traditional;
        await AddAsync(
            Customer(run, form, ProcessMetricActions.OpenForm, At(10, 0), T),
            Customer(run, form, ProcessMetricActions.SelectOrderItem, At(10, 10), T),
            Customer(run, form, ProcessMetricActions.SelectType, At(10, 11), T),
            Customer(run, form, ProcessMetricActions.Submit, At(10, 12), T),
            Staff(run, form, ProcessMetricActions.OpenRequest, At(10, 15), 1, T),
            Staff(run, form, ProcessMetricActions.Approve, At(10, 20), 1, T));

        var metrics = Assert.Single((await CompareDayAsync()).Runs);

        Assert.Equal(10, metrics.EndToEndMinutes);
        Assert.Equal(3, metrics.CustomerSteps);
        Assert.Equal(5, metrics.StaffHandlingMinutes);
        Assert.True(metrics.IsCompleted);
    }

    [Fact]
    public async Task StaffHandling_SumsEachRequest()
    {
        // 一句兩事：兩筆申請各自「開啟 → 決策」，客服處理分鐘是兩段相加；端到端只到第一個決策。
        var run = await AddRunAsync("B", "一句兩事", ScenarioFlow.SingleAgent, At(10, 0));
        var key = Guid.NewGuid();
        await AddAsync(
            Customer(run, key, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Staff(run, key, ProcessMetricActions.OpenRequest, At(10, 10), 1),
            Staff(run, key, ProcessMetricActions.Approve, At(10, 13), 1),
            Staff(run, key, ProcessMetricActions.OpenRequest, At(10, 14), 2),
            Staff(run, key, ProcessMetricActions.RequestMoreInfo, At(10, 19), 2));

        var metrics = Assert.Single((await CompareDayAsync()).Runs);

        Assert.Equal(8, metrics.StaffHandlingMinutes);
        Assert.Equal(12, metrics.EndToEndMinutes);
        Assert.Equal(4, metrics.StaffSteps);
    }

    [Fact]
    public async Task StopToAsk_CountsAwaitingUserReply()
    {
        var run = await AddRunAsync("B", "一句兩事", ScenarioFlow.SingleAgent, At(10, 0));
        var key = Guid.NewGuid();
        await AddAsync(
            Customer(run, key, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(run, key, ProcessMetricActions.AgentRequestCreated, At(10, 8)),
            Trace(key, At(10, 1), AgentStopReason.AwaitingUserReply),
            Trace(key, At(10, 2), AgentStopReason.AwaitingUserReply),
            Trace(key, At(10, 3), AgentStopReason.AwaitingConfirmation),
            Trace(key, At(10, 4), AgentStopReason.AwaitingPhotoUpload),
            Trace(key, At(10, 5), AgentStopReason.Answered),
            Trace(key, At(10, 6), null),
            // 別的會話不算。
            Trace(Guid.NewGuid(), At(10, 7), AgentStopReason.AwaitingUserReply));

        var result = await CompareDayAsync();

        var metrics = Assert.Single(result.Runs);
        Assert.Equal(2, metrics.StopToAskCount);
        Assert.Equal(1, metrics.AwaitingPhotoCount);
        var flow = Assert.Single(Assert.Single(result.Families).Flows);
        Assert.Equal(2, flow.StopToAskCount);
        Assert.Equal(1, flow.AwaitingPhotoCount);
    }

    [Fact]
    public async Task Others_GroupedByChannel_BrowseOnlyPagesIgnored()
    {
        Guid form = Guid.NewGuid(), browse = Guid.NewGuid(), handoff = Guid.NewGuid(), openedOnly = Guid.NewGuid();
        const AfterSalesChannel T = AfterSalesChannel.Traditional;
        await AddAsync(
            Customer(null, browse, ProcessMetricActions.ViewOrders, At(9, 0), T),
            Customer(null, form, ProcessMetricActions.OpenForm, At(9, 1), T),
            Customer(null, form, ProcessMetricActions.FillDetail, At(9, 2), T),
            Customer(null, form, ProcessMetricActions.FillDetail, At(9, 3), T),
            Customer(null, form, ProcessMetricActions.Submit, At(9, 4), T),
            Staff(null, form, ProcessMetricActions.OpenRequest, At(9, 10), 1, T),
            Staff(null, form, ProcessMetricActions.Reject, At(9, 12), 1, T),
            Customer(null, handoff, ProcessMetricActions.AgentSendMessage, At(9, 30), AfterSalesChannel.AgentHandoff),
            Customer(null, openedOnly, ProcessMetricActions.OpenForm, At(9, 40), T),
            // 範圍外不算。
            Customer(null, Guid.NewGuid(), ProcessMetricActions.OpenForm, Day.AddMinutes(-1), T));

        var result = await CompareDayAsync();

        Assert.Empty(result.Runs);
        Assert.Empty(result.Families);
        Assert.Equal([AfterSalesChannel.Traditional, AfterSalesChannel.AgentHandoff], result.Others.Select(x => x.Channel));
        var traditional = result.Others[0];
        Assert.Equal(1, traditional.RunCount);
        Assert.Equal(2, traditional.CustomerSteps);
        // 端到端從第一個不是開啟表單的操作（9:02 填寫）算起（裁定 R10）。
        Assert.Equal(10, traditional.EndToEndMinutes);
        Assert.Equal(2, traditional.StaffHandlingMinutes);
        Assert.Equal(1, traditional.RequestCount);
        // 只打開表單就離開：打開頁面不算顧客操作（裁定 R11），不列入平均、計入排除數。
        Assert.Equal(1, traditional.ExcludedRunCount);
    }

    [Fact]
    public async Task Others_NoCustomerCaseExcluded_RunCaseKeysNotDoubleCounted()
    {
        var run = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        Guid inRun = Guid.NewGuid(), staffOnly = Guid.NewGuid(), chat = Guid.NewGuid();
        await AddAsync(
            Customer(run, inRun, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(run, inRun, ProcessMetricActions.AgentRequestCreated, At(10, 2)),
            Trace(inRun, At(10, 1), AgentStopReason.AwaitingUserReply),
            // 同一個 CaseKey 之後沒蓋執行編號的埋點（例如重置後才審核）：不另成「其他」案件，停下詢問不重複計算。
            Staff(null, inRun, ProcessMetricActions.OpenRequest, At(10, 30), 1),
            // 只有客服操作（申請在範圍前送出）：不列入平均，計入排除數。
            Staff(null, staffOnly, ProcessMetricActions.OpenRequest, At(11, 0), 2),
            Staff(null, staffOnly, ProcessMetricActions.Approve, At(11, 5), 2),
            Customer(null, chat, ProcessMetricActions.AgentSendMessage, At(12, 0)),
            Trace(chat, At(12, 0), AgentStopReason.AwaitingUserReply));

        var result = await CompareDayAsync();

        var agent = Assert.Single(result.Others);
        Assert.Equal(AfterSalesChannel.Agent, agent.Channel);
        Assert.Equal(1, agent.RunCount);
        Assert.Equal(1, agent.ExcludedRunCount);
        Assert.Equal(1, agent.StopToAskCount);
        Assert.Equal(1, agent.CustomerSteps);
        Assert.Equal(1, Assert.Single(result.Runs).StopToAskCount);
    }

    [Fact]
    public async Task InvalidRuns_ExcludedFromAllAverages()
    {
        var valid = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        var abandoned = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(11, 0));
        var empty = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(12, 0));
        Guid k1 = Guid.NewGuid(), k2 = Guid.NewGuid();
        await AddAsync(
            Customer(valid, k1, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(valid, k1, ProcessMetricActions.AgentConfirm, At(10, 2)),
            Customer(valid, k1, ProcessMetricActions.AgentRequestCreated, At(10, 2, 5)),
            // 準備了劇本、聊了幾句就放棄：沒有建立申請。
            Customer(abandoned, k2, ProcessMetricActions.AgentSendMessage, At(11, 1)),
            Customer(abandoned, k2, ProcessMetricActions.AgentSendMessage, At(11, 2)),
            Customer(abandoned, k2, ProcessMetricActions.AgentConfirm, At(11, 3)),
            Trace(k2, At(11, 1), AgentStopReason.AwaitingUserReply));
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(10, 3), 2.0);
        await AddCallAsync(k2, TokenUsageOperations.AfterSalesAgent, At(11, 4), 5.0);
        await AddCallAsync(k2, TokenUsageOperations.AfterSalesAgent, At(11, 5), 5.0);

        var result = await CompareDayAsync();

        Assert.Equal([null, BenefitMetricsService.NoRequestReason, BenefitMetricsService.NoCustomerReason], result.Runs.Select(x => x.ExclusionReason));
        Assert.Equal(empty, result.Runs[2].RunId);
        Assert.Equal(abandoned, result.Runs[1].RunId);
        var flow = Assert.Single(Assert.Single(result.Families).Flows);
        Assert.Equal(1, flow.RunCount);
        Assert.Equal(2, flow.ExcludedRunCount);
        Assert.Equal(0, flow.ReplayExcludedCount);
        // 申請建立的埋點不是點擊：步數只算送出與確認。
        Assert.Equal(2, flow.CustomerSteps);
        Assert.Equal(2.0, flow.AiCostTwd);
        Assert.Equal(1, flow.ModelCallCount);
        Assert.Equal(0, flow.StopToAskCount);
        Assert.Equal(1, flow.RequestCount);
        Assert.Equal(12.0, flow.TotalAiCostTwd);
    }

    [Fact]
    public async Task CompletedPopulation_UsedForEstimatorInputs()
    {
        // 兩次有效執行：一次已有客服決策（成本 1），一次還沒審（成本 9）。顯示的平均含兩次，推估只用已完成的那次。
        var done = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        var pending = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(11, 0));
        Guid k1 = Guid.NewGuid(), k2 = Guid.NewGuid();
        await AddAsync(
            Customer(done, k1, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(done, k1, ProcessMetricActions.AgentRequestCreated, At(10, 2)),
            Staff(done, k1, ProcessMetricActions.OpenRequest, At(10, 7), 1),
            Staff(done, k1, ProcessMetricActions.Approve, At(10, 11), 1),
            Customer(pending, k2, ProcessMetricActions.AgentSendMessage, At(11, 1)),
            Customer(pending, k2, ProcessMetricActions.AgentRequestCreated, At(11, 2)));
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(10, 3), 1.0);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(10, 4), null);
        await AddCallAsync(k2, TokenUsageOperations.AfterSalesAgent, At(11, 3), 9.0);

        var flow = Assert.Single(Assert.Single((await CompareDayAsync()).Families).Flows);

        Assert.Equal(2, flow.RunCount);
        Assert.Equal(5.0, flow.AiCostTwd);
        Assert.Equal(1, flow.CompletedRunCount);
        Assert.Equal(1.0, flow.CompletedAiCostTwd);
        Assert.Equal(10, flow.CompletedEndToEndMinutes);
        Assert.Equal(4, flow.CompletedStaffHandlingMinutes);
        Assert.Equal(2, flow.CompletedModelCallCount);
        Assert.Equal(1, flow.CompletedUnpricedCallCount);
        Assert.Equal(1, flow.UnpricedCallCount);

        var traditional = new FlowMetrics
        {
            Flow = ScenarioFlow.Traditional,
            RunCount = 1,
            CompletedRunCount = 1,
            CompletedStaffHandlingMinutes = 6,
            CompletedEndToEndMinutes = 30,
            CompletedAiCostTwd = 0,
        };
        var estimate = BenefitEstimator.Estimate(traditional, flow, new DemoAssumptionInput(0, 0, 0, 0, 0, 8, 100));
        Assert.True(estimate.IsSufficient);
        Assert.Equal(1.0, estimate.AiCostPerCaseTwd);
        Assert.Equal(4, estimate.AiStaffMinutesPerCase);
        Assert.Equal(10, estimate.AiCustomerWaitMinutes);
        Assert.Equal(1, estimate.AiCompletedRunCount);
        Assert.Contains("未定價", estimate.Warning);
    }

    [Fact]
    public async Task AiCost_MatchesTokenUsageSummary()
    {
        var single = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(9, 0));
        var multi = await AddRunAsync("A-M", "換尺碼", ScenarioFlow.MultiAgent, At(10, 0));
        Guid k1 = Guid.NewGuid(), k2 = Guid.NewGuid(), other = Guid.NewGuid();
        await AddAsync(
            Customer(single, k1, ProcessMetricActions.AgentSendMessage, At(9, 1)),
            Customer(multi, k2, ProcessMetricActions.AgentSendMessage, At(10, 1), AfterSalesChannel.AgentHandoff),
            Customer(null, other, ProcessMetricActions.AgentSendMessage, At(15, 0)));

        // 單一 Agent：兩筆已定價、一筆未定價，加上初審（同一個 CaseKey）。
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(9, 5), 1.25);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(9, 6), 0.75);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, At(9, 7), null);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesPreReview, At(9, 20), 0.5);
        // 多 Agent。
        await AddCallAsync(k2, TokenUsageOperations.AfterSalesAgentHandoff, At(10, 5), 3.0);
        // 舊案件在下一次執行期間才跑完的初審：不歸任何執行。
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesPreReview, At(10, 10), 0.4);
        // 示範模式外的案件。
        await AddCallAsync(other, TokenUsageOperations.AfterSalesAgent, At(15, 1), 0.3);
        // AI 對話紀錄已過期（沒有會話識別碼可對應）。
        await AddCallAsync(null, TokenUsageOperations.AfterSalesAgent, At(16, 0), 0.2, withCallLog: false);
        // 客服 AI 助手：每開一次一個識別碼，對不到案件，另列。
        await AddCallAsync(Guid.NewGuid(), TokenUsageOperations.AfterSalesStaffAssistant, At(16, 30), 0.6);
        // 其他作業與範圍外：都不算。
        await AddCallAsync(null, TokenUsageOperations.AiLogAnalysis, At(17, 0), 9.0);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, Day.AddSeconds(-1), 5.0);
        await AddCallAsync(k1, TokenUsageOperations.AfterSalesAgent, Day.AddDays(1), 5.0);

        var result = await CompareDayAsync();

        var singleRun = result.Runs.Single(x => x.RunId == single);
        Assert.Equal(2.5, singleRun.AiCostTwd, 6);
        Assert.Equal(4, singleRun.ModelCallCount);
        Assert.Equal(1, singleRun.UnpricedCallCount);
        Assert.Equal(3.0, result.Runs.Single(x => x.RunId == multi).AiCostTwd, 6);
        Assert.Equal(result.Runs.Sum(x => x.AiCostTwd), result.Cost.DemoRunsTwd, 6);
        Assert.Equal(0.3, result.Cost.OtherCasesTwd, 6);
        Assert.Equal(result.Others.Sum(x => x.TotalAiCostTwd), result.Cost.OtherCasesTwd, 6);
        Assert.Equal(0.6, result.Cost.UnattributedTwd, 6);
        Assert.Equal(2, result.Cost.UnattributedCallCount);
        Assert.Equal(0.6, result.Cost.StaffAssistantTwd, 6);
        Assert.Equal(1, result.Cost.StaffAssistantCallCount);

        // 與「Token 用量」頁同一天、同一作業的合計比對。
        var page = await CreateTokenUsageService().GetGroupedAsync(new TokenUsageQuery { StartDate = Day, EndDate = Day }, TokenUsageGroupBy.Operation);
        string[] caseOperations = [TokenUsageOperations.AfterSalesAgent, TokenUsageOperations.AfterSalesAgentHandoff, TokenUsageOperations.AfterSalesPreReview];
        var pageCaseCost = page.Where(x => caseOperations.Contains(x.Key)).Sum(x => x.CostTwd);
        Assert.Equal(6.4, pageCaseCost, 6);
        Assert.Equal(pageCaseCost, result.Cost.DemoRunsTwd + result.Cost.OtherCasesTwd + result.Cost.UnattributedTwd, 2);
        Assert.Equal(page.Single(x => x.Key == TokenUsageOperations.AfterSalesStaffAssistant).CostTwd, result.Cost.StaffAssistantTwd, 2);
    }

    [Fact]
    public async Task Metrics_SurviveDemoReset()
    {
        var customer1 = await SeedDemoDataAsync();
        var run = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, At(10, 0));
        var key = Guid.NewGuid();
        int requestId;
        await using (var db = factory.CreateDbContext())
        {
            var request = new AfterSalesRequest
            {
                RequestNo = "AS202610240001",
                CaseKey = key,
                CustomerUserId = customer1,
                SalesOrderItemId = await db.SalesOrderItem.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(),
                Type = AfterSalesType.SizeExchange,
                Status = AfterSalesStatus.Approved,
                Channel = AfterSalesChannel.Agent,
                Description = "尺寸偏小",
                CreatedAt = At(10, 5),
                UpdatedAt = At(10, 30),
                DecidedAt = At(10, 30),
            };
            db.AfterSalesRequest.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        await AddAsync(
            Customer(run, key, ProcessMetricActions.AgentSendMessage, At(10, 1)),
            Customer(run, key, ProcessMetricActions.AgentConfirm, At(10, 5)),
            Metric(run, key, ProcessActor.Customer, ProcessMetricActions.AgentRequestCreated, At(10, 5, 10), requestId: requestId),
            Trace(key, At(10, 1), AgentStopReason.AwaitingUserReply),
            Staff(run, key, ProcessMetricActions.OpenRequest, At(10, 20), requestId),
            Staff(run, key, ProcessMetricActions.Approve, At(10, 30), requestId));
        await AddCallAsync(key, TokenUsageOperations.AfterSalesAgent, At(10, 2), 1.5);
        await AddCallAsync(key, TokenUsageOperations.AfterSalesPreReview, At(10, 6), 0.5);

        var before = await CompareDayAsync();
        var reset = await CreateResetService().ResetAsync();
        var after = await CompareDayAsync();

        Assert.True(reset.Success, reset.Message);
        Assert.Equal(1, reset.DeletedRequests);
        var metrics = Assert.Single(after.Runs);
        Assert.Equal(1, metrics.RequestCount);
        Assert.Equal(29, metrics.EndToEndMinutes);
        Assert.Equal(10, metrics.StaffHandlingMinutes);
        Assert.Equal(2.0, metrics.AiCostTwd, 6);
        Assert.Equal(1, metrics.StopToAskCount);
        Assert.True(metrics.IsCompleted);
        Assert.Equal(2.0, Assert.Single(Assert.Single(after.Families).Flows).CompletedAiCostTwd!.Value, 6);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    private TokenUsageLogService CreateTokenUsageService()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        var settings = new SystemSettings();
        settings.ExternalFileSystem.TokenUsagePath = Path.Combine(Path.GetTempPath(), "BenefitTests-" + Guid.NewGuid().ToString("N"));
        return new TokenUsageLogService(
            factory,
            mapper,
            NullLogger<TokenUsageLogService>.Instance,
            new TokenUsageRawStore(Options.Create(settings), NullLogger<TokenUsageRawStore>.Instance),
            new NoCostCalculator(),
            new StubAiQuotaService());
    }

    /// <summary>以正式的 seeder 寫入示範帳號與目錄（重置需要），回傳 customer1 的 Id。</summary>
    private async Task<int> SeedDemoDataAsync()
    {
        await using var db = factory.CreateDbContext();
        var demoOptions = Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" });
        var rolePermissionService = new RolePermissionService();
        IDatabaseSeeder[] seeders =
        [
            new DefaultRoleViewSeeder(db, rolePermissionService, NullLogger<DefaultRoleViewSeeder>.Instance),
            new SupportUserSeeder(db, Options.Create(new BootstrapSettings { SupportPassword = "support-test-password" }), NullLogger<SupportUserSeeder>.Instance),
            new RbacBackfillSeeder(new RbacBackfillService(db, rolePermissionService, NullLogger<RbacBackfillService>.Instance), NullLogger<RbacBackfillSeeder>.Instance),
            new DemoAccountSeeder(db, new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), demoOptions, NullLogger<DemoAccountSeeder>.Instance),
            new DemoCatalogSeeder(db, demoOptions, new ManualTimeProvider(new DateTimeOffset(Day.AddHours(-8), TimeSpan.Zero)), NullLogger<DemoCatalogSeeder>.Instance),
        ];
        foreach (var seeder in seeders.OrderBy(x => x.Order))
        {
            await seeder.SeedAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }

        return (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer1)).Id;
    }

    private DemoResetService CreateResetService()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = Path.Combine(Path.GetTempPath(), "BenefitTests-" + Guid.NewGuid().ToString("N"));
        return new DemoResetService(
            factory,
            TestDemoControl.Off(factory),
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }),
            new ManualTimeProvider(new DateTimeOffset(Day.AddHours(4), TimeSpan.Zero)),
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [new AgentPhotoInbox()],
            NullLogger<DemoResetService>.Instance);
    }

    private sealed class NoCostCalculator : IAiUsageCostCalculator
    {
        public AiUsageCost? Calculate(TokenUsageEntry entry) => null;
    }
}
