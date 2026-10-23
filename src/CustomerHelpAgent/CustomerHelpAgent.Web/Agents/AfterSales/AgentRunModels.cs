using System.Text.Encodings.Web;
using System.Text.Json;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>執行軌跡的一個事件（同時送給畫面的幕後面板與寫入 <c>AgentRunStep</c>）。</summary>
public sealed record AgentTraceEvent(AgentStepKind Kind, AgentDataSource Source, string PlainText, string? RawJson, DateTime OccurredAt);

/// <summary>
/// 寫入前的確認卡。內容全部由程式依模型提出的參數<b>重新查詢</b>後組成（商品、規格、規則版本、簽收天數、照片張數），不採模型文字。
/// <see cref="ApprovalRequestId"/> 是 MAF 的 <c>ToolApprovalRequestContent.RequestId</c>。
/// </summary>
public sealed record ConfirmationCard(
    int ConfirmationId,
    string ApprovalRequestId,
    string Title,
    string ProductLine,
    string FromSpec,
    string? ToSpec,
    string IssueLabel,
    string PolicyLine,
    IReadOnlyList<string> Facts,
    int PhotoCount)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>存進 <c>AgentConfirmation.CardJson</c> 與還原用事件 <c>RawJson</c> 的格式。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ConfirmationCard FromJson(string json)
        => JsonSerializer.Deserialize<ConfirmationCard>(json, JsonOptions)
            ?? throw new JsonException("Confirmation card JSON is empty.");
}

/// <summary>
/// 一輪執行的結果。
/// <list type="bullet">
/// <item><see cref="ReplyText"/>：顧客看到的回覆。串流期間 <c>onTextDelta</c> 送出的只是預覽，最後一律以這裡為準。</item>
/// <item><see cref="StopExplanation"/>：異常停止時由程式產生的白話說明（逾時、權限、服務不可用、額度、模型錯誤、步數上限、連續失敗、規則衝突、取消）；
/// 正常停下（已回答、等待回覆／確認／上傳）為 null。逾時、權限、服務不可用、額度與模型錯誤時，<see cref="ReplyText"/> 就是這段固定說明（模型文字被取代）。</item>
/// <item><see cref="PolicyCards"/>：本輪 <c>get_effective_policy</c> 成功取得的規則卡，畫面據此渲染條文（不採模型轉述）。</item>
/// <item><see cref="CreatedRequestNo"/>：本輪確實建立並回傳的申請編號；沒有建立就是 null。</item>
/// </list>
/// </summary>
public sealed record AgentTurnResult(
    string ReplyText,
    IReadOnlyList<AgentPolicyCard> PolicyCards,
    ConfirmationCard? Confirmation,
    AgentStopReason StopReason,
    string? StopExplanation,
    string? CreatedRequestNo);
