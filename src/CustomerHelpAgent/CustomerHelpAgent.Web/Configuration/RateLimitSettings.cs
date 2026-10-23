namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 速率限制設定。各案的流量特性不同（內網管理系統 vs 對外開放 API），
/// 因此做成可由 appsettings 覆寫，而不是寫死在程式碼。
/// </summary>
public class RateLimitSettings
{
    public const string SectionName = "RateLimit";

    /// <summary>一般 API 的每分鐘配額（每個呼叫端各自計算）。</summary>
    public int ApiRequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// 登入端點的每分鐘配額；比一般 API 嚴格得多。
    /// 帳號鎖定（連續失敗 5 次）是第二道防線，但它擋不住「橫向」猜測多個帳號。
    /// </summary>
    public int LoginRequestsPerMinute { get; set; } = 10;
}
