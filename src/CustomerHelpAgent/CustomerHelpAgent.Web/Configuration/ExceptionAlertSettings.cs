using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 系統例外的 Email 告警（<c>ExceptionAlertSettings</c> 區段，0.9.78 起，LOG-12）。
///
/// <para><see cref="Recipients"/> 為空即停用（出貨預設）。收件人與系統帳號無關，可以填維運群組信箱。
/// 實際寄出還需要 <c>EmailSettings:Provider</c> 不是 <c>None</c>；信件裡的連結取自 <c>EmailSettings:PublicBaseUrl</c>。</para>
///
/// <para>三種觸發：新簽章第一次發生、Critical、同一簽章在 <see cref="BurstWindowMinutes"/> 分鐘內達 <see cref="BurstThreshold"/> 次。
/// 兩層節流：同一簽章 <see cref="PerSignatureCooldownMinutes"/> 分鐘內只寄一次；全系統每小時最多 <see cref="MaxEmailsPerHour"/> 封，
/// 超過的不寄，筆數併入下一封告知。</para>
/// </summary>
public class ExceptionAlertSettings
{
    public const string SectionName = "ExceptionAlertSettings";

    /// <summary>告警收件人；空清單＝停用告警。</summary>
    public string[] Recipients { get; set; } = [];

    [Range(2, 100000)]
    public int BurstThreshold { get; set; } = 20;

    [Range(1, 1440)]
    public int BurstWindowMinutes { get; set; } = 10;

    [Range(0, 10080)]
    public int PerSignatureCooldownMinutes { get; set; } = 60;

    [Range(1, 1000)]
    public int MaxEmailsPerHour { get; set; } = 20;

    public bool IsEnabled => Recipients.Any(x => string.IsNullOrWhiteSpace(x) == false);
}
