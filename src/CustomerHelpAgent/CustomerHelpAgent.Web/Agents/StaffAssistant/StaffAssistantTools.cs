using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Web.Agents.StaffAssistant;

/// <summary>客服 AI 助手的工具環境：綁定的申請與開啟助手的客服（由 <see cref="StaffAssistantRunner.StartSession"/> 決定，模型改不了）。</summary>
public sealed class StaffAssistantToolContext
{
    /// <summary>目前開啟的那一筆申請；所有工具只查它（與它的顧客）。</summary>
    public required int RequestId { get; init; }

    /// <summary>開啟助手的客服；工具執行時登入使用者必須仍是他，且仍有「售後審核」檢視權限。</summary>
    public required int StaffUserId { get; init; }

    /// <summary>每次工具呼叫（含權限檢查）的逾時；Runner 傳入 <c>AfterSalesAgentSettings.ToolTimeoutSeconds</c>。</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 客服 AI 助手的 6 個只讀工具。安全邊界：
/// <list type="bullet">
/// <item><b>案件由程式綁定</b>：工具<b>沒有任何參數</b>，一律查 <see cref="StaffAssistantToolContext.RequestId"/> 那一筆與它的顧客；
/// 模型多傳的引數（申請編號、顧客 Id…）會被 <see cref="AIFunctionFactory"/> 忽略。</item>
/// <item><b>只讀</b>：只呼叫查詢方法，沒有寫入工具，也不需要確認關卡。</item>
/// <item><b>權限每次重查</b>：登入使用者須仍是開啟助手的客服，且 <see cref="IPermissionChecker"/> 即時確認仍有「售後審核」檢視權限，否則 <c>forbidden</c>。</item>
/// <item><b>不含個資</b>：回傳一律是 <see cref="ToolOutcome"/> 信封，只含投影欄位（沒有姓名、帳號、Email、收件人、地址、手機、使用者 Id，
/// 名稱列在 <c>withheld_fields</c>）；顧客與客服寫的自由文字經 <see cref="PreReviewPrivacy"/> 遮蔽。</item>
/// </list>
/// 外框（逾時 → 權限 → 本體、例外 → <c>unavailable</c>）與顧客端 Agent 的工具共用 <see cref="AfterSalesAgentTools.RunFramedAsync"/>。
/// </summary>
public sealed class StaffAssistantTools
{
    public const string GetCaseName = "get_case";
    public const string GetEffectivePolicyName = "get_effective_policy";
    public const string CheckStockName = "check_stock";
    public const string GetCustomerHistoryName = "get_customer_history";
    public const string GetCustomerAgentTrailName = "get_customer_agent_trail";
    public const string GetPreReviewResultName = "get_pre_review_result";

    /// <summary>刻意不送給模型的顧客資料（只有名稱）。</summary>
    private static readonly string[] CustomerWithheld = ["顧客姓名", "帳號", "Email", "收件人", "地址", "手機", "使用者 Id"];

    private readonly AfterSalesRequestService requests;
    private readonly AfterSalesPolicyService policies;
    private readonly ShopOrderService orders;
    private readonly PreReviewRunService preReviews;
    private readonly HandoverSummaryBuilder handover;
    private readonly IPermissionChecker permissionChecker;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<StaffAssistantTools> logger;
    private readonly StaffAssistantToolContext context;

    public StaffAssistantTools(
        AfterSalesRequestService requests,
        AfterSalesPolicyService policies,
        ShopOrderService orders,
        PreReviewRunService preReviews,
        HandoverSummaryBuilder handover,
        IPermissionChecker permissionChecker,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<StaffAssistantTools> logger,
        StaffAssistantToolContext context)
    {
        this.requests = requests;
        this.policies = policies;
        this.orders = orders;
        this.preReviews = preReviews;
        this.handover = handover;
        this.permissionChecker = permissionChecker;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.context = context;
    }

    public IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AfterSalesAgentTools.Create(GetCaseAsync, GetCaseName,
            "取得目前開啟的這一筆售後申請內容（類型、狀態、商品與規格、目標規格、是否穿過、顧客說明、簽收天數、送件時套用的規則版本、照片張數）。不提供顧客個資。"),
        AfterSalesAgentTools.Create(GetEffectivePolicyAsync, GetEffectivePolicyName,
            "取得這筆申請目前生效的售後規則卡，並由程式依申請內容計算簽收天數、不符原因與尚缺資訊。"),
        AfterSalesAgentTools.Create(CheckStockAsync, CheckStockName,
            "查詢這筆申請商品同款的各顏色／尺碼是否有貨（只回有無，不回數量），並標出換貨目標規格是否有貨。"),
        AfterSalesAgentTools.Create(GetCustomerHistoryAsync, GetCustomerHistoryName,
            "取得此顧客過去的售後申請（不含這一筆）：類型、商品、狀態、送件時間。"),
        AfterSalesAgentTools.Create(GetCustomerAgentTrailAsync, GetCustomerAgentTrailName,
            "取得顧客端 Agent 在這個案件的交接摘要（查過的訂單、規則、庫存、確認卡、照片）與對話紀錄（顧客說的話、查詢與回覆）。個資已遮蔽。"),
        AfterSalesAgentTools.Create(GetPreReviewResultAsync, GetPreReviewResultName,
            "取得這筆申請最新一輪 AI 初審的結論：建議、理由、客服檢查清單，以及照片初審、規則核對、歷史紀錄三項結論。"),
    ];

    public Task<ToolOutcome> GetCaseAsync(CancellationToken cancellationToken = default)
        => RunAsync(GetCaseName, async (detail, ct) =>
        {
            var applied = detail.AppliedPolicyId is { } policyId
                ? await AfterSalesAgentTools.ObserveFault(policies.GetByIdAsync(policyId)).WaitAsync(ct)
                : null;
            var redact = await RedactorAsync(ct);
            return ToolOutcome.Ok(new StaffCaseFacts(
                detail.RequestNo,
                detail.OrderNo,
                AfterSalesLabels.TypeText(detail.Type),
                AfterSalesLabels.StatusText(detail.Status),
                AfterSalesLabels.ChannelText(detail.Channel),
                detail.ProductName,
                detail.ProductCategory,
                detail.Color,
                detail.Size,
                detail.TargetColor,
                detail.TargetSize,
                detail.IsWorn,
                redact(detail.Description),
                string.IsNullOrWhiteSpace(detail.StaffNote) ? null : redact(detail.StaffNote),
                detail.DeliveredAt,
                AfterSalesEligibility.DaysSinceDelivery(detail.DeliveredAt, Now()),
                detail.CreatedAt,
                detail.Attachments.Count,
                applied is null ? null : $"{applied.Title}（第 {applied.Version} 版）"), CustomerWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> GetEffectivePolicyAsync(CancellationToken cancellationToken = default)
        => RunAsync(GetEffectivePolicyName, async (detail, ct) =>
        {
            var now = Now();
            var (policy, failure) = await AfterSalesAgentTools.LookupPolicyAsync(policies, detail.Type, detail.ProductCategory, now, ct);
            if (policy is null)
            {
                return failure!;
            }

            var shippedAt = detail.Type == AfterSalesType.ShipmentUrge
                ? (await AfterSalesAgentTools.ObserveFault(orders.GetMyOrderDetailAsync(detail.CustomerUserId, detail.SalesOrderId)).WaitAsync(ct))?.Shipment?.ShippedAt
                : null;
            var eligibility = AfterSalesEligibility.Evaluate(
                policy, detail.DeliveredAt, detail.IsWorn, hasPhoto: detail.Attachments.Count > 0, now, shippedAt);
            return ToolOutcome.Ok(AfterSalesAgentTools.ToPolicyCard(policy, detail.DeliveredAt, eligibility, now));
        }, cancellationToken);

    public Task<ToolOutcome> CheckStockAsync(CancellationToken cancellationToken = default)
        => RunAsync(CheckStockName, async (detail, ct) =>
        {
            var variants = await AfterSalesAgentTools.ObserveFault(orders.GetVariantStockAsync(detail.CustomerUserId, detail.OrderItemId)).WaitAsync(ct);
            if (variants.Count == 0)
            {
                return ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "查無這筆申請商品的規格資料。");
            }

            var options = AfterSalesAgentTools.ToStockOptions(variants);
            var (targetColor, targetSize) = detail.Type switch
            {
                AfterSalesType.SizeExchange => (detail.Color, detail.TargetSize),
                AfterSalesType.ColorExchange => (detail.TargetColor, detail.Size),
                _ => (null, null),
            };
            bool? targetInStock = targetColor is null || targetSize is null
                ? null
                : options.Any(x => x.Color == targetColor && x.Size == targetSize && x.InStock);
            return ToolOutcome.Ok(new StaffStockResult(
                $"{detail.Color} / {detail.Size}",
                targetInStock is null ? null : $"{targetColor} / {targetSize}",
                targetInStock,
                options), AfterSalesAgentTools.StockWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> GetCustomerHistoryAsync(CancellationToken cancellationToken = default)
        => RunAsync(GetCustomerHistoryName, async (detail, ct) =>
        {
            var mine = await AfterSalesAgentTools.ObserveFault(requests.GetMyRequestsAsync(detail.CustomerUserId)).WaitAsync(ct);
            var prior = mine
                .Where(x => x.Id != detail.Id)
                .Select(x => new StaffHistoryItem(
                    x.RequestNo,
                    x.CreatedAt,
                    AfterSalesLabels.TypeText(x.Type),
                    x.ProductName,
                    x.ProductCategory,
                    AfterSalesLabels.StatusText(x.Status),
                    AfterSalesLabels.ChannelText(x.Channel)))
                .ToList();
            return ToolOutcome.Ok(new { PriorCount = prior.Count, Requests = prior }, CustomerWithheld);
        }, cancellationToken);

    public Task<ToolOutcome> GetCustomerAgentTrailAsync(CancellationToken cancellationToken = default)
        => RunAsync(GetCustomerAgentTrailName, async (detail, ct) =>
        {
            var (summary, steps) = await AfterSalesAgentTools.ObserveFault(handover.BuildWithTrailAsync(detail)).WaitAsync(ct);
            return summary.HasTrail
                ? ToolOutcome.Ok(new { Handover = summary, Steps = steps }, CustomerWithheld)
                : ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "顧客端 Agent 沒有這個案件的對話紀錄。");
        }, cancellationToken);

    public Task<ToolOutcome> GetPreReviewResultAsync(CancellationToken cancellationToken = default)
        => RunAsync(GetPreReviewResultName, async (detail, ct) =>
        {
            var run = await AfterSalesAgentTools.ObserveFault(preReviews.GetLatestForRequestAsync(detail.Id)).WaitAsync(ct);
            if (run is null)
            {
                return ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "這筆申請還沒有 AI 初審紀錄。");
            }

            var photo = PreReviewFindingReader.ParseFinding(run.PhotoJson);
            var rule = PreReviewFindingReader.ParseFinding(run.RuleJson);
            var history = PreReviewFindingReader.ParseFinding(run.HistoryJson);
            var summary = PreReviewFindingReader.ParseDetail(
                PreReviewFindingReader.ParseFinding(run.SummaryJson), PreReviewJsonContext.Default.PreReviewSummary);
            var ruleDetail = PreReviewFindingReader.ParseDetail(rule, PreReviewJsonContext.Default.RuleCheckResult);
            var historyDetail = PreReviewFindingReader.ParseDetail(history, PreReviewJsonContext.Default.HistoryReviewResult);
            var photoDetail = PreReviewFindingReader.ParseDetail(photo, PreReviewJsonContext.Default.PhotoReviewResult);

            // ⚠️ 初審結論是模型寫的自由文字（標題、理由、觀察、顧客陳述…），可能轉述顧客打出的個資：每個字串欄位都要遮蔽，
            // 與摘要節點讀這些結論前的處理相同。
            var redact = await RedactorAsync(ct);
            IReadOnlyList<string> All(IReadOnlyList<string>? items) => items is null ? [] : [.. items.Select(redact)];
            string? One(string? text) => text is null ? null : redact(text);
            return ToolOutcome.Ok(new StaffPreReviewResult(
                run.Attempt,
                PreReviewLabels.QueueTagText(run.Status, run.Recommendation),
                summary is null ? null : PreReviewLabels.RecommendationText(summary.Recommendation),
                One(summary?.Headline),
                All(summary?.Reasons),
                All(summary?.StaffChecklist),
                One(summary?.Warning),
                [.. new[] { photo, rule, history }.Where(x => x is not null).Select(x => new StaffFinding(
                    PreReviewLabels.NodeText(x!.Node), x.Status.ToString(), redact(x.Headline)))],
                ruleDetail is null ? null : ruleDetail with
                {
                    PolicyTitle = One(ruleDetail.PolicyTitle),
                    UnmetReasons = All(ruleDetail.UnmetReasons),
                    MissingInfo = All(ruleDetail.MissingInfo),
                },
                historyDetail is null ? null : historyDetail with
                {
                    Notes = All(historyDetail.Notes),
                    CustomerStatements = All(historyDetail.CustomerStatements),
                },
                photoDetail is null ? null : photoDetail with
                {
                    Verdict = redact(photoDetail.Verdict),
                    Observations = All(photoDetail.Observations),
                    Confidence = redact(photoDetail.Confidence),
                }));
        }, cancellationToken);

    /// <summary>回覆上方的小標籤（「查了：規則 v1」）：工具名稱＋結果的白話；不是 ok 時附上狀態。</summary>
    public static string DescribeCall(string toolName, ToolOutcome outcome)
    {
        // 中介層拿到的是序列化後的信封（Data 為 JsonElement）；直接呼叫工具方法時 Data 是規則卡本身。
        var policyVersion = outcome.Data switch
        {
            AgentPolicyCard card => card.Version.ToString(CultureInfo.InvariantCulture),
            JsonElement { ValueKind: JsonValueKind.Object } card when card.TryGetProperty("version", out var version) => version.GetRawText(),
            _ => null,
        };
        var label = toolName switch
        {
            GetCaseName => "申請內容",
            GetEffectivePolicyName when outcome.Status == ToolOutcomeStatus.Ok && policyVersion is not null => $"規則 v{policyVersion}",
            GetEffectivePolicyName => "規則",
            CheckStockName => "庫存",
            GetCustomerHistoryName => "過往申請",
            GetCustomerAgentTrailName => "顧客端對話",
            GetPreReviewResultName => "AI 初審",
            _ => toolName,
        };
        return outcome.Status switch
        {
            ToolOutcomeStatus.Ok => $"查了：{label}",
            // 客服端的說法（顧客端 Agent 的幕後面板仍用「身分不符」）。
            ToolOutcomeStatus.Forbidden => $"查了：{label}（目前沒有審核權限）",
            _ => $"查了：{label}（{AfterSalesAgentGuard.StatusText(outcome.Status)}）",
        };
    }

    private DateTime Now() => timeProvider.GetLocalNow().DateTime;

    /// <summary>外框（逾時 → 客服身分與權限 → 本體），本體拿到綁定那一筆的申請明細。</summary>
    private Task<ToolOutcome> RunAsync(
        string toolName, Func<AfterSalesRequestView, CancellationToken, Task<ToolOutcome>> body, CancellationToken cancellationToken)
        => AfterSalesAgentTools.RunFramedAsync(
            toolName,
            context.ToolTimeout,
            () => ResolveStaffAsync(toolName),
            beforeServiceCall: null,
            AgentToolOwner.StaffAssistant,
            async (_, ct) =>
            {
                var detail = await AfterSalesAgentTools.ObserveFault(requests.GetReviewDetailAsync(context.RequestId)).WaitAsync(ct);
                if (detail is null)
                {
                    logger.LogInformation("Staff assistant tool found no request. ToolName={ToolName}, RequestId={RequestId}", toolName, context.RequestId);
                    return ToolOutcome.Fail(ToolOutcomeStatus.NotFound, "找不到這筆申請（可能已被刪除）。");
                }

                return await body(detail, ct);
            },
            logger,
            cancellationToken);

    /// <summary>登入使用者仍是開啟助手的客服、且仍有「售後審核」檢視權限時回他的 Id；否則 null（→ forbidden）。</summary>
    private async Task<int?> ResolveStaffAsync(string toolName)
    {
        var userId = currentUserService.CurrentUser.Id;
        if (userId == 0 || userId != context.StaffUserId)
        {
            logger.LogWarning(
                "Staff assistant tool rejected because the signed-in user did not open the assistant. ToolName={ToolName}, UserId={UserId}",
                toolName, userId);
            return null;
        }

        // 「頁面:view」動作鍵：裸頁面鍵（舊制＝全動作）也會通過，與 CheckAccessPage 的規則一致；即時查資料庫，對話中途收回權限立即生效。
        if (!await permissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_售後審核, PermissionActions.View)))
        {
            logger.LogWarning(
                "Staff assistant tool rejected because the user lacks the review permission. ToolName={ToolName}, UserId={UserId}",
                toolName, userId);
            return null;
        }

        return userId;
    }

    /// <summary>這筆申請的個資原文只在程式內比對（<see cref="PreReviewPrivacy"/>），絕不送給模型。</summary>
    private async Task<Func<string, string>> RedactorAsync(CancellationToken ct)
    {
        var (names, others) = await AfterSalesAgentTools.ObserveFault(requests.GetPersonalValuesForRedactionAsync(context.RequestId)).WaitAsync(ct);
        return text => PreReviewPrivacy.Redact(text, names, others);
    }
}

// 給模型看的投影：沒有姓名、帳號、Email、收件人、地址、手機、使用者 Id。

/// <summary>申請內容（<c>get_case</c>）。</summary>
public sealed record StaffCaseFacts(
    string RequestNo,
    string OrderNo,
    string Type,
    string Status,
    string Channel,
    string ProductName,
    string ProductCategory,
    string Color,
    string Size,
    string? TargetColor,
    string? TargetSize,
    bool? IsWorn,
    string Description,
    string? StaffNote,
    DateTime? DeliveredAt,
    int? DaysSinceDelivery,
    DateTime SubmittedAt,
    int PhotoCount,
    string? AppliedPolicyAtSubmission);

/// <summary>庫存（<c>check_stock</c>）：目前規格、換貨目標規格與它是否有貨、同款各規格有無。</summary>
public sealed record StaffStockResult(string CurrentSpec, string? TargetSpec, bool? TargetInStock, IReadOnlyList<AgentStockOption> Options);

/// <summary>此顧客過去的一筆申請（<c>get_customer_history</c>）。</summary>
public sealed record StaffHistoryItem(
    string RequestNo,
    DateTime SubmittedAt,
    string Type,
    string ProductName,
    string ProductCategory,
    string Status,
    string Channel);

/// <summary>一項初審結論的標題。</summary>
public sealed record StaffFinding(string Node, string Status, string Headline);

/// <summary>最新一輪 AI 初審（<c>get_pre_review_result</c>）。</summary>
public sealed record StaffPreReviewResult(
    int Attempt,
    string Status,
    string? Recommendation,
    string? Headline,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> StaffChecklist,
    string? Warning,
    IReadOnlyList<StaffFinding> Findings,
    RuleCheckResult? RuleCheck,
    HistoryReviewResult? History,
    PhotoReviewResult? Photo);
