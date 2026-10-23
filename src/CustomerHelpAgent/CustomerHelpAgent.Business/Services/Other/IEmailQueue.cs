using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 把信交給背景寄送，立即返回。
///
/// <para>為什麼不直接呼叫 <see cref="IEmailSender"/>：匿名流程（忘記密碼）若同步寄信，
/// 「帳號存在」會比「帳號不存在」慢上數秒，回應時間本身就洩漏了帳號是否存在。</para>
///
/// <para>⚠️ 佇列只在記憶體：程式重啟時尚未寄出的信會遺失，寄送失敗也不重試
/// （只記錄錯誤）。使用者重新申請即可，這是刻意的取捨。</para>
/// </summary>
public interface IEmailQueue
{
    /// <summary>成功入列回傳 true；佇列已滿回傳 false（信不會寄出）。</summary>
    bool TryEnqueue(EmailMessage message);
}
