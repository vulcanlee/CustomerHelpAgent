using System.ComponentModel.DataAnnotations;
using System.Net.Mail;

namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 寄信設定（<c>EmailSettings</c> 區段）。
///
/// <para>Provider 比照 <see cref="CacheSettings"/> 存字串再解析：打錯字時得到可讀的中文訊息，
/// 而不是框架的綁定例外。</para>
///
/// <para>⚠️ <see cref="Password"/> 是機密，<c>appsettings.json</c> 一律出貨空字串，
/// 開發機用 User Secrets、正式環境用環境變數 <c>EmailSettings__Password</c>。</para>
/// </summary>
public class EmailSettings
{
    public const string SectionName = "EmailSettings";

    /// <summary><c>None</c>（預設，停用）、<c>Pickup</c>（寫 .eml 檔，開發用）或 <c>Smtp</c>。</summary>
    public string Provider { get; set; } = nameof(EmailProvider.None);

    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary><c>Auto</c>、<c>None</c>、<c>StartTls</c>（預設，587）或 <c>SslOnConnect</c>（465）。</summary>
    public string Security { get; set; } = nameof(EmailSecurityMode.StartTls);

    /// <summary>SMTP 登入帳號；留空代表不登入（內網中繼）。</summary>
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    /// <summary>寄件者顯示名稱；留空時使用 <c>SystemSettings:SystemInformation:SystemName</c>。</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>Pickup 模式存放 .eml 的資料夾。第一次寫信時才建立。</summary>
    public string PickupDirectory { get; set; } = @"C:\temp\CustomerHelpAgent\Mails";

    /// <summary>
    /// 對外公開網址（例如 <c>https://erp.example.com</c>），信中連結一律以它為準。
    /// 不從請求的 Host header 組網址，以免被偽造 Host 的請求把連結導到別處。
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    public EmailProvider GetProvider()
    {
        if (TryGetProvider(out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"不支援的寄信 provider：{Provider}");
    }

    public bool TryGetProvider(out EmailProvider provider)
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            provider = EmailProvider.None;
            return true;
        }

        return Enum.TryParse(Provider, ignoreCase: true, out provider) && Enum.IsDefined(provider);
    }

    public EmailSecurityMode GetSecurity()
    {
        if (TryGetSecurity(out var security))
        {
            return security;
        }

        throw new InvalidOperationException($"不支援的寄信加密方式：{Security}");
    }

    public bool TryGetSecurity(out EmailSecurityMode security)
    {
        if (string.IsNullOrWhiteSpace(Security))
        {
            security = EmailSecurityMode.StartTls;
            return true;
        }

        return Enum.TryParse(Security, ignoreCase: true, out security) && Enum.IsDefined(security);
    }

    /// <summary>
    /// Smtp 模式的必要欄位。所有環境都檢查（啟動時 <c>ValidateOnStart</c>）：
    /// 選了 Smtp 卻沒填主機或寄件者，等到第一封信寄不出去才發現就太晚了。
    /// </summary>
    public bool HasRequiredSmtpFields()
    {
        if (!TryGetProvider(out var provider) || provider != EmailProvider.Smtp)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Host) && MailAddress.TryCreate(FromAddress, out _);
    }
}

public enum EmailProvider
{
    None,
    Pickup,
    Smtp
}

public enum EmailSecurityMode
{
    Auto,
    None,
    StartTls,
    SslOnConnect
}
