using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 慢操作記錄的門檻（<c>SlowOperationSettings</c> 區段，0.9.79 起，LOG-21）。超過就記一筆 Warning，
/// 含操作名稱、耗時與門檻。任一項設為 <c>0</c> 即停用該項。
///
/// <para>刻意<b>不量「Blazor 單次互動」</b>：AI 分析在按鈕事件裡等數十秒、確認窗等使用者按鈕，都會被算進互動時間，
/// 每次都誤報。改在真正耗時的地方量：HTTP 請求、資料庫指令、寄信、AI 呼叫。
/// Google SSO 的往返（<c>/signin-google</c> 與回呼）是 HTTP 請求，由 <see cref="HttpRequestMs"/> 涵蓋。</para>
/// </summary>
public class SlowOperationSettings
{
    public const string SectionName = "SlowOperationSettings";

    /// <summary>HTTP 請求（排除 <c>/_blazor</c> 長連線）。</summary>
    [Range(0, 3_600_000)]
    public int HttpRequestMs { get; set; } = 3000;

    /// <summary>單一資料庫指令（EF Core 攔截器，只記指令類型，不記 SQL 與參數）。</summary>
    [Range(0, 3_600_000)]
    public int DbCommandMs { get; set; } = 1000;

    /// <summary>寄出一封信（背景寄信作業）。</summary>
    [Range(0, 3_600_000)]
    public int ExternalCallMs { get; set; } = 10000;

    /// <summary>一次 AI 呼叫（含健康檢查的探測）。</summary>
    [Range(0, 3_600_000)]
    public int AiCallMs { get; set; } = 60000;

    /// <summary>耗時是否超過門檻；門檻 ≤ 0 代表停用。</summary>
    public static bool IsSlow(TimeSpan elapsed, int thresholdMs)
        => thresholdMs > 0 && elapsed.TotalMilliseconds > thresholdMs;
}
