using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 效益對照的指標計算（M6，Scoped，唯讀）。只讀示範重置後仍保留的紀錄：劇本執行（<see cref="DemoScenarioRun"/>）、流程埋點（<see cref="ProcessMetric"/>）、
/// Agent 執行軌跡（<see cref="AgentRunTrace"/>）、Token 用量（<see cref="TokenUsageLog"/>）與 AI 對話紀錄（<see cref="AiCallLog"/>）；
/// 不讀售後申請、初審等重置會刪掉的表，所以重置前後算出來的數字相同。
/// <list type="bullet">
/// <item>查詢範圍 [from, to)（本地時間，含頭不含尾）。開始時間落在範圍內的劇本執行才列入；每次執行只算
///   [開始時間, 下一次執行開始時間與 to 較早者) 內、且蓋上該執行編號的埋點（裁定 R1）。</item>
/// <item>「其他／未分類」：範圍內沒有執行編號的埋點，依 CaseKey 成為案件，再依管道（案件第一筆埋點的管道）彙總；
///   只有瀏覽頁面（我的訂單、物流、我的申請）的 CaseKey、以及屬於任一劇本執行的 CaseKey 不算案件。</item>
/// <item>有效才列入平均：劇本執行要有顧客操作且有建立申請（送出表單、催件或 Agent 建立申請）；「其他」案件要有顧客操作。
///   無效的照列在明細（附原因）但不列入任何平均。月度推估的每案數字只用「已完成」（另有端到端與客服處理時間）的同一群。</item>
/// <item>AI 成本口徑與「Token 用量」頁相同：範圍內三種作業（<see cref="CaseOperations"/>）的每一列用量，經 CallId 對到 AI 對話紀錄的會話識別碼，
///   落在某次執行的區間內且會話識別碼是該執行的 CaseKey → 算該執行；否則是「其他」案件的 CaseKey → 算該案件；否則算「對應不到」。
///   三者相加＝「Token 用量」頁同範圍三種作業的合計。「客服 AI 助手」對不到案件，另列。</item>
/// <item>回放一律排除：執行建立時是回放模式，或其任一 CaseKey 有回放的執行軌跡（不限時間），整筆不列入平均，明細照列並標示。</item>
/// </list>
/// </summary>
public sealed class BenefitMetricsService
{
    /// <summary>計入每案 AI 成本的作業（以 CaseKey 作會話識別碼的三種）。</summary>
    public static readonly IReadOnlyList<string> CaseOperations =
    [
        TokenUsageOperations.AfterSalesAgent,
        TokenUsageOperations.AfterSalesAgentHandoff,
        TokenUsageOperations.AfterSalesPreReview,
    ];

    public const string ReplayReason = "回放（不計）";
    public const string NoCustomerReason = "沒有顧客操作，不計";
    public const string NoRequestReason = "未建立申請，不計";

    private static readonly HashSet<string> DecisionActions =
    [
        ProcessMetricActions.Approve,
        ProcessMetricActions.Reject,
        ProcessMetricActions.RequestMoreInfo,
    ];

    /// <summary>建立申請的顧客埋點：申請筆數與「有效執行」都靠它們（AgentConfirm 只是點擊，可能是記憶卡或沒建成，不算）。</summary>
    private static readonly HashSet<string> RequestActions =
    [
        ProcessMetricActions.Submit,
        ProcessMetricActions.UrgeShipment,
        ProcessMetricActions.AgentRequestCreated,
    ];

    /// <summary>
    /// 顧客端埋點中不是顧客操作的：不算操作步數、不算「顧客操作」，也不當端到端起點。
    /// <list type="bullet">
    /// <item><see cref="ProcessMetricActions.AgentRequestCreated"/>：程式記錄的結果，不是點擊。</item>
    /// <item><see cref="ProcessMetricActions.OpenForm"/>：表單頁載入時自動記錄（裁定 R10／R11）。AI 打開線上客服頁不記錄，
    ///   兩邊都不算「打開頁面」才對等；展示控制台直接打開表單時展示者可能還在講解，也不從它起算端到端。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> NonStepActions =
    [
        ProcessMetricActions.AgentRequestCreated,
        ProcessMetricActions.OpenForm,
    ];

    /// <summary>只有這些動作的 CaseKey 是瀏覽頁面（每開一次頁面一個新 CaseKey），在「其他／未分類」不算案件。</summary>
    private static readonly HashSet<string> BrowseOnlyActions =
    [
        ProcessMetricActions.ViewOrders,
        ProcessMetricActions.ViewShipment,
        ProcessMetricActions.ViewRequests,
    ];

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ILogger<BenefitMetricsService> logger;

    public BenefitMetricsService(IDbContextFactory<BackendDBContext> contextFactory, ILogger<BenefitMetricsService> logger)
    {
        this.contextFactory = contextFactory;
        this.logger = logger;
    }

    /// <summary>範圍 [<paramref name="from"/>, <paramref name="to"/>)（本地時間）的效益對照；<paramref name="to"/> 不晚於 <paramref name="from"/> 時回空結果。</summary>
    public async Task<BenefitComparison> GetComparisonAsync(DateTime from, DateTime to)
    {
        if (to <= from)
        {
            return new BenefitComparison { From = from, To = to };
        }

        await using var context = await contextFactory.CreateDbContextAsync();

        var runs = await context.DemoScenarioRun.AsNoTracking()
            .Where(x => x.StartedAt >= from && x.StartedAt < to)
            .OrderBy(x => x.StartedAt).ThenBy(x => x.Id)
            .ToListAsync();
        // 每次執行的區間終點：下一次執行的開始時間；最後一次是 to（範圍外更晚的執行一定在 to 之後，不必另查）。
        var windows = runs.Select((run, i) => new RunWindow(run, i + 1 < runs.Count ? runs[i + 1].StartedAt : to)).ToList();
        var windowById = windows.ToDictionary(x => x.Run.Id);

        var metrics = await context.ProcessMetric.AsNoTracking()
            .Where(x => x.OccurredAt >= from && x.OccurredAt < to)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .ToListAsync();

        foreach (var metric in metrics.Where(x => x.DemoRunId is not null))
        {
            // 蓋了編號但不在該執行區間內（或執行不在範圍內）的埋點不算（裁定 R1）。
            if (windowById.TryGetValue(metric.DemoRunId!.Value, out var window) && window.Contains(metric.OccurredAt))
            {
                window.Metrics.Add(metric);
            }
        }

        // 屬於任一劇本執行的 CaseKey 不再當成「其他」案件（避免停下詢問等被算兩次）。
        var runCaseKeys = windows.SelectMany(x => x.CaseKeys).ToHashSet();
        var otherCases = metrics
            .Where(x => x.DemoRunId is null && !runCaseKeys.Contains(x.CaseKey))
            .GroupBy(x => x.CaseKey)
            .Where(g => g.Any(x => !BrowseOnlyActions.Contains(x.Action)))
            .ToDictionary(g => g.Key, g => new OtherCase(g.ToList()));

        var caseKeys = runCaseKeys.Concat(otherCases.Keys).ToList();
        var traces = caseKeys.Count == 0
            ? []
            : await context.AgentRunTrace.AsNoTracking()
                .Where(x => caseKeys.Contains(x.SessionKey))
                .Select(x => new TraceRow(x.SessionKey, x.StartedAt, x.EndedAt, x.StopReason, x.ReplayRecordingId))
                .ToListAsync();
        var tracesByKey = traces.ToLookup(x => x.SessionKey);

        // 費用是 double（REAL），SQLite 無法可靠加總，取回後在記憶體加總（同 AgentRecordingService.SumCostAsync）。
        var operations = CaseOperations.ToList();
        var tokens = await (
            from usage in context.TokenUsageLog.AsNoTracking()
            where usage.OccurredAt >= @from && usage.OccurredAt < to && operations.Contains(usage.Operation)
            from call in context.AiCallLog.AsNoTracking().Where(c => usage.CallId == (Guid?)c.CallId).DefaultIfEmpty()
            select new TokenRow(usage.OccurredAt, usage.CostTwd, call == null ? null : call.ConversationId)).ToListAsync();

        var unattributed = new List<TokenRow>();
        foreach (var token in tokens)
        {
            var window = token.ConversationId is { } conversation
                ? windows.FirstOrDefault(w => w.Contains(token.OccurredAt) && w.CaseKeys.Contains(conversation))
                : null;
            if (window is not null)
            {
                window.Tokens.Add(token);
            }
            else if (token.ConversationId is { } key && otherCases.TryGetValue(key, out var otherCase))
            {
                otherCase.Tokens.Add(token);
            }
            else
            {
                unattributed.Add(token);
            }
        }

        var assistantCosts = await context.TokenUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= from && x.OccurredAt < to && x.Operation == TokenUsageOperations.AfterSalesStaffAssistant)
            .Select(x => x.CostTwd)
            .ToListAsync();

        var runResults = windows.Select(w => (Window: w, Value: MeasureRun(w, tracesByKey))).ToList();
        var runMetrics = runResults.Select(x => ToRunMetrics(x.Window, x.Value)).ToList();
        var otherMeasured = otherCases
            .Select(c => (Channel: c.Value.Metrics[0].Channel, Value: MeasureOtherCase(c.Value, tracesByKey[c.Key], from, to)))
            .ToList();
        logger.LogDebug(
            "Benefit comparison computed. Runs={Runs}, OtherCases={OtherCases}, UnattributedCalls={UnattributedCalls}",
            runMetrics.Count, otherMeasured.Count, unattributed.Count);

        return new BenefitComparison
        {
            From = from,
            To = to,
            Families = BuildFamilies(runResults),
            Others = otherMeasured
                .GroupBy(x => x.Channel)
                .OrderBy(g => g.Key)
                .Select(g => Average(new ChannelMetrics { Channel = g.Key }, g.Select(x => x.Value).ToList()))
                .ToList(),
            Runs = runMetrics,
            Cost = new BenefitCostBreakdown
            {
                DemoRunsTwd = runMetrics.Sum(x => x.AiCostTwd),
                OtherCasesTwd = otherMeasured.Sum(x => x.Value.AiCostTwd),
                UnattributedTwd = SumCost(unattributed),
                UnattributedCallCount = unattributed.Count,
                StaffAssistantTwd = assistantCosts.Where(x => x is not null).Sum(x => x!.Value),
                StaffAssistantCallCount = assistantCosts.Count,
            },
        };
    }

    /// <summary>一次執行的實測值與排除原因（回放 → 沒有顧客操作 → 未建立申請，依序判斷）。</summary>
    private static Measured MeasureRun(RunWindow window, ILookup<Guid, TraceRow> tracesByKey)
    {
        var value = Measure(window.Metrics, window.CaseKeys.SelectMany(k => tracesByKey[k]), window.Tokens, window.Run.StartedAt, window.End);
        var isReplay = window.Run.IsReplay || value.IsReplay;
        var exclusion = isReplay ? Exclusion.Replay
            : !value.HasCustomerAction ? Exclusion.NoCustomer
            : value.RequestCount == 0 ? Exclusion.NoRequest
            : Exclusion.None;
        return value with { IsReplay = isReplay, Exclusion = exclusion };
    }

    /// <summary>「其他」案件的實測值與排除原因（回放 → 沒有顧客操作）。</summary>
    private static Measured MeasureOtherCase(OtherCase otherCase, IEnumerable<TraceRow> traces, DateTime from, DateTime to)
    {
        var value = Measure(otherCase.Metrics, traces, otherCase.Tokens, from, to);
        var exclusion = value.IsReplay ? Exclusion.Replay
            : !value.HasCustomerAction ? Exclusion.NoCustomer
            : Exclusion.None;
        return value with { Exclusion = exclusion };
    }

    private static RunMetrics ToRunMetrics(RunWindow window, Measured value)
    {
        var run = window.Run;
        return new RunMetrics
        {
            RunId = run.Id,
            ScenarioCode = run.ScenarioCode,
            Family = run.Family,
            Flow = run.Flow,
            Account = run.Account,
            IsReplay = value.IsReplay,
            ExclusionReason = value.Exclusion switch
            {
                Exclusion.Replay => ReplayReason,
                Exclusion.NoCustomer => NoCustomerReason,
                Exclusion.NoRequest => NoRequestReason,
                _ => null,
            },
            IsCompleted = value.IsCompleted,
            StartedAt = run.StartedAt,
            EndBoundary = window.End,
            LastActivityAt = value.LastActivityAt,
            CaseCount = window.CaseKeys.Count,
            CustomerSteps = value.CustomerSteps,
            StaffSteps = value.StaffSteps,
            EndToEndMinutes = value.EndToEndMinutes,
            StaffHandlingMinutes = value.StaffHandlingMinutes,
            AiCostTwd = value.AiCostTwd,
            UnpricedCallCount = value.UnpricedCallCount,
            ModelCallCount = value.ModelCallCount,
            StopToAskCount = value.StopToAskCount,
            AwaitingPhotoCount = value.AwaitingPhotoCount,
            RequestCount = value.RequestCount,
        };
    }

    /// <summary>
    /// 一組埋點（一次執行或一個案件）的實測值，排除原因由呼叫端填。<paramref name="traces"/> 是這些 CaseKey 的全部軌跡（回放判斷不限時間），
    /// 停下詢問與等待上傳只算 [<paramref name="start"/>, <paramref name="end"/>) 內開始的軌跡。
    /// </summary>
    private static Measured Measure(IReadOnlyList<ProcessMetric> metrics, IEnumerable<TraceRow> traces, IReadOnlyList<TokenRow> tokens, DateTime start, DateTime end)
    {
        var ordered = metrics.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToList();
        var customer = ordered.Where(x => x.Actor == ProcessActor.Customer).ToList();
        var customerActions = customer.Where(x => !NonStepActions.Contains(x.Action)).ToList();
        var staff = ordered.Where(x => x.Actor == ProcessActor.Staff).ToList();

        // 同一張表連續的「填寫細節」合併成 1 步（每改一個欄位就記一次）。
        var customerSteps = 0;
        ProcessMetric? previous = null;
        foreach (var metric in customerActions)
        {
            var merged = metric.Action == ProcessMetricActions.FillDetail
                && previous is { Action: ProcessMetricActions.FillDetail }
                && previous.CaseKey == metric.CaseKey;
            if (!merged)
            {
                customerSteps++;
            }

            previous = metric;
        }

        // 端到端起點（裁定 R10）：第一個顧客操作。customerActions 已排除自動記錄的「開啟表單」，傳統從第一次選擇或填寫算起，
        // AI 做法從第一句話算起。
        double? endToEnd = null;
        if (customerActions.Count > 0)
        {
            var firstCustomer = customerActions[0].OccurredAt;
            var firstDecision = staff.FirstOrDefault(x => DecisionActions.Contains(x.Action) && x.OccurredAt >= firstCustomer);
            if (firstDecision is not null)
            {
                endToEnd = (firstDecision.OccurredAt - firstCustomer).TotalMinutes;
            }
        }

        // 客服處理：每筆申請「第一個決策之前的第一次開啟 → 第一個決策」，各筆相加。
        double? staffHandling = null;
        foreach (var request in staff.Where(x => x.AfterSalesRequestId is not null).GroupBy(x => x.AfterSalesRequestId))
        {
            var decision = request.FirstOrDefault(x => DecisionActions.Contains(x.Action));
            var open = decision is null
                ? null
                : request.FirstOrDefault(x => x.Action == ProcessMetricActions.OpenRequest && x.OccurredAt <= decision.OccurredAt);
            if (decision is not null && open is not null)
            {
                staffHandling = (staffHandling ?? 0) + (decision.OccurredAt - open.OccurredAt).TotalMinutes;
            }
        }

        var allTraces = traces.ToList();
        var windowTraces = allTraces.Where(x => x.StartedAt >= start && x.StartedAt < end).ToList();

        DateTime?[] activity =
        [
            ordered.Count > 0 ? ordered[^1].OccurredAt : null,
            tokens.Count > 0 ? tokens.Max(x => x.OccurredAt) : null,
            windowTraces.Count > 0 ? windowTraces.Max(x => x.EndedAt ?? x.StartedAt) : null,
        ];

        return new Measured(
            IsReplay: allTraces.Any(x => x.ReplayRecordingId is not null),
            HasCustomerAction: customerActions.Count > 0,
            LastActivityAt: activity.Max(),
            CustomerSteps: customerSteps,
            StaffSteps: staff.Count,
            EndToEndMinutes: endToEnd,
            StaffHandlingMinutes: staffHandling,
            AiCostTwd: SumCost(tokens),
            UnpricedCallCount: tokens.Count(x => x.CostTwd is null),
            ModelCallCount: tokens.Count,
            StopToAskCount: windowTraces.Count(x => x.StopReason == AgentStopReason.AwaitingUserReply),
            AwaitingPhotoCount: windowTraces.Count(x => x.StopReason == AgentStopReason.AwaitingPhotoUpload),
            RequestCount: customer.Count(x => RequestActions.Contains(x.Action)));
    }

    private static List<FamilyComparison> BuildFamilies(List<(RunWindow Window, Measured Value)> runs)
    {
        // 家族順序同展示控制台的劇本順序；不在劇本清單裡的家族排最後（依名稱）。
        var order = DemoScenarios.All.Select(x => x.Family).Distinct().Select((family, i) => (family, i)).ToDictionary(x => x.family, x => x.i);
        return runs
            .GroupBy(x => x.Window.Run.Family)
            .OrderBy(g => order.TryGetValue(g.Key, out var i) ? i : int.MaxValue).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(family => new FamilyComparison
            {
                Family = family.Key,
                Flows = family
                    .GroupBy(x => x.Window.Run.Flow)
                    .OrderBy(g => g.Key)
                    .Select(flow => Average(new FlowMetrics { Family = family.Key, Flow = flow.Key }, flow.Select(x => x.Value).ToList()))
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 平均列入統計（沒有排除原因）的筆數；可能沒有值的指標只平均有值的筆數。
    /// 「已完成」是列入統計且端到端、客服處理都有值的同一群，月度推估的每案數字只用它們。
    /// </summary>
    private static T Average<T>(T target, List<Measured> all) where T : BenefitAverages
    {
        var included = all.Where(x => x.Exclusion == Exclusion.None).ToList();
        var completed = included.Where(x => x.IsCompleted).ToList();
        var endToEnd = included.Where(x => x.EndToEndMinutes is not null).Select(x => x.EndToEndMinutes!.Value).ToList();
        var staffHandling = included.Where(x => x.StaffHandlingMinutes is not null).Select(x => x.StaffHandlingMinutes!.Value).ToList();

        double? Avg(Func<Measured, double> selector) => included.Count == 0 ? null : included.Average(selector);
        double? CompletedAvg(Func<Measured, double> selector) => completed.Count == 0 ? null : completed.Average(selector);

        target.RunCount = included.Count;
        target.ReplayExcludedCount = all.Count(x => x.Exclusion == Exclusion.Replay);
        target.ExcludedRunCount = all.Count(x => x.Exclusion is Exclusion.NoCustomer or Exclusion.NoRequest);
        target.CustomerSteps = Avg(x => x.CustomerSteps);
        target.StaffSteps = Avg(x => x.StaffSteps);
        target.EndToEndMinutes = endToEnd.Count == 0 ? null : endToEnd.Average();
        target.EndToEndSampleCount = endToEnd.Count;
        target.StaffHandlingMinutes = staffHandling.Count == 0 ? null : staffHandling.Average();
        target.StaffHandlingSampleCount = staffHandling.Count;
        target.AiCostTwd = Avg(x => x.AiCostTwd);
        target.ModelCallCount = Avg(x => x.ModelCallCount);
        target.StopToAskCount = Avg(x => x.StopToAskCount);
        target.AwaitingPhotoCount = Avg(x => x.AwaitingPhotoCount);
        target.RequestCount = Avg(x => x.RequestCount);
        target.UnpricedCallCount = included.Sum(x => x.UnpricedCallCount);
        target.CompletedRunCount = completed.Count;
        target.CompletedEndToEndMinutes = CompletedAvg(x => x.EndToEndMinutes!.Value);
        target.CompletedStaffHandlingMinutes = CompletedAvg(x => x.StaffHandlingMinutes!.Value);
        target.CompletedAiCostTwd = CompletedAvg(x => x.AiCostTwd);
        target.CompletedModelCallCount = completed.Sum(x => x.ModelCallCount);
        target.CompletedUnpricedCallCount = completed.Sum(x => x.UnpricedCallCount);
        target.TotalAiCostTwd = all.Sum(x => x.AiCostTwd);
        return target;
    }

    private static double SumCost(IEnumerable<TokenRow> tokens) => tokens.Where(x => x.CostTwd is not null).Sum(x => x.CostTwd!.Value);

    private sealed class RunWindow(DemoScenarioRun run, DateTime end)
    {
        public DemoScenarioRun Run { get; } = run;
        public DateTime End { get; } = end;
        public List<ProcessMetric> Metrics { get; } = [];
        public List<TokenRow> Tokens { get; } = [];

        private HashSet<Guid>? caseKeys;

        /// <summary>這次執行（區間內埋點）的 CaseKey；埋點分配完才讀。</summary>
        public HashSet<Guid> CaseKeys => caseKeys ??= Metrics.Select(x => x.CaseKey).ToHashSet();

        public bool Contains(DateTime at) => at >= Run.StartedAt && at < End;
    }

    private sealed class OtherCase(List<ProcessMetric> metrics)
    {
        public List<ProcessMetric> Metrics { get; } = metrics;
        public List<TokenRow> Tokens { get; } = [];
    }

    private sealed record TraceRow(Guid SessionKey, DateTime StartedAt, DateTime? EndedAt, AgentStopReason? StopReason, int? ReplayRecordingId);

    private sealed record TokenRow(DateTime OccurredAt, double? CostTwd, Guid? ConversationId);

    private enum Exclusion
    {
        None,
        Replay,
        NoCustomer,
        NoRequest,
    }

    private sealed record Measured(
        bool IsReplay,
        bool HasCustomerAction,
        DateTime? LastActivityAt,
        int CustomerSteps,
        int StaffSteps,
        double? EndToEndMinutes,
        double? StaffHandlingMinutes,
        double AiCostTwd,
        int UnpricedCallCount,
        int ModelCallCount,
        int StopToAskCount,
        int AwaitingPhotoCount,
        int RequestCount)
    {
        public Exclusion Exclusion { get; init; }

        /// <summary>已完成：列入統計，且端到端與客服處理時間都有值。</summary>
        public bool IsCompleted => Exclusion == Exclusion.None && EndToEndMinutes is not null && StaffHandlingMinutes is not null;
    }
}
