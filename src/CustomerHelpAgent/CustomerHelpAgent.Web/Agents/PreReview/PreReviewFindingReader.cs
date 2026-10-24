using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 讀回 <c>PreReviewRun</c> 節點結論欄（<c>PhotoJson</c>／<c>RuleJson</c>／<c>HistoryJson</c>／<c>SummaryJson</c>）：欄位存的是序列化的
/// <see cref="PreReviewFinding"/>，明細再包在 <see cref="PreReviewFinding.Json"/> 裡。審核頁的 AI 初審面板與客服 AI 助手的
/// <c>get_pre_review_result</c> 共用；壞掉的資料一律當作沒有（不讓畫面或工具因此失敗）。
/// </summary>
public static class PreReviewFindingReader
{
    public static PreReviewFinding? ParseFinding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, PreReviewJsonContext.Default.PreReviewFinding);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static T? ParseDetail<T>(PreReviewFinding? finding, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (finding is null || string.IsNullOrWhiteSpace(finding.Json) || finding.Json == "{}")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(finding.Json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
