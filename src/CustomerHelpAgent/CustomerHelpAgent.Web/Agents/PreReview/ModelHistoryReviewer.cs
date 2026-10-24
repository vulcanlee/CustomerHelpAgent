using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>歷史紀錄初審的結構化輸出（也是 <see cref="PreReviewFinding.Json"/> 的明細）。</summary>
public sealed record HistoryReviewResult(int PriorRequestCount, IReadOnlyList<string> Notes, IReadOnlyList<string> CustomerStatements);

/// <summary>
/// 歷史紀錄初審（模型節點）：此顧客過去的售後申請（類型、商品類別、結果、日期，不含這次），
/// 加上這個案件顧客端 Agent 對話軌跡中 <see cref="AgentStepKind.UserMessage"/>／<see cref="AgentStepKind.ToolResult"/> 的白話文字
/// （只到這一輪初審建立為止，見 <see cref="TraceUpTo"/>），
/// 以結構化輸出取得 <see cref="HistoryReviewResult"/>。
///
/// ⚠️ 個資：申請與軌跡的白話文字本來就不含收件人、地址、手機（工具結果刻意不送這些欄位），
/// 但顧客自己打的字可能提到：送出前整段經 <see cref="PreReviewPrivacy"/> 遮蔽。
/// 過往申請筆數以程式查到的為準（不採用模型的數字）。
/// </summary>
public sealed class ModelHistoryReviewer : IHistoryReviewer
{
    /// <summary>軌跡只取最近這麼多步（對話很長時控制提示詞長度）。</summary>
    internal const int MaxTraceSteps = 40;

    private readonly PreReviewChatClientProvider models;
    private readonly AfterSalesRequestService requests;
    private readonly AgentTraceService traces;
    private readonly PreReviewRunService runs;

    public ModelHistoryReviewer(PreReviewChatClientProvider models, AfterSalesRequestService requests, AgentTraceService traces, PreReviewRunService runs)
    {
        this.models = models;
        this.requests = requests;
        this.traces = traces;
        this.runs = runs;
    }

    public async Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
    {
        var detail = await requests.GetReviewDetailAsync(reviewCase.RequestId)
            ?? throw new InvalidOperationException("After-sales request not found.");
        var prior = (await requests.GetMyRequestsAsync(detail.CustomerUserId))
            .Where(x => x.Id != reviewCase.RequestId)
            .ToList();
        var cutOff = (await runs.GetAsync(reviewCase.RunId))?.CreatedAt
            ?? throw new InvalidOperationException("Pre-review run not found.");
        var steps = TraceUpTo(await traces.GetSessionStepsAsync(reviewCase.CaseKey, detail.CustomerUserId), cutOff)
            .Where(x => x.Kind is AgentStepKind.UserMessage or AgentStepKind.ToolResult)
            .TakeLast(MaxTraceSteps)
            .ToList();

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"這次申請：{AfterSalesLabels.TypeText(reviewCase.Type)}（{reviewCase.ProductCategory}）\n\n");
        text.Append(CultureInfo.InvariantCulture, $"此顧客過去的售後申請（{prior.Count} 筆，不含這次）：\n");
        if (prior.Count == 0)
        {
            text.Append("（沒有）\n");
        }

        foreach (var request in prior)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"- {request.CreatedAt:yyyy/MM/dd} {AfterSalesLabels.TypeText(request.Type)}（{request.ProductCategory}）：{AfterSalesLabels.StatusText(request.Status)}\n");
        }

        text.Append("\n這次線上客服對話的紀錄（依時間順序）：\n");
        if (steps.Count == 0)
        {
            text.Append("（沒有線上客服對話紀錄）\n");
        }

        foreach (var step in steps)
        {
            text.Append(step.Kind == AgentStepKind.UserMessage ? "- 顧客：" : "- 查詢結果：").Append(step.PlainText).Append('\n');
        }

        var output = await models.RunStructuredAsync<HistoryReviewResult>(
            reviewCase,
            AgentRecordingStreams.PreReviewHistory,
            vision: false,
            PromptTemplateKeys.AfterSalesPreReviewHistory,
            new ChatMessage(ChatRole.User, await PreReviewPrivacy.RedactAsync(requests, reviewCase.RequestId, text.ToString())),
            cancellationToken);
        var result = new HistoryReviewResult(prior.Count, output.Notes ?? [], output.CustomerStatements ?? []);
        var headline = prior.Count == 0 ? "此顧客沒有過往售後申請" : $"此顧客過往售後申請 {prior.Count} 筆";
        if (result.Notes.Count > 0)
        {
            headline += $"，{result.Notes.Count} 項注意事項";
        }

        return new PreReviewFinding(
            PreReviewNode.HistoryReview,
            FindingStatus.Ok,
            headline,
            JsonSerializer.Serialize(result, PreReviewJsonContext.Default.HistoryReviewResult));
    }

    /// <summary>
    /// 只留這一輪初審建立當下（<paramref name="cutOff"/>＝<c>PreReviewRun.CreatedAt</c>，含）以前的步驟，並拿掉「建立申請」這個工具自己的結果。
    /// ⚠️ 送件掛勾在 <c>create_after_sales_request</c> 工具內（commit 之後）就建立這一輪並入列，而守門員要等工具回來才寫它的結果步驟，
    /// 顧客之後也可能繼續聊：不切掉的話，提示詞（與回放比對用的請求摘要）會隨背景執行的時機不同而不同。
    /// 顧客長期記憶工具（M5，<c>remember_preference</c>）的結果也拿掉：記憶只是偏好，不是業務事實，不能當作歷史證據。
    /// 工具結果步驟本身沒有工具名稱，以「緊接在這兩個工具的請求步驟之後的第一個結果」認定（工具一次只跑一個）。
    /// </summary>
    internal static IEnumerable<AgentRunStepView> TraceUpTo(IEnumerable<AgentRunStepView> steps, DateTime cutOff)
    {
        var skipResult = false;
        foreach (var step in steps.Where(x => x.OccurredAt <= cutOff))
        {
            if (step.Kind == AgentStepKind.ToolRequest)
            {
                skipResult = IsExcludedToolRequest(step.RawJson);
            }
            else if (step.Kind == AgentStepKind.ToolResult && skipResult)
            {
                skipResult = false;
                continue;
            }

            yield return step;
        }
    }

    /// <summary>建立申請或記憶工具的請求步驟（它們的結果不送歷史初審）。</summary>
    private static bool IsExcludedToolRequest(string? rawJson)
    {
        if (string.IsNullOrEmpty(rawJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.String
                && tool.GetString() is AfterSalesAgentTools.CreateAfterSalesRequestName or AfterSalesAgentTools.RememberPreferenceName;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
