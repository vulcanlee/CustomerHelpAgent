namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 反向代理（Nginx / IIS ARR / 雲端負載平衡器）的信任設定。
///
/// ⚠️ <c>UseForwardedHeaders</c> 若不指定信任來源，ASP.NET Core 預設只信任 loopback；
/// 但一旦清空預設值又沒填 KnownProxies/KnownNetworks，任何呼叫端都能偽造
/// <c>X-Forwarded-For</c>。限流以 IP 分割之後，這會直接變成繞過配額的手段。
/// </summary>
public class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>信任的反向代理 IP 清單（例如 "10.0.0.8"）。</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>信任的反向代理網段，CIDR 表示法（例如 "10.0.0.0/8"）。</summary>
    public string[] KnownNetworks { get; set; } = [];
}
