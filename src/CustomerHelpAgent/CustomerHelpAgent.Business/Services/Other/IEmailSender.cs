using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 寄出一封信。實作依 <c>EmailSettings:Provider</c> 決定（None／Pickup／Smtp），
/// 在 Web 層註冊 —— Business 只依賴這個介面，不引用 MailKit。
///
/// ⚠️ 失敗一律**拋例外**，由呼叫端決定要顯示給使用者（測試寄信）還是只記錄（背景佇列）。
/// 需要「入列即返回」的情境請改用 <see cref="IEmailQueue"/>。
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
