namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 例外告警信（LOG-12）的內容。<b>只放摘要</b>：信件可能被轉寄或長留在信箱，
/// 例外訊息全文、堆疊、帳號、UserId 一律不放，要看細節請登入系統。
/// </summary>
/// <param name="Reason">觸發原因，例如「新例外」「Critical」「10 分鐘內發生 20 次」。</param>
/// <param name="Link">系統例外紀錄頁的絕對網址；未設定 <c>EmailSettings:PublicBaseUrl</c> 時為 null，信中只寫頁面路徑。</param>
/// <param name="SuppressedCount">先前因每小時寄信上限而沒寄出的告警數，合併在這封信裡告知。</param>
public sealed record ExceptionAlertContent(
    string Reason,
    string ExceptionType,
    string Source,
    string? Page,
    long OccurrenceCount,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt,
    string? TraceId,
    string? Link,
    int SuppressedCount);
