using System.Text.Encodings.Web;
using System.Text.Json;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>執行軌跡的一個事件（同時送給畫面的幕後面板與寫入 <c>AgentRunStep</c>）。</summary>
public sealed record AgentTraceEvent(AgentStepKind Kind, AgentDataSource Source, string PlainText, string? RawJson, DateTime OccurredAt);

/// <summary>確認卡的種類（M5 起）。⚠️ 數值存在 <c>AgentConfirmation.CardJson</c> 裡；M5 之前的卡片沒有這個欄位，讀回時一律是 <see cref="AfterSalesRequest"/>（0）。</summary>
public enum ConfirmationCardKind
{
    /// <summary>建立售後申請（<c>create_after_sales_request</c>）。</summary>
    AfterSalesRequest = 0,

    /// <summary>記住顧客的一項偏好（<c>remember_preference</c>）：不是申請、不是業務事實。</summary>
    Memory = 1,
}

/// <summary>
/// 寫入前的確認卡。內容全部由程式依模型提出的參數<b>重新查詢</b>後組成（商品、規格、規則版本、簽收天數、照片張數），不採模型文字。
/// <see cref="ApprovalRequestId"/> 是 MAF 的 <c>ToolApprovalRequestContent.RequestId</c>。
/// 記憶卡（<see cref="Kind"/>＝<see cref="ConfirmationCardKind.Memory"/>，M5 起）：<see cref="Facts"/> 是程式產生的詢問句，
/// <see cref="MemoryKind"/>／<see cref="MemoryValue"/> 是經 <c>MemoryContentPolicy</c> 重核後要記住的內容；商品、規格、規則欄位為空。
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
    int PhotoCount,
    ConfirmationCardKind Kind = ConfirmationCardKind.AfterSalesRequest,
    MemoryKind? MemoryKind = null,
    string? MemoryValue = null)
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
/// <item><see cref="Speaker"/>：多 Agent 模式（M5）最後一段回覆的發言者顯示名稱（例如「品質專員」）；單一模式或回覆是程式說明時為 null。</item>
/// <item><see cref="Segments"/>：多 Agent 模式（M5）同一輪有兩位以上的 Agent 發言時，<see cref="ReplyText"/> 依發言者切開的各段
/// （以空一行串接即為 <see cref="ReplyText"/>）；只有一位發言者、單一模式或程式說明時為 null（以 <see cref="Speaker"/> 標示）。</item>
/// </list>
/// </summary>
public sealed record AgentTurnResult(
    string ReplyText,
    IReadOnlyList<AgentPolicyCard> PolicyCards,
    ConfirmationCard? Confirmation,
    AgentStopReason StopReason,
    string? StopExplanation,
    string? CreatedRequestNo,
    string? Speaker = null,
    IReadOnlyList<AgentReplySegment>? Segments = null);

/// <summary>多 Agent 模式（M5）一輪回覆中的一段：發言者顯示名稱（例如「換退貨專員」；認不得時為 null）與那一段的文字。</summary>
public sealed record AgentReplySegment(string? Speaker, string Text);

/// <summary>
/// 回放劇本（1.0.5 起，<c>AfterSalesAgentRunner.GetReplayScriptAsync</c>）：<see cref="IsReplay"/>＝目前是否回放中（畫面要明確標示「回放中」）；
/// <see cref="NextTurn"/>＝下一個待執行的錄製動作（頁面據此帶入下一句、或只開放錄製時按的確認／取消），播完或不是回放時為 null；
/// <see cref="RecordedAt"/>＝錄製建立的時間（頁面的「回放中（錄製於 …）」標籤），錄製已刪除或不是回放時為 null；
/// <see cref="Mode"/>（M5）＝錄製時的會話模式：回放必須以這個模式開新對話（<c>StartNewAsync(Mode)</c>），否則 Runner 擋下每個動作；
/// 錄製已刪除或不是回放時為 null。
/// </summary>
public sealed record AgentReplayScript(bool IsReplay, AgentRecordedTurnView? NextTurn, DateTime? RecordedAt = null, AgentMode? Mode = null)
{
    public static AgentReplayScript NotReplay { get; } = new(false, null);
}
