using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Web.Agents.StaffAssistant;

/// <summary>確認卡在交接摘要裡的樣子（內容取自程式重查組出的確認卡，事實句已遮蔽個資）。</summary>
public sealed record HandoverConfirmation(
    string Title,
    string ProductLine,
    string Spec,
    string PolicyLine,
    IReadOnlyList<string> Facts,
    int PhotoCount,
    string Outcome);

/// <summary>
/// 轉人工交接：顧客端 Agent 在這個案件查過什麼（純程式整理，不呼叫模型）。
/// ⚠️ 不含個資：沒有姓名、帳號、Email、地址、手機、使用者 Id；顧客說的話與確認卡的事實句經 <see cref="PreReviewPrivacy"/> 遮蔽。
/// </summary>
public sealed record HandoverSummary(
    bool HasTrail,
    int OrderLookups,
    int OrderItemsFound,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> StockChecks,
    IReadOnlyList<string> CustomerStatements,
    HandoverConfirmation? Confirmation,
    int PhotosUploaded);

/// <summary>顧客端 Agent 軌跡的一步（給客服 AI 助手的 <c>get_customer_agent_trail</c>；文字已遮蔽個資）。</summary>
public sealed record StaffTrailStep(DateTime OccurredAt, string Kind, string Text);

/// <summary>
/// 從顧客端 Agent 的執行軌跡（<see cref="AgentTraceService.GetSessionStepsAsync"/>，CaseKey＝顧客會話的 SessionKey）整理交接摘要：
/// 查過的訂單筆數、套用的規則與版本、查過的庫存、顧客說過的話、確認卡內容、是否上傳照片。
/// 工具名稱取自 <see cref="AgentStepKind.ToolRequest"/> 的 RawJson（<c>{"tool":…}</c>），結果取自緊接的
/// <see cref="AgentStepKind.ToolResult"/>（<see cref="ToolOutcome"/> 信封）；讀不懂的步驟略過，不讓摘要失敗。
/// 只讀：不寫任何軌跡（寫在顧客的 SessionKey 底下會出現在顧客還原的對話裡）。
/// </summary>
public sealed partial class HandoverSummaryBuilder
{
    /// <summary>顧客說過的話最多列幾句（取最近的）。</summary>
    internal const int MaxStatements = 5;

    internal const int MaxStatementLength = 120;

    /// <summary>給模型的軌跡最多幾步（取最近的），每步最多幾個字。</summary>
    internal const int MaxTrailSteps = 60;

    internal const int MaxTrailTextLength = 500;

    private readonly AgentTraceService traces;
    private readonly AfterSalesRequestService requests;

    public HandoverSummaryBuilder(AgentTraceService traces, AfterSalesRequestService requests)
    {
        this.traces = traces;
        this.requests = requests;
    }

    public async Task<HandoverSummary> BuildAsync(AfterSalesRequestView request)
        => (await BuildWithTrailAsync(request)).Summary;

    /// <summary>交接摘要＋可讀的軌跡步驟（客服 AI 助手的 <c>get_customer_agent_trail</c>）。</summary>
    public async Task<(HandoverSummary Summary, IReadOnlyList<StaffTrailStep> Steps)> BuildWithTrailAsync(AfterSalesRequestView request)
    {
        var steps = await traces.GetSessionStepsAsync(request.CaseKey, request.CustomerUserId);
        var (names, others) = await requests.GetPersonalValuesForRedactionAsync(request.Id);
        string Redact(string text) => PreReviewPrivacy.Redact(text, names, others);
        return (Summarize(steps, Redact), ToTrail(steps, Redact));
    }

    private static HandoverSummary Summarize(IReadOnlyList<AgentRunStepView> steps, Func<string, string> redact)
    {
        var orderLookups = 0;
        var orderItems = new HashSet<int>();
        var policies = new List<string>();
        var stocks = new List<string>();
        var statements = new List<string>();
        var photos = 0;
        HandoverConfirmation? confirmation = null;
        var lastCardIsMemory = false;
        string? pendingTool = null;

        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case AgentStepKind.UserMessage when PhotosUploadedPattern().Match(step.PlainText) is { Success: true } match:
                    photos += int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
                    break;
                case AgentStepKind.UserMessage:
                    statements.Add(Shorten(redact(step.PlainText), MaxStatementLength));
                    break;
                case AgentStepKind.ToolRequest:
                    pendingTool = ToolNameOf(step.RawJson);
                    if (pendingTool == AfterSalesAgentTools.QueryMyOrdersName)
                    {
                        orderLookups++;
                    }

                    break;
                case AgentStepKind.ToolResult:
                    if (pendingTool is not null && ToolOutcome.TryParse(step.RawJson, out var outcome))
                    {
                        Collect(pendingTool, outcome, orderItems, policies, stocks);
                    }

                    pendingTool = null;
                    break;
                case AgentStepKind.ConfirmationShown when TryReadCard(step.RawJson) is { Kind: ConfirmationCardKind.Memory }:
                    // 記憶確認卡（M5）不是申請：不列入交接；它之後的「顧客決定」也屬於這張記憶卡，不可蓋掉申請卡的結果。
                    lastCardIsMemory = true;
                    break;
                case AgentStepKind.ConfirmationShown when TryReadCard(step.RawJson) is { } card:
                    lastCardIsMemory = false;
                    confirmation = new HandoverConfirmation(
                        card.Title,
                        card.ProductLine,
                        card.ToSpec is null ? $"{card.FromSpec}（{card.IssueLabel}）" : $"{card.FromSpec} → {card.ToSpec}（{card.IssueLabel}）",
                        card.PolicyLine,
                        card.Facts.Select(redact).ToList(),
                        card.PhotoCount,
                        "顧客尚未回應");
                    break;
                case AgentStepKind.ConfirmationDecided when confirmation is not null && !lastCardIsMemory:
                    confirmation = confirmation with { Outcome = DecisionOf(step.RawJson) };
                    break;
            }
        }

        return new HandoverSummary(
            steps.Count > 0,
            orderLookups,
            orderItems.Count,
            policies.Distinct(StringComparer.Ordinal).ToList(),
            stocks.Distinct(StringComparer.Ordinal).ToList(),
            statements.TakeLast(MaxStatements).ToList(),
            confirmation,
            photos);
    }

    private static void Collect(string tool, ToolOutcome outcome, HashSet<int> orderItems, List<string> policies, List<string> stocks)
    {
        var data = outcome.Data as JsonElement?;
        switch (tool)
        {
            case AfterSalesAgentTools.QueryMyOrdersName when outcome.Status == ToolOutcomeStatus.Ok && data is { ValueKind: JsonValueKind.Array } items:
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("order_item_id", out var id) && id.TryGetInt32(out var value))
                    {
                        orderItems.Add(value);
                    }
                }

                break;
            case AfterSalesAgentTools.GetEffectivePolicyName when outcome.Status == ToolOutcomeStatus.Ok && data is { ValueKind: JsonValueKind.Object } card:
                policies.Add($"{Text(card, "title")}（第 {Number(card, "version")} 版）");
                break;
            case AfterSalesAgentTools.GetEffectivePolicyName:
                policies.Add($"規則查詢：{AfterSalesAgentGuard.StatusText(outcome.Status)}");
                break;
            case AfterSalesAgentTools.CheckVariantStockName when outcome.Status == ToolOutcomeStatus.Ok && data is { ValueKind: JsonValueKind.Array } options:
                var specs = options.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Object)
                    .Select(x => (Spec: $"{Text(x, "color")} / {Text(x, "size")}",
                        InStock: x.TryGetProperty("in_stock", out var flag) && flag.ValueKind == JsonValueKind.True))
                    .ToList();
                var inStock = specs.Where(x => x.InStock).Select(x => x.Spec).ToList();
                var outOfStock = specs.Where(x => !x.InStock).Select(x => x.Spec).ToList();
                var parts = new List<string>();
                if (inStock.Count > 0)
                {
                    parts.Add("有貨：" + string.Join("、", inStock));
                }

                if (outOfStock.Count > 0)
                {
                    parts.Add("無貨：" + string.Join("、", outOfStock));
                }

                if (parts.Count > 0)
                {
                    stocks.Add(string.Join("；", parts));
                }

                break;
        }
    }

    private static List<StaffTrailStep> ToTrail(IReadOnlyList<AgentRunStepView> steps, Func<string, string> redact)
        => steps
            .Select(x => (Step: x, Kind: TrailKindText(x.Kind)))
            .Where(x => x.Kind is not null)
            .TakeLast(MaxTrailSteps)
            .Select(x => new StaffTrailStep(x.Step.OccurredAt, x.Kind!, Shorten(redact(x.Step.PlainText), MaxTrailTextLength)))
            .ToList();

    /// <summary>給模型看的步驟種類；身分核對、模型請求摘要、停止與收尾等程式內部步驟不列（null）。</summary>
    private static string? TrailKindText(AgentStepKind kind) => kind switch
    {
        AgentStepKind.UserMessage => "顧客",
        AgentStepKind.ToolRequest => "Agent 查詢",
        AgentStepKind.ToolResult => "查詢結果",
        AgentStepKind.ModelReply => "Agent 回覆",
        AgentStepKind.ConfirmationShown => "確認卡",
        AgentStepKind.ConfirmationDecided => "顧客決定",
        AgentStepKind.Warning => "程式提醒",
        _ => null,
    };

    private static string? ToolNameOf(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.String
                ? tool.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConfirmationCard? TryReadCard(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return ConfirmationCard.FromJson(rawJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>確認卡的結果：RawJson 帶 <c>approved</c> 的是顧客按下的選擇；沒有的是顧客改以文字回覆而作廢。</summary>
    private static string DecisionOf(string? rawJson) => ApprovedOf(rawJson) switch
    {
        true => "顧客按下確認送出",
        false => "顧客按下取消",
        null => "顧客改以文字回覆，確認卡作廢",
    };

    /// <summary>RawJson 的 <c>approved</c>；沒有或讀不懂回 null（當作作廢）。</summary>
    private static bool? ApprovedOf(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("approved", out var approved)
                && approved.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? approved.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "?";

    /// <summary>顧客上傳照片後續跑時代送的訊息（<see cref="AfterSalesAgentRunner.PhotosUploadedMessageFormat"/>）。</summary>
    [GeneratedRegex(@"^（已上傳 (?<count>\d+) 張照片）$")]
    private static partial Regex PhotosUploadedPattern();
}
