namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>
/// AI 對話紀錄的畫面繫結模型。本頁唯讀（只有刪除，沒有編輯），因此不需要 ICloneable。
/// 內文不在這裡，在內容檔（見 AiCallLogDetail.Content）。
/// </summary>
public class AiCallLogAdapterModel
{
    public int Id { get; set; }

    public Guid CallId { get; set; }

    public DateTime OccurredAt { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public int? HttpStatus { get; set; }

    public string? FinishReason { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public int RequestCharacters { get; set; }

    public int ResponseCharacters { get; set; }

    public string? RelatedInfo { get; set; }

    public Guid? ConversationId { get; set; }

    /// <summary>是否有內容檔（寫檔失敗時為 false）。</summary>
    public bool HasContent { get; set; }

    /// <summary>使用者取消（已送出、但沒有等到結果）。</summary>
    public bool IsCanceled => string.Equals(FailureReason, "Canceled", StringComparison.Ordinal);
}
