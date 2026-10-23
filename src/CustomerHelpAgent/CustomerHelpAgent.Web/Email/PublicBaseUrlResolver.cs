using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// 信中連結的網址基準（不含結尾斜線）。
///
/// ⚠️ 優先用 <see cref="EmailSettings.PublicBaseUrl"/>，**不信任請求的 Host header**：
/// 攻擊者可以對忘記密碼頁送出偽造 Host 的請求，讓受害者收到指向攻擊者網站的「真的」重設信
/// （password reset poisoning）。只有非 Production 且沒設定時，才退回目前請求的網址 —— 方便本機開發。
/// Production 用 Smtp 時 <c>StartupSafetyValidator</c> 已要求 <c>PublicBaseUrl</c> 必填。
/// </summary>
public static class PublicBaseUrlResolver
{
    /// <summary>回傳網址基準；Production 且未設定時回 null（呼叫端不得自行拿 Host header 補）。</summary>
    public static string? Resolve(EmailSettings settings, HttpRequest request, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
        {
            return settings.PublicBaseUrl.Trim().TrimEnd('/');
        }

        if (environment.IsProduction())
        {
            return null;
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }
}
