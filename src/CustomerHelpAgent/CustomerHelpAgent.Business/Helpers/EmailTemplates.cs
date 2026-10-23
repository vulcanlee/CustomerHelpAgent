using System.Net;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 系統寄出的每一種信的內容（HTML ＋ 純文字）。
///
/// <para>⚠️ 放進 HTML 的每一個變數都必須經過 <see cref="WebUtility.HtmlEncode(string)"/>：
/// 帳號、系統名稱都可能含有 <c>&lt;</c>、<c>&amp;</c>，不編碼就是信件內的 HTML 注入。</para>
///
/// <para>模板寫在程式裡（而不是外部 .html 檔）：可以單元測試、不怕部署漏檔，
/// 也不需要一套佔位符檢查機制。要改文案就改這裡。</para>
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage BuildTest(string to, string systemName, DateTime sentAtLocal, string provider)
    {
        var sentAt = sentAtLocal.ToString("yyyy/MM/dd HH:mm:ss");
        var subject = $"[{systemName}] 測試信";

        var html = WrapHtml(
            systemName,
            "這是一封測試信",
            $"<p>如果您收到這封信，代表「{Encode(systemName)}」的寄信設定可以正常運作。</p>"
            + $"<p style=\"color:#666666;font-size:13px;\">寄送時間：{Encode(sentAt)}<br />寄送方式：{Encode(provider)}</p>");

        var text = $"如果您收到這封信，代表「{systemName}」的寄信設定可以正常運作。\n\n"
            + $"寄送時間：{sentAt}\n寄送方式：{provider}\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.Test);
    }

    /// <summary>
    /// 忘記密碼的重設信。<paramref name="link"/> 內含 token，**只能出現在信裡**，不可寫進日誌或稽核。
    /// </summary>
    public static EmailMessage BuildPasswordReset(string to, string systemName, string account, string link, int lifetimeMinutes)
    {
        var subject = $"[{systemName}] 重設密碼";

        var html = WrapHtml(
            systemName,
            "重設您的密碼",
            $"<p>我們收到帳號「{Encode(account)}」的重設密碼申請。請在 <strong>{lifetimeMinutes} 分鐘內</strong>點下方按鈕設定新密碼：</p>"
            + $"<p style=\"margin:24px 0;\"><a href=\"{Encode(link)}\" style=\"display:inline-block;padding:10px 20px;background:#555555;color:#ffffff;text-decoration:none;border-radius:6px;\">設定新密碼</a></p>"
            + $"<p style=\"color:#666666;font-size:13px;\">按鈕無法點選時，請將下列網址貼到瀏覽器：<br />{Encode(link)}</p>"
            + "<p style=\"color:#666666;font-size:13px;\">連結只能使用一次。如果不是您本人申請，請忽略這封信，您的密碼不會改變。</p>");

        var text = $"我們收到帳號「{account}」的重設密碼申請。\n\n"
            + $"請在 {lifetimeMinutes} 分鐘內開啟下列網址設定新密碼（連結只能使用一次）：\n{link}\n\n"
            + "如果不是您本人申請，請忽略這封信，您的密碼不會改變。\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.PasswordReset);
    }

    /// <summary>重設成功後的通知信：若不是本人操作，帳號主人才有機會察覺。</summary>
    public static EmailMessage BuildPasswordChanged(string to, string systemName, string account, DateTime changedAtLocal)
    {
        var changedAt = changedAtLocal.ToString("yyyy/MM/dd HH:mm:ss");
        var subject = $"[{systemName}] 您的密碼已變更";

        var html = WrapHtml(
            systemName,
            "您的密碼已變更",
            $"<p>帳號「{Encode(account)}」的密碼已於 {Encode(changedAt)} 透過「忘記密碼」重新設定。</p>"
            + "<p>如果這不是您本人的操作，請立即聯絡系統管理員。</p>");

        var text = $"帳號「{account}」的密碼已於 {changedAt} 透過「忘記密碼」重新設定。\n\n"
            + "如果這不是您本人的操作，請立即聯絡系統管理員。\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.PasswordChanged);
    }

    /// <summary>
    /// 系統例外告警（LOG-12）。<b>只含摘要</b>：類型、來源、頁面、次數、時間、追蹤碼與連結；
    /// 例外訊息全文、堆疊、帳號都不放 —— 信件可能被轉寄或長留在信箱，細節請登入系統查看。
    /// </summary>
    public static EmailMessage BuildExceptionAlert(string to, string systemName, ExceptionAlertContent content)
    {
        var subject = $"[{systemName}] 系統例外告警：{content.Reason}";
        var page = string.IsNullOrWhiteSpace(content.Page) ? "—" : content.Page;
        var traceId = string.IsNullOrWhiteSpace(content.TraceId) ? "—" : content.TraceId;
        var first = content.FirstOccurredAt.ToString("yyyy/MM/dd HH:mm:ss");
        var last = content.LastOccurredAt.ToString("yyyy/MM/dd HH:mm:ss");
        var suppressed = content.SuppressedCount > 0
            ? $"另外，先前有 {content.SuppressedCount} 則告警因每小時寄信上限而未寄出，請一併到系統查看。"
            : null;

        var rows = new (string Label, string Value)[]
        {
            ("觸發原因", content.Reason),
            ("例外類型", content.ExceptionType),
            ("來源", content.Source),
            ("頁面", page),
            ("累計次數", content.OccurrenceCount.ToString("N0")),
            ("首次發生", first),
            ("最後發生", last),
            ("錯誤追蹤碼", traceId),
        };

        var tableHtml = "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + string.Concat(rows.Select(row =>
                $"<tr><td style=\"padding:4px 12px 4px 0;color:#666666;white-space:nowrap;\">{Encode(row.Label)}</td>"
                + $"<td style=\"padding:4px 0;\">{Encode(row.Value)}</td></tr>"))
            + "</table>";

        var linkHtml = content.Link is null
            ? "<p>請登入系統，於「系統例外紀錄」頁（/system-exceptions）查看完整內容。</p>"
            : $"<p style=\"margin:24px 0;\"><a href=\"{Encode(content.Link)}\" style=\"display:inline-block;padding:10px 20px;background:#555555;color:#ffffff;text-decoration:none;border-radius:6px;\">查看系統例外紀錄</a></p>";

        var html = WrapHtml(
            systemName,
            "系統例外告警",
            tableHtml + linkHtml
            + (suppressed is null ? string.Empty : $"<p style=\"color:#666666;font-size:13px;\">{Encode(suppressed)}</p>"));

        var text = string.Concat(rows.Select(row => $"{row.Label}：{row.Value}\n"))
            + "\n"
            + (content.Link is null
                ? "請登入系統，於「系統例外紀錄」頁（/system-exceptions）查看完整內容。\n"
                : $"查看系統例外紀錄：{content.Link}\n")
            + (suppressed is null ? string.Empty : $"\n{suppressed}\n");

        return new EmailMessage(to, subject, html, text, EmailKinds.ExceptionAlert);
    }

    /// <summary>
    /// 站內通知的同步信（0.9.100 起）。<paramref name="link"/> 是完整網址（沒有設定 <c>PublicBaseUrl</c> 時為 null，信裡只請收件人登入查看）。
    /// </summary>
    public static EmailMessage BuildNotification(string to, string systemName, string title, string? body, string? link)
    {
        var subject = $"[{systemName}] {title}";
        var bodyHtml = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : $"<p style=\"white-space:pre-wrap;\">{Encode(body)}</p>";
        var linkHtml = link is null
            ? "<p style=\"color:#666666;font-size:13px;\">請登入系統，按右上角的鈴鐺查看。</p>"
            : $"<p style=\"margin:24px 0;\"><a href=\"{Encode(link)}\" style=\"display:inline-block;padding:10px 20px;background:#555555;color:#ffffff;text-decoration:none;border-radius:6px;\">前往查看</a></p>";

        var html = WrapHtml(systemName, title, bodyHtml + linkHtml);
        var text = (string.IsNullOrWhiteSpace(body) ? string.Empty : body + "\n\n")
            + (link is null ? "請登入系統，按右上角的鈴鐺查看。\n" : $"前往查看：{link}\n");

        return new EmailMessage(to, subject, html, text, EmailKinds.Notification);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string WrapHtml(string systemName, string heading, string bodyHtml)
    {
        return "<!DOCTYPE html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\" /></head>"
            + "<body style=\"margin:0;padding:24px;background:#f5f5f5;font-family:'Microsoft JhengHei','PingFang TC',sans-serif;color:#333333;\">"
            + "<div style=\"max-width:560px;margin:0 auto;padding:24px;background:#ffffff;border:1px solid #e5e5e5;border-radius:8px;\">"
            + $"<p style=\"margin:0 0 8px;color:#888888;font-size:13px;\">{Encode(systemName)}</p>"
            + $"<h1 style=\"margin:0 0 16px;font-size:20px;\">{Encode(heading)}</h1>"
            + bodyHtml
            + "<p style=\"margin:24px 0 0;color:#999999;font-size:12px;\">此信由系統自動寄出，請勿直接回覆。</p>"
            + "</div></body></html>";
    }
}
