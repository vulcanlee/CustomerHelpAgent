using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 摘要模型的結構化輸出。刻意不用 <see cref="PreReviewSummary"/>：它的 <see cref="PreReviewSummary.Warning"/> 只能由程式設定，
/// 不可出現在送給模型的 JSON schema 裡。
/// </summary>
public sealed record PreReviewSummaryOutput(
    string Recommendation,
    string Headline,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> StaffChecklist);

/// <summary>
/// 摘要（模型節點）：讀三個初審結論（JSON），以結構化輸出取得建議。
/// 建議值不在允許清單內、建議核准但規則核對不符資格或規則核對未完成時，由 <see cref="SummaryExecutor"/> 改為需人工判斷並標警示；
/// 回應不是合法的 JSON 時這裡拋例外，executor 改用程式摘要。
/// </summary>
public sealed class ModelPreReviewSummarizer : IPreReviewSummarizer
{
    /// <summary>送給模型的結論 JSON：中文不跳脫（省 token、模型讀得懂）。</summary>
    private static readonly JsonSerializerOptions PromptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PreReviewChatClientProvider models;
    private readonly AfterSalesRequestService requests;

    public ModelPreReviewSummarizer(PreReviewChatClientProvider models, AfterSalesRequestService requests)
    {
        this.models = models;
        this.requests = requests;
    }

    public async Task<PreReviewSummary> SummarizeAsync(
        PreReviewCase reviewCase, IReadOnlyList<PreReviewFinding> findings, CancellationToken cancellationToken)
    {
        var input = findings.Select(x => new
        {
            node = NodeKey(x.Node),
            status = x.Status.ToString(),
            headline = x.Headline,
            detail = Detail(x),
        });
        // 結論裡有模型轉述的顧客說法（觀察、顧客陳述），同樣經過遮蔽。
        var text = await PreReviewPrivacy.RedactAsync(requests, reviewCase.RequestId, $"""
            售後類型：{AfterSalesLabels.TypeText(reviewCase.Type)}
            三項初審結論（JSON）：
            {JsonSerializer.Serialize(input, PromptJsonOptions)}
            """);

        var output = await models.RunStructuredAsync<PreReviewSummaryOutput>(
            reviewCase,
            AgentRecordingStreams.PreReviewSummary,
            vision: false,
            PromptTemplateKeys.AfterSalesPreReviewSummary,
            new ChatMessage(ChatRole.User, text),
            cancellationToken);
        return new PreReviewSummary(output.Recommendation, output.Headline, output.Reasons, output.StaffChecklist);
    }

    /// <summary>
    /// 節點明細以物件嵌入（不是跳脫過的字串）。規則核對拿掉規則 Id：對模型沒有意義，而且重置示範資料後會變，
    /// 留著會讓回放時的請求摘要（<see cref="AfterSales.ModelRequestDigest"/>）對不上。
    /// </summary>
    private static JsonElement Detail(PreReviewFinding finding)
    {
        if (finding is { Node: PreReviewNode.RuleCheck, Status: FindingStatus.Ok }
            && JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.RuleCheckResult) is { } rule)
        {
            return JsonSerializer.SerializeToElement(rule with { PolicyId = null }, PreReviewJsonContext.Default.RuleCheckResult);
        }

        return JsonSerializer.Deserialize<JsonElement>(finding.Json);
    }

    private static string NodeKey(PreReviewNode node) => node switch
    {
        PreReviewNode.PhotoReview => "photo_review",
        PreReviewNode.RuleCheck => "rule_check",
        PreReviewNode.HistoryReview => "history_review",
        _ => node.ToString(),
    };
}
