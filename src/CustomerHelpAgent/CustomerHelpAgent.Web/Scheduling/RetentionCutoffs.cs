namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 各類資料「超過保留天數」的門檻（0.9.133 起）：清理作業與「資料保留與清理」頁的「下次會清掉幾筆」共用，
/// 時間基準（本地或 UTC）跟著各資料表存的時間走，兩邊才不會差上 8 小時。呼叫端先確認天數大於 0（0＝不清理）。
/// Token 用量與 AI 對話紀錄的門檻（當天 0 點往前）由各自的服務提供：<c>TokenUsageLogService.ExpiryThreshold</c>、<c>AiCallLogService.ExpiryThreshold</c>。
/// </summary>
public static class RetentionCutoffs
{
    /// <summary>系統例外紀錄：「最後發生」是本地時間。</summary>
    public static DateTime ExceptionLog(int days, TimeProvider timeProvider) => Local(days, timeProvider);

    /// <summary>稽核紀錄：<c>OccurredAt</c> 存 UTC。</summary>
    public static DateTime AuditLog(int days, TimeProvider timeProvider) => Utc(days, timeProvider);

    /// <summary>已刪除資料：<c>DeletedAt</c> 是本地時間。</summary>
    public static DateTime SoftDelete(int days, TimeProvider timeProvider) => Local(days, timeProvider);

    /// <summary>站內通知（建立或已讀時間）：存 UTC。</summary>
    public static DateTime Notification(int days, TimeProvider timeProvider) => Utc(days, timeProvider);

    /// <summary>排程執行紀錄：<c>StartedAtUtc</c>。</summary>
    public static DateTime JobRun(int days, TimeProvider timeProvider) => Utc(days, timeProvider);

    private static DateTime Local(int days, TimeProvider timeProvider) => timeProvider.GetLocalNow().DateTime.AddDays(-days);

    private static DateTime Utc(int days, TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
}
