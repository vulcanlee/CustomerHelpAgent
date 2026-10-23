namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// AI 對話紀錄（0.9.72 起）：每次<b>確實送出</b>的 AI API 呼叫一列。
///
/// 與 <see cref="TokenUsageLog"/> 的分工：
/// <list type="bullet">
///   <item><see cref="TokenUsageLog"/> 記「花了多少」，長期保存、只有數值，絕不存內文。</item>
///   <item>本表記「送了什麼、拿回什麼」的索引；完整請求與回應寫在檔案系統（見 <see cref="ContentFile"/>）。</item>
/// </list>
/// 兩者以 <see cref="CallId"/> 關聯，各自獨立清除（本表依 AiCallLogSettings:RetentionDays 自動過期）。
///
/// ⚠️ 內容含整份日誌、例外堆疊與使用者帳號，<b>只供系統管理員查閱</b>。
/// 本表只放可篩選的中繼資料，內文一律在內容檔。時間為本地時間（與 TokenUsageLog 一致）。
/// </summary>
public class AiCallLog
{
    public int Id { get; set; }

    /// <summary>呼叫識別碼，與 TokenUsageLog.CallId 相同；也是內容檔的檔名。</summary>
    public Guid CallId { get; set; }

    /// <summary>送出時間（本地時間）。</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>作業名稱（TokenUsageOperations）。</summary>
    public string Operation { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    /// <summary>實際使用的模型（優先取回應的 model，空的才退回設定值）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>發起呼叫的使用者帳號。系統自動觸發時為 null。刻意不記姓名／Email。</summary>
    public string? Account { get; set; }

    public int? UserId { get; set; }

    public bool Success { get; set; }

    /// <summary>失敗原因（AiAnalysisFailureReason 名稱，例如 Timeout、Canceled）。</summary>
    public string? FailureReason { get; set; }

    /// <summary>HTTP 狀態碼；沒拿到回應時為 null。</summary>
    public int? HttpStatus { get; set; }

    public string? FinishReason { get; set; }

    public long ElapsedMilliseconds { get; set; }

    /// <summary>原始請求 JSON 的字元數。</summary>
    public int RequestCharacters { get; set; }

    /// <summary>解析後回應文字的字元數。</summary>
    public int ResponseCharacters { get; set; }

    /// <summary>可讀的關聯說明（例外紀錄 Id、日誌區間等），供列表顯示與關鍵字篩選。</summary>
    public string? RelatedInfo { get; set; }

    /// <summary>同一段對話的識別碼（例外分析與其追問共用）。</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>內容檔的相對路徑，例如 202609/ab12….json。寫檔失敗時為 null。</summary>
    public string? ContentFile { get; set; }
}
