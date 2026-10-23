using System.Text.Json;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 一次 AI 呼叫的完整紀錄：既是 <c>IAiCallLogRecorder.RecordAsync</c> 的輸入，也是內容檔的 JSON 結構。
///
/// ⚠️ <b>屬性名稱就是檔案格式</b>：改名會讓舊檔讀不回來。結構有不相容變動時請遞增 <see cref="SchemaVersion"/>。
///
/// ⚠️ 絕不可放入 HTTP 標頭：API 金鑰只在單次請求的標頭上。<see cref="Endpoint"/> 只是請求 URI，不含金鑰。
/// </summary>
public sealed class AiCallLogEntry
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>本次呼叫的識別碼，與 TokenUsageLog.CallId 相同，用來雙向關聯。</summary>
    public Guid CallId { get; init; }

    /// <summary>送出時間（本地時間，與 TokenUsageLog 一致）。</summary>
    public DateTime OccurredAt { get; init; }

    public string Operation { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    /// <summary>設定上要求的模型。</summary>
    public string RequestedModel { get; init; } = string.Empty;

    /// <summary>實際使用的模型（優先取回應的 model，空的才退回設定值）。</summary>
    public string Model { get; init; } = string.Empty;

    public string? Account { get; init; }

    public int? UserId { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    /// <summary>可讀的關聯說明，例如「例外紀錄 #42（NullReferenceException）」、「日誌 …～…，送出 n／N 筆」。</summary>
    public string? RelatedInfo { get; init; }

    /// <summary>同一段對話的識別碼（例外分析與其追問共用）；其他作業為 null。</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>原始請求 JSON（逐字）。</summary>
    public string RequestBody { get; init; } = string.Empty;

    /// <summary>HTTP 狀態碼；沒拿到回應（逾時、連線失敗、取消）時為 null。</summary>
    public int? HttpStatus { get; init; }

    /// <summary>原始回應 body（含錯誤 body）；沒拿到回應時為 null。</summary>
    public string? ResponseBody { get; init; }

    /// <summary>解析後的回應文字；沒拿到或解析不出時為空字串。</summary>
    public string ResponseText { get; init; } = string.Empty;

    public string? FinishReason { get; init; }

    public bool Success { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>例外型別名稱（逾時、連線失敗、非預期錯誤時）。</summary>
    public string? ExceptionType { get; init; }

    public long ElapsedMilliseconds { get; init; }
}

/// <summary>請求中的一則訊息。</summary>
public sealed record AiCallLogMessage(string Role, string Content);

/// <summary>從原始請求 JSON 解析出 messages（讀取時才解析，不另外存一份）。</summary>
public static class AiCallLogMessages
{
    /// <summary>解析 <c>messages[].role/content</c>。JSON 無效或沒有 messages 時回 null。</summary>
    public static IReadOnlyList<AiCallLogMessage>? Parse(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("messages", out var messages) == false
                || messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<AiCallLogMessage>();
            foreach (var message in messages.EnumerateArray())
            {
                var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                    ? roleElement.GetString() ?? string.Empty
                    : string.Empty;

                var content = string.Empty;
                if (message.TryGetProperty("content", out var contentElement))
                {
                    content = contentElement.ValueKind == JsonValueKind.String
                        ? contentElement.GetString() ?? string.Empty
                        : contentElement.GetRawText();
                }

                result.Add(new AiCallLogMessage(role, content));
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>AI 對話紀錄的查詢條件。比照 <see cref="TokenUsageQuery"/>，不污染共用的 DataRequest。</summary>
public class AiCallLogQuery
{
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public string Operation { get; set; } = string.Empty;

    /// <summary>帳號，部分比對。</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>成敗篩選：true 只看成功、false 只看失敗、null 不限。</summary>
    public bool? Success { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>關聯說明關鍵字，部分比對。</summary>
    public string Keyword { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}

/// <summary>下拉的可選值，只列出資料庫中實際出現過的。</summary>
public class AiCallLogFilterOptions
{
    public List<string> Operations { get; set; } = new();

    public List<string> Models { get; set; } = new();
}

/// <summary>明細視窗需要的全部資料。</summary>
public sealed class AiCallLogDetail
{
    public AiCallLogAdapterModel Item { get; init; } = new();

    /// <summary>內容檔；檔案不存在或無法解析時為 null。</summary>
    public AiCallLogEntry? Content { get; init; }

    /// <summary>由原始請求解析出的訊息；無法解析時為 null。</summary>
    public IReadOnlyList<AiCallLogMessage>? Messages { get; init; }

    /// <summary>對應的 Token 用量紀錄（以 CallId 關聯）；沒有時為 null。</summary>
    public TokenUsageLogAdapterModel? Usage { get; init; }

    /// <summary>同一對話的所有紀錄（依時間由舊到新）；不屬於任何對話時為空。</summary>
    public IReadOnlyList<AiCallLogAdapterModel> Conversation { get; init; } = [];
}
