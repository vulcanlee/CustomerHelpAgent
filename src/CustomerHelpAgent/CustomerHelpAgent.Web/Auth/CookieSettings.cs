using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// Blazor UI 登入 Cookie 的效期設定。與 <see cref="JwtSettings"/> 平行 ——
/// 那一份管 API 的 session 長度，這一份管畫面的。
///
/// <para>⚠️ 0.9.39 之前這些值**完全沒有設定**，吃的是框架隱藏預設（14 天 ＋ 滑動續期），
/// 導致「登入能撐多久」這件事在設定檔裡查不到、也無法依環境調整。</para>
/// </summary>
public class CookieSettings
{
    public const string SectionName = "CookieSettings";

    /// <summary>
    /// 沒有勾「記住我」時的票證效期（分鐘）。預設 480（8 小時）。
    /// ⚠️ 此時 Cookie 同時是 session cookie，關掉瀏覽器就沒了 —— 兩個條件取先到者。
    /// </summary>
    [Range(1, 43200)]
    public int ExpireMinutes { get; set; } = 480;

    /// <summary>
    /// 勾了「記住我」時的票證效期（天）。預設 30。
    /// ⚠️ <c>ExpireTimeSpan</c> 是整個 scheme 共用的，做不出兩種效期，
    /// 因此這個值是在登入時以 <c>AuthenticationProperties.ExpiresUtc</c> 明寫的
    /// （見 <c>Login.razor.cs</c>）。
    /// </summary>
    [Range(1, 365)]
    public int RememberMeDays { get; set; } = 30;

    /// <summary>
    /// 是否啟用滑動續期。預設 <c>true</c>（與框架預設一致）。
    ///
    /// <para>⚠️ 規則不是「每次請求都續」：**已用掉超過一半效期時的那一次請求**才續期，
    /// 並從那一刻起重新算滿。設 <c>false</c> 則到期就是到期。</para>
    /// <para>⚠️ Blazor Server 特有：續期只發生在真正的 HTTP 請求上，
    /// circuit 內的 SignalR 換頁不經過 cookie 中介軟體。</para>
    /// </summary>
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// 工作階段版本在 HTTP 請求上的檢查間隔（分鐘，0.9.103 起）。預設 5。
    /// 改密碼、停用、強制登出之後，其他瀏覽器的下一個 HTTP 請求最晚在這段時間內被登出；同一個行程內強制登出是立即的。
    /// 已開著的頁面在換頁時檢查（不受這個值影響）。調小會增加每次請求查資料庫的機會。
    /// </summary>
    [Range(1, 60)]
    public int ValidationIntervalMinutes { get; set; } = 5;
}
