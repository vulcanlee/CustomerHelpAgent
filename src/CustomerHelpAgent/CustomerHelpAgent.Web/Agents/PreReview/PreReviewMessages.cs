using System.Text.Json;
using System.Text.Json.Serialization;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

// 初審 Workflow 節點之間傳遞的訊息與結論。
// ⚠️ 這些型別會序列化進 checkpoint（含組件與型別名稱）：改名、搬命名空間或刪欄位，會讓重啟前存下的 checkpoint 無法還原。
// 新增跨邊訊息或節點狀態型別時，要同步加進 PreReviewJsonContext。

/// <summary>Workflow 的輸入：開始跑這一輪初審。</summary>
public sealed record PreReviewStart(int RunId, int RequestId);

/// <summary>
/// 申請事實（Intake 產生，送給三路初審與摘要）。
/// ⚠️ 不含個資：沒有姓名、帳號、Email、地址、手機、使用者 Id；照片只給附件 Id，不放位元組。
/// </summary>
public sealed record PreReviewCase(
    int RunId,
    int RequestId,
    Guid CaseKey,
    AfterSalesType Type,
    string ProductName,
    string ProductCategory,
    string Color,
    string Size,
    string? TargetColor,
    string? TargetSize,
    int? DaysSinceDelivery,
    bool? IsWorn,
    string Description,
    IReadOnlyList<int> AttachmentIds);

/// <summary>節點結論的狀態。</summary>
public enum FindingStatus
{
    Ok,

    /// <summary>不適用（例如沒有附照片，不呼叫照片初審模型）。</summary>
    NotApplicable,

    /// <summary>節點失敗（模型錯誤、逾時等）；Workflow 照常走到人工決策。</summary>
    Failed,
}

/// <summary>
/// 一個初審節點的結論；<see cref="Json"/> 是節點自己的明細（規則核對為 <see cref="RuleCheckResult"/>，摘要為 <see cref="PreReviewSummary"/>）。
/// ⚠️ 自訂轉換器是 MAF Workflows 1.25.0 的缺陷繞道，不可拿掉：見 <see cref="PreReviewFindingJsonConverter"/>。
/// </summary>
[JsonConverter(typeof(PreReviewFindingJsonConverter))]
public sealed record PreReviewFinding(PreReviewNode Node, FindingStatus Status, string Headline, string Json);

/// <summary>
/// 規則核對（純程式）的明細。<see cref="IsEligible"/> 為 false 時摘要不可建議核准。
/// <see cref="TargetInStock"/>：換貨類為目標規格、品質問題為同規格（補寄）是否有貨；其他類型為 null。
/// </summary>
public sealed record RuleCheckResult(
    int? PolicyId,
    string? PolicyTitle,
    int? PolicyVersion,
    int? DaysSinceDelivery,
    bool IsEligible,
    IReadOnlyList<string> UnmetReasons,
    IReadOnlyList<string> MissingInfo,
    bool? TargetInStock);

/// <summary>摘要建議的值（<see cref="PreReviewSummary.Recommendation"/>）。</summary>
public static class PreReviewRecommendations
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string NeedMoreInfo = "need_more_info";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = [Approve, Reject, NeedMoreInfo, Manual];
}

/// <summary>
/// 摘要節點的輸出（摘要模型的結構化輸出也是這個形狀）。
/// <see cref="Warning"/> 只由程式設定（規則要求把建議降為需人工判斷時的說明，畫面以紅字顯示）；模型給的值一律被清掉。
/// </summary>
public sealed record PreReviewSummary(
    string Recommendation,
    string Headline,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> StaffChecklist,
    string? Warning = null);

/// <summary>等待客服決定（人工決策 RequestPort 的請求）。<see cref="PreviousFailure"/> 是上一次執行失敗的原因（例如無庫存）。</summary>
public sealed record DecisionRequest(int RunId, string? PreviousFailure);

/// <summary>客服的決定（人工決策 RequestPort 的回應）。</summary>
public sealed record StaffDecision(StaffDecisionKind Kind, string? Note, int StaffUserId);

/// <summary>執行節點的結果（送給通知節點，同時以 Workflow output 交給 Runner 回報客服）。</summary>
public sealed record ExecutionResult(DecisionResult Result);

/// <summary>
/// <see cref="PreReviewFinding"/> 的 JSON 轉換器。
/// ⚠️ MAF Workflows 1.25.0 的缺陷：經 fan-in barrier 邊送出的訊息在 checkpoint 裡多包了一層
/// <c>{"typeId":…,"value":…}</c>（PortableValue），從 checkpoint 還原時框架把這層外殼當成訊息本身反序列化，
/// 結果是每個欄位都是預設值（Node＝Intake、Headline＝null）。這裡讀取時剝掉外殼再讀，寫出時維持一般格式。
/// </summary>
public sealed class PreReviewFindingJsonConverter : JsonConverter<PreReviewFinding>
{
    public override PreReviewFinding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        while (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("typeId", out _)
            && element.TryGetProperty("value", out var inner))
        {
            element = inner;
        }

        var data = element.Deserialize(PreReviewJsonContext.Default.PreReviewFindingData);
        return data is null ? null : new PreReviewFinding(data.Node, data.Status, data.Headline ?? string.Empty, data.Json ?? "{}");
    }

    public override void Write(Utf8JsonWriter writer, PreReviewFinding value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(
            writer,
            new PreReviewFindingData(value.Node, value.Status, value.Headline, value.Json),
            PreReviewJsonContext.Default.PreReviewFindingData);
}

/// <summary><see cref="PreReviewFindingJsonConverter"/> 的實際序列化形狀（同欄位、不掛轉換器）。</summary>
public sealed record PreReviewFindingData(PreReviewNode Node, FindingStatus Status, string? Headline, string? Json);
