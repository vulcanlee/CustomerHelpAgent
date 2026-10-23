using MimeKit;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>把 <see cref="EmailMessage"/> 轉成 MIME（HTML ＋ 純文字 multipart/alternative）。</summary>
internal static class MimeMessageFactory
{
    /// <summary>Pickup 模式允許不填寄件者，此時用這個位址 —— 信不會真的寄出。</summary>
    internal const string PickupFallbackFromAddress = "no-reply@localhost";

    public static MimeMessage Create(EmailMessage message, EmailSettings settings, string systemName)
    {
        var fromName = string.IsNullOrWhiteSpace(settings.FromName) ? systemName : settings.FromName;
        var fromAddress = string.IsNullOrWhiteSpace(settings.FromAddress)
            ? PickupFallbackFromAddress
            : settings.FromAddress;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(fromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        return mime;
    }
}
