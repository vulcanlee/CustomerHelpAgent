using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 一輪初審的 executor 共用的依賴與識別（由 <see cref="PreReviewWorkflowFactory"/> 每次建 Workflow 時建立）。
/// ⚠️ 這些欄位不進 checkpoint：重啟後由 Runner 依資料庫的 <c>PreReviewRun</c> 重建，所以只能放「同一輪不會變」的東西。
/// </summary>
public sealed class PreReviewNodeContext
{
    public required int RunId { get; init; }
    public required int RequestId { get; init; }
    public required PreReviewRunService Runs { get; init; }
    public required AfterSalesRequestService Requests { get; init; }
    public required AfterSalesPolicyService Policies { get; init; }
    public required ShopOrderService Orders { get; init; }
    public required AfterSalesDecisionService Decisions { get; init; }
    public required IPhotoReviewer PhotoReviewer { get; init; }
    public required IHistoryReviewer HistoryReviewer { get; init; }
    public required IPreReviewSummarizer Summarizer { get; init; }
    public required PreReviewSignal Signal { get; init; }
    public required TimeProvider Time { get; init; }
    public required ILogger Logger { get; init; }
}

/// <summary>
/// 初審節點的基底：每個節點都只經 <see cref="HandleAsync"/> 進入，節點狀態一律經 <see cref="MarkAsync(NodeStatus, string?)"/>
/// （寫入 <c>PreReviewNodeState</c> 並發出 <see cref="PreReviewSignal"/>）。
/// 節點只用 <c>SendMessageAsync</c> 送訊息，要送的型別列在 <see cref="SentMessageTypes"/>（框架會擋下未宣告的型別）。
/// </summary>
public abstract class PreReviewExecutor<TInput> : Executor<TInput>
{
    /// <summary>Intake 存放申請事實的共用狀態（具名 scope 跨 executor 共用，並隨 checkpoint 保存）。</summary>
    internal const string StateScope = "prereview";
    internal const string CaseStateKey = "case";

    protected PreReviewExecutor(string id, PreReviewNode node, PreReviewNodeContext nodes)
        : base(id)
    {
        Node = node;
        Nodes = nodes;
    }

    protected PreReviewNode Node { get; }

    protected PreReviewNodeContext Nodes { get; }

    /// <summary>這個節點會送出的訊息型別。</summary>
    protected virtual IEnumerable<Type> SentMessageTypes => [];

    /// <summary>這個節點會產出的 Workflow output 型別。</summary>
    protected virtual IEnumerable<Type> YieldedOutputTypes => [];

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => base.ConfigureProtocol(protocolBuilder)
            .SendsMessageTypes(SentMessageTypes)
            .YieldsOutputTypes(YieldedOutputTypes);

    /// <summary>
    /// 節點的唯一入口。進入節點前的共同檢查只加在這裡：
    /// 這一輪已由人工處理（<see cref="PreReviewStatus.Superseded"/>，客服沒等 AI 就直接決定）→ 不執行、不送訊息，Workflow 就此結束，
    /// 之後的摘要、執行、通知都不會發生。已經在跑的節點（例如卡在模型呼叫）會跑完自己並存下結論，只供參考。
    /// </summary>
    public sealed override async ValueTask HandleAsync(TInput message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if ((await Nodes.Runs.GetAsync(Nodes.RunId))?.Status == PreReviewStatus.Superseded)
        {
            Nodes.Logger.LogInformation("Pre-review node skipped because the run was superseded by a staff decision. RunId={RunId}, Node={Node}", Nodes.RunId, Node);
            return;
        }

        await RunNodeAsync(message, context, cancellationToken);
    }

    protected abstract ValueTask RunNodeAsync(TInput message, IWorkflowContext context, CancellationToken cancellationToken);

    protected Task MarkAsync(NodeStatus status, string? note = null) => MarkAsync(Node, status, note);

    protected async Task MarkAsync(PreReviewNode node, NodeStatus status, string? note = null)
    {
        await Nodes.Runs.SetNodeAsync(Nodes.RunId, node, status, note);
        Nodes.Signal.Publish(Nodes.RequestId);
    }

    /// <summary>
    /// 是取消（應用程式關閉、Runner 被丟棄）而不是節點失敗：這種例外不可吞下改成 Failed 結論，
    /// 否則該 superstep 會帶著假的失敗結論寫進 checkpoint，重啟後就無法重跑。
    /// </summary>
    protected static bool IsCancellation(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    protected static string Serialize(PreReviewFinding finding)
        => JsonSerializer.Serialize(finding, PreReviewJsonContext.Default.PreReviewFinding);

    protected static NodeStatus ToNodeStatus(FindingStatus status) => status switch
    {
        FindingStatus.Ok => NodeStatus.Done,
        FindingStatus.NotApplicable => NodeStatus.Skipped,
        _ => NodeStatus.Failed,
    };

    /// <summary>
    /// 這一輪已存過的本節點結論（照片、規則、歷史、摘要才有）；沒有回 null。
    /// 節點冪等的依據：checkpoint 只在整個 superstep 完成時寫入，同一 superstep 先完成的節點在重啟後會被再投遞一次，
    /// 以資料庫裡的結論為準就不會重複呼叫模型。
    /// </summary>
    protected async Task<PreReviewFinding?> LoadSavedFindingAsync()
    {
        var run = await Nodes.Runs.GetAsync(Nodes.RunId);
        var json = Node switch
        {
            PreReviewNode.PhotoReview => run?.PhotoJson,
            PreReviewNode.RuleCheck => run?.RuleJson,
            PreReviewNode.HistoryReview => run?.HistoryJson,
            PreReviewNode.Summary => run?.SummaryJson,
            _ => null,
        };
        return json is null ? null : JsonSerializer.Deserialize(json, PreReviewJsonContext.Default.PreReviewFinding);
    }
}

/// <summary>三路初審（照片、規則、歷史）的共同收尾：存結論、標節點狀態、把結論送往 fan-in。</summary>
public abstract class PreReviewFindingExecutor(string id, PreReviewNode node, PreReviewNodeContext nodes)
    : PreReviewExecutor<PreReviewCase>(id, node, nodes)
{
    protected override IEnumerable<Type> SentMessageTypes => [typeof(PreReviewFinding)];

    protected override async ValueTask RunNodeAsync(PreReviewCase message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        if (await LoadSavedFindingAsync() is { } saved)
        {
            // 續跑重送：重啟前這個節點已完成並存檔（但同一個 superstep 的其他節點沒跑完，checkpoint 沒寫成），不再呼叫模型。
            // ⚠️ 存下的若是 Failed 結論，也照樣沿用、不重試：「AI 失敗不擋人工」，Workflow 要盡快走到人工決策；
            // 想重試由客服按「重新初審」（AfterSalesReviewCoordinator.RetryAsync，新的一輪）。
            Nodes.Logger.LogInformation("Pre-review node already finished before restart; resending its finding. RunId={RunId}, Node={Node}", Nodes.RunId, Node);
            await context.SendMessageAsync(saved, cancellationToken);
            return;
        }

        await MarkAsync(NodeStatus.Running);
        PreReviewFinding finding;
        try
        {
            finding = (await ReviewAsync(message, cancellationToken)) with { Node = Node };
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            // 例外內容可能含模型回應或內部訊息：只記日誌，不寫進給客服看的結論。
            Nodes.Logger.LogWarning(ex, "Pre-review node failed; continuing with a failed finding. RunId={RunId}, Node={Node}", Nodes.RunId, Node);
            finding = new PreReviewFinding(Node, FindingStatus.Failed, FailedHeadline, "{}");
        }

        await Nodes.Runs.SaveFindingAsync(Nodes.RunId, Node, Serialize(finding));
        await MarkAsync(ToNodeStatus(finding.Status), finding.Headline);
        await context.SendMessageAsync(finding, cancellationToken);
    }

    /// <summary>節點失敗時給客服看的固定說明。</summary>
    protected abstract string FailedHeadline { get; }

    protected abstract Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken);
}

/// <summary>進件：讀申請事實（不含個資）組成 <see cref="PreReviewCase"/>，存進共用狀態並 fan-out 給三路初審。</summary>
public sealed class IntakeExecutor(PreReviewNodeContext nodes)
    : PreReviewExecutor<PreReviewStart>(PreReviewWorkflowFactory.IntakeId, PreReviewNode.Intake, nodes)
{
    private const string RequestNotFound = "找不到這筆售後申請";
    private const string RequestAlreadyDecided = "申請已由人工處理，略過 AI 初審";

    protected override IEnumerable<Type> SentMessageTypes => [typeof(PreReviewCase)];

    protected override async ValueTask RunNodeAsync(PreReviewStart message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        await MarkAsync(NodeStatus.Running);
        var detail = await Nodes.Requests.GetReviewDetailAsync(Nodes.RequestId);
        if (detail is null)
        {
            Nodes.Logger.LogWarning("Pre-review request not found. RunId={RunId}, RequestId={RequestId}", Nodes.RunId, Nodes.RequestId);
            await Nodes.Runs.SetStatusAsync(Nodes.RunId, PreReviewStatus.Failed, RequestNotFound);
            await MarkAsync(NodeStatus.Failed, RequestNotFound);
            return;
        }

        if (detail.Status != AfterSalesStatus.Submitted)
        {
            // 排入後、開始前申請已被決定（例如「重新初審」與直接決定同時發生）：不呼叫任何模型，這一輪就此結束。
            Nodes.Logger.LogInformation("Pre-review run ended at intake because the request is no longer submitted. RunId={RunId}, RequestStatus={RequestStatus}", Nodes.RunId, detail.Status);
            await Nodes.Runs.SupersedeAsync(Nodes.RequestId);
            await MarkAsync(NodeStatus.Skipped, RequestAlreadyDecided);
            return;
        }

        var now = Nodes.Time.GetLocalNow().DateTime;
        var reviewCase = new PreReviewCase(
            Nodes.RunId,
            Nodes.RequestId,
            detail.CaseKey,
            detail.Type,
            detail.ProductName,
            detail.ProductCategory,
            detail.Color,
            detail.Size,
            detail.TargetColor,
            detail.TargetSize,
            AfterSalesEligibility.DaysSinceDelivery(detail.DeliveredAt, now),
            detail.IsWorn,
            detail.Description,
            detail.Attachments.Select(x => x.Id).ToList());
        await context.QueueStateUpdateAsync(CaseStateKey, reviewCase, StateScope, cancellationToken);
        await MarkAsync(NodeStatus.Done, AfterSalesLabels.TypeText(detail.Type));
        await context.SendMessageAsync(reviewCase, cancellationToken);
    }
}

/// <summary>照片初審（AI）。沒有附照片時回 <see cref="FindingStatus.NotApplicable"/>，不呼叫模型。</summary>
public sealed class PhotoReviewExecutor(PreReviewNodeContext nodes)
    : PreReviewFindingExecutor(PreReviewWorkflowFactory.PhotoReviewId, PreReviewNode.PhotoReview, nodes)
{
    protected override string FailedHeadline => "照片初審失敗，請客服自行檢視照片";

    protected override Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
        => reviewCase.AttachmentIds.Count == 0
            ? Task.FromResult(new PreReviewFinding(Node, FindingStatus.NotApplicable, "未附照片，略過照片初審", "{}"))
            : Nodes.PhotoReviewer.ReviewAsync(reviewCase, cancellationToken);
}

/// <summary>歷史紀錄初審（AI）。</summary>
public sealed class HistoryReviewExecutor(PreReviewNodeContext nodes)
    : PreReviewFindingExecutor(PreReviewWorkflowFactory.HistoryReviewId, PreReviewNode.HistoryReview, nodes)
{
    protected override string FailedHeadline => "歷史紀錄初審失敗，請客服自行查詢過往申請";

    protected override Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
        => Nodes.HistoryReviewer.ReviewAsync(reviewCase, cancellationToken);
}

/// <summary>
/// 規則核對（純程式，不呼叫模型）：與顧客端 <c>get_effective_policy</c> 同一套判定
/// （<c>AfterSalesPolicyService.GetEffectiveAsync</c>＋<see cref="AfterSalesEligibility.Evaluate"/>），換貨類另查目標規格庫存、
/// 品質問題另查同規格庫存（核准時要補寄同規格，沒貨核准就會失敗，AI 不可建議核准）。
/// </summary>
public sealed class RuleCheckExecutor(PreReviewNodeContext nodes)
    : PreReviewFindingExecutor(PreReviewWorkflowFactory.RuleCheckId, PreReviewNode.RuleCheck, nodes)
{
    protected override string FailedHeadline => "規則核對失敗，請客服自行核對規則";

    protected override async Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
    {
        var detail = await Nodes.Requests.GetReviewDetailAsync(Nodes.RequestId)
            ?? throw new InvalidOperationException("After-sales request not found.");
        var now = Nodes.Time.GetLocalNow().DateTime;
        var lookup = await Nodes.Policies.GetEffectiveAsync(reviewCase.Type, reviewCase.ProductCategory, now);
        if (lookup.Status != PolicyLookupStatus.Found)
        {
            var reason = lookup.Status == PolicyLookupStatus.Conflict
                ? "這類問題同時有多條生效的規則，無法自動判定"
                : "查無這類問題適用的售後規則";
            return Finding($"不符資格：{reason}", new RuleCheckResult(
                null, null, null, reviewCase.DaysSinceDelivery, false, [reason], [], null));
        }

        var policy = lookup.Policies[0];
        var shippedAt = reviewCase.Type == AfterSalesType.ShipmentUrge
            ? (await Nodes.Orders.GetMyOrderDetailAsync(detail.CustomerUserId, detail.SalesOrderId))?.Shipment?.ShippedAt
            : null;
        var eligibility = AfterSalesEligibility.Evaluate(
            policy, detail.DeliveredAt, detail.IsWorn, hasPhoto: reviewCase.AttachmentIds.Count > 0, now, shippedAt);

        bool? targetInStock = null;
        var unmet = eligibility.UnmetReasons.ToList();
        if (reviewCase.Type is AfterSalesType.SizeExchange or AfterSalesType.ColorExchange or AfterSalesType.QualityIssue)
        {
            var targetColor = reviewCase.Type == AfterSalesType.ColorExchange ? reviewCase.TargetColor : reviewCase.Color;
            var targetSize = reviewCase.Type == AfterSalesType.SizeExchange ? reviewCase.TargetSize : reviewCase.Size;
            var variants = await Nodes.Orders.GetVariantStockAsync(detail.CustomerUserId, detail.OrderItemId);
            targetInStock = variants.Any(x => x.Color == targetColor && x.Size == targetSize && x.Stock > 0);
            if (targetInStock == false)
            {
                unmet.Add(reviewCase.Type == AfterSalesType.QualityIssue
                    ? $"同規格 {targetColor} / {targetSize} 已無庫存，無法補寄"
                    : $"目標規格 {targetColor} / {targetSize} 已無庫存");
            }
        }

        var result = new EligibilityResult(unmet.Count == 0 && eligibility.MissingInfo.Count == 0, unmet, eligibility.MissingInfo);
        var headline = result.IsEligible
            ? $"符合「{policy.Title}」（第 {policy.Version} 版）"
            : $"不符資格：{AfterSalesEligibility.DescribeUnmet(result)}";
        return Finding(headline, new RuleCheckResult(
            policy.Id,
            policy.Title,
            policy.Version,
            AfterSalesEligibility.DaysSinceDelivery(detail.DeliveredAt, now),
            result.IsEligible,
            result.UnmetReasons,
            result.MissingInfo,
            targetInStock));
    }

    private PreReviewFinding Finding(string headline, RuleCheckResult result)
        => new(Node, FindingStatus.Ok, headline, JsonSerializer.Serialize(result, PreReviewJsonContext.Default.RuleCheckResult));
}

/// <summary>
/// 摘要（AI）：fan-in barrier 把三路結論在同一個 superstep 送進來，收齊三個才呼叫摘要模型（只呼叫一次）。
/// 收到的結論暫存在欄位，並以 <see cref="OnCheckpointingAsync"/>／<see cref="OnCheckpointRestoredAsync"/> 存回 Workflow 狀態。
/// 不讓模型蓋過規則：規則核對沒有完成（結論不是 Ok）→ 一律改為需人工判斷並標警示；規則核對不符資格而建議核准 → 同樣改為需人工判斷。
/// 摘要模型失敗 → 改用程式產生的固定摘要。這一輪已存過摘要（續跑時重送）→ 不再呼叫模型，直接送出人工決策請求。
/// </summary>
public sealed class SummaryExecutor(PreReviewNodeContext nodes)
    : PreReviewExecutor<PreReviewFinding>(PreReviewWorkflowFactory.SummaryId, PreReviewNode.Summary, nodes)
{
    /// <summary>規則核對不符資格、摘要卻建議核准時的警示（存在 <see cref="PreReviewSummary.Warning"/>）。</summary>
    public const string IneligibleWarning = "AI 建議已被程式改為需人工判斷：規則核對不符資格";

    /// <summary>規則核對沒有完成（失敗）時的警示：沒有規則依據，任何建議都改為需人工判斷。</summary>
    public const string RuleIncompleteWarning = "規則核對未完成，AI 建議改為需人工判斷";

    private const string FindingsStateKey = "findings";
    private static readonly PreReviewNode[] ExpectedNodes = [PreReviewNode.PhotoReview, PreReviewNode.RuleCheck, PreReviewNode.HistoryReview];

    private readonly Dictionary<PreReviewNode, PreReviewFinding> findings = [];

    protected override IEnumerable<Type> SentMessageTypes => [typeof(DecisionRequest)];

    protected override async ValueTask RunNodeAsync(PreReviewFinding message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        bool first;
        List<PreReviewFinding> collected;
        lock (findings)
        {
            first = findings.Count == 0;
            findings[message.Node] = message;
            collected = Snapshot();
        }

        if (first)
        {
            await MarkAsync(NodeStatus.Running);
        }

        if (!ExpectedNodes.All(node => collected.Any(x => x.Node == node)))
        {
            return;
        }

        if (await LoadSavedFindingAsync() is { } saved)
        {
            // 續跑重送：摘要已在重啟前產生並存檔，不再呼叫模型（存的是程式摘要〔Failed〕也一樣沿用，見 PreReviewFindingExecutor）。
            // 節點狀態補回存檔時的結果；建議與警示旗標再寫一次（冪等）：存結論與存摘要欄位是兩次寫入，中間當機會讓佇列頁少了建議。
            var savedSummary = JsonSerializer.Deserialize(saved.Json, PreReviewJsonContext.Default.PreReviewSummary);
            await Nodes.Runs.SaveSummaryAsync(Nodes.RunId, savedSummary?.Recommendation ?? PreReviewRecommendations.Manual, savedSummary?.Warning is not null);
            await MarkAsync(ToNodeStatus(saved.Status), saved.Headline);
            await context.SendMessageAsync(new DecisionRequest(Nodes.RunId, null), cancellationToken);
            return;
        }

        var status = FindingStatus.Ok;
        PreReviewSummary summary;
        try
        {
            var reviewCase = await context.ReadStateAsync<PreReviewCase>(CaseStateKey, StateScope, cancellationToken)
                ?? throw new InvalidOperationException("Pre-review case state is missing.");
            summary = Normalize(await Nodes.Summarizer.SummarizeAsync(reviewCase, collected, cancellationToken));
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            Nodes.Logger.LogWarning(ex, "Pre-review summary failed; using the program summary. RunId={RunId}", Nodes.RunId);
            status = FindingStatus.Failed;
            summary = FallbackSummary(collected);
        }

        if (RuleWarning(collected, summary.Recommendation) is { } warning)
        {
            summary = summary with { Recommendation = PreReviewRecommendations.Manual, Warning = warning };
        }

        var hasWarning = summary.Warning is not null;

        var json = JsonSerializer.Serialize(summary, PreReviewJsonContext.Default.PreReviewSummary);
        await Nodes.Runs.SaveFindingAsync(Nodes.RunId, Node, Serialize(new PreReviewFinding(Node, status, summary.Headline, json)));
        await Nodes.Runs.SaveSummaryAsync(Nodes.RunId, summary.Recommendation, hasWarning);
        await MarkAsync(ToNodeStatus(status), summary.Headline);
        await context.SendMessageAsync(new DecisionRequest(Nodes.RunId, null), cancellationToken);
    }

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        List<PreReviewFinding> collected;
        lock (findings)
        {
            collected = Snapshot();
        }

        await context.QueueStateUpdateAsync(FindingsStateKey, collected, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var saved = await context.ReadStateAsync<List<PreReviewFinding>>(FindingsStateKey, cancellationToken);
        lock (findings)
        {
            findings.Clear();
            foreach (var finding in saved ?? [])
            {
                findings[finding.Node] = finding;
            }
        }
    }

    /// <summary>依節點順序（照片、規則、歷史）排列。</summary>
    private List<PreReviewFinding> Snapshot() => findings.Values.OrderBy(x => x.Node).ToList();

    private static PreReviewSummary Normalize(PreReviewSummary summary) => summary with
    {
        Recommendation = PreReviewRecommendations.All.Contains(summary.Recommendation) ? summary.Recommendation : PreReviewRecommendations.Manual,
        Headline = summary.Headline ?? string.Empty,
        Reasons = summary.Reasons ?? [],
        StaffChecklist = summary.StaffChecklist ?? [],

        // 警示只由程式設定，模型輸出的值一律不採用。
        Warning = null,
    };

    /// <summary>規則要求降為需人工判斷時回傳警示文字，否則 null。</summary>
    private static string? RuleWarning(IEnumerable<PreReviewFinding> collected, string recommendation)
    {
        var rule = collected.FirstOrDefault(x => x.Node == PreReviewNode.RuleCheck);
        if (rule is null || rule.Status != FindingStatus.Ok)
        {
            return RuleIncompleteWarning;
        }

        return recommendation == PreReviewRecommendations.Approve
            && JsonSerializer.Deserialize(rule.Json, PreReviewJsonContext.Default.RuleCheckResult) is { IsEligible: false }
            ? IneligibleWarning
            : null;
    }

    private static PreReviewSummary FallbackSummary(IReadOnlyList<PreReviewFinding> collected) => new(
        PreReviewRecommendations.Manual,
        "AI 摘要無法產生，請依各項初審結果人工判斷",
        collected.Select(x => $"{NodeText(x.Node)}：{x.Headline}").ToList(),
        ["檢視申請內容與照片", "確認規則核對結果"]);

    private static string NodeText(PreReviewNode node) => node switch
    {
        PreReviewNode.PhotoReview => "照片初審",
        PreReviewNode.RuleCheck => "規則核對",
        PreReviewNode.HistoryReview => "歷史紀錄",
        _ => node.ToString(),
    };
}

/// <summary>
/// 執行（純程式）：客服決定後呼叫 <see cref="AfterSalesDecisionService.ApplyAsync"/>（狀態守門、開單、扣庫存、通知顧客）。
/// 執行失敗而申請仍是已提交（例如目標規格剛好賣完）→ 回到人工決策並帶上原因；
/// 申請已被其他路徑決定 → 這一輪標為已由人工處理（Superseded）並結束，不再等待；
/// 但若申請正是這個決定生效後的樣子（重啟後重跑，上一次已 commit），當成成功收尾。
/// </summary>
public sealed class ExecuteExecutor(PreReviewNodeContext nodes)
    : PreReviewExecutor<StaffDecision>(PreReviewWorkflowFactory.ExecuteId, PreReviewNode.Execute, nodes)
{
    protected override IEnumerable<Type> SentMessageTypes => [typeof(ExecutionResult), typeof(DecisionRequest)];

    protected override IEnumerable<Type> YieldedOutputTypes => [typeof(ExecutionResult)];

    protected override async ValueTask RunNodeAsync(StaffDecision message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        await MarkAsync(PreReviewNode.StaffDecision, NodeStatus.Done, DecisionText(message.Kind));
        await MarkAsync(NodeStatus.Running);
        await Nodes.Runs.SetStatusAsync(Nodes.RunId, PreReviewStatus.Executing);
        var staffUserId = message.StaffUserId > 0 ? message.StaffUserId : (int?)null;
        var result = await Nodes.Decisions.ApplyAsync(Nodes.RequestId, message.Kind, message.Note, staffUserId);
        if (!result.Success)
        {
            var current = await Nodes.Requests.GetReviewDetailAsync(Nodes.RequestId);
            if (current?.Status == AfterSalesStatus.Submitted)
            {
                await context.YieldOutputAsync(new ExecutionResult(result), cancellationToken);
                await MarkAsync(NodeStatus.Failed, result.Message);
                Nodes.Logger.LogInformation("Pre-review execution failed; waiting for another staff decision. RunId={RunId}", Nodes.RunId);
                await context.SendMessageAsync(new DecisionRequest(Nodes.RunId, result.Message), cancellationToken);
                return;
            }

            // 重啟後重跑：上一次的 ApplyAsync 其實已 commit（這個 superstep 沒來得及寫進 checkpoint），這次才會拿到「狀態已變更」。
            // 申請正是這個決定生效後的樣子 → 當成成功收尾（不再執行、不再通知）。上一次 commit 之後若還沒通知就當機，那則通知會遺失（沒有 outbox）。
            if (await Nodes.Decisions.GetAppliedResultAsync(Nodes.RequestId, message.Kind, message.Note, staffUserId) is { } applied)
            {
                Nodes.Logger.LogInformation("Pre-review execution found its decision already applied before a restart. RunId={RunId}", Nodes.RunId);
                result = applied;
            }
            else
            {
                await context.YieldOutputAsync(new ExecutionResult(result), cancellationToken);
                await MarkAsync(NodeStatus.Failed, result.Message);
                Nodes.Logger.LogInformation("Pre-review run ended because the request was decided elsewhere. RunId={RunId}", Nodes.RunId);
                await Nodes.Runs.SetStatusAsync(Nodes.RunId, PreReviewStatus.Superseded, result.Message);
                Nodes.Signal.Publish(Nodes.RequestId);
                return;
            }
        }

        await context.YieldOutputAsync(new ExecutionResult(result), cancellationToken);
        await MarkAsync(NodeStatus.Done, result.FulfillmentNo is { } no ? $"處理單號 {no}" : DecisionText(message.Kind));
        await context.SendMessageAsync(new ExecutionResult(result), cancellationToken);
    }

    private static string DecisionText(StaffDecisionKind kind) => kind switch
    {
        StaffDecisionKind.Approve => "核准",
        StaffDecisionKind.Reject => "駁回",
        _ => "要求補件",
    };
}

/// <summary>
/// 通知：顧客通知已由 <see cref="AfterSalesDecisionService.ApplyAsync"/> 在 commit 後送出，這裡只標記節點完成並結束這一輪。
/// </summary>
public sealed class NotifyExecutor(PreReviewNodeContext nodes)
    : PreReviewExecutor<ExecutionResult>(PreReviewWorkflowFactory.NotifyId, PreReviewNode.Notify, nodes)
{
    protected override async ValueTask RunNodeAsync(ExecutionResult message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        await Nodes.Runs.SetStatusAsync(Nodes.RunId, PreReviewStatus.Completed);
        await MarkAsync(NodeStatus.Done, "已通知顧客");
    }
}
