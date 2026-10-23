using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Diagnostics;

/// <summary>
/// 系統例外的 Email 告警（0.9.78 起，LOG-12）。由 <see cref="ExceptionLogWriter"/> 在每筆例外寫入後呼叫，
/// 啟動時補登（補登檔）也會呼叫。
///
/// 觸發與節流規則見 <see cref="ExceptionAlertSettings"/>。狀態（暴增計數、冷卻、每小時配額）只存在記憶體，
/// 重啟即歸零 —— 單機設計，與例外合併計數同一個前提。
///
/// ⚠️ <b>本類別絕不可使用 <see cref="ILogger"/>。</b>它在例外記錄管線之內被呼叫：寄信失敗若經 ILogger 記成 Error，
/// 會再被收成一筆例外、再觸發告警。留話一律走 NLog 的 InternalLogger，且任何錯誤都不得拋回寫入器。
/// 同理，寄信背景作業自己的例外（頁面 <c>EmailDispatch</c>）不觸發告警，避免 SMTP 掛掉時自己告自己。
/// </summary>
public sealed class ExceptionAlertService
{
    /// <summary>寄信背景作業在例外情境中的頁面名稱（見 EmailDispatchWorker）。</summary>
    private const string EmailDispatchPage = "EmailDispatch";

    private static readonly TimeSpan GlobalWindow = TimeSpan.FromHours(1);

    private readonly IOptionsMonitor<ExceptionAlertSettings> alertOptions;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly IEmailQueue emailQueue;
    private readonly TimeProvider timeProvider;
    private readonly LoggingPipelineMonitor? monitor;

    private readonly object gate = new();
    private readonly Dictionary<int, Queue<DateTimeOffset>> occurrences = [];
    private readonly Dictionary<int, DateTimeOffset> lastSentBySignature = [];
    private readonly Queue<DateTimeOffset> sentWithinHour = new();
    private int suppressedCount;

    public ExceptionAlertService(
        IOptionsMonitor<ExceptionAlertSettings> alertOptions,
        IOptionsMonitor<EmailSettings> emailOptions,
        ISystemIdentity systemIdentity,
        IEmailQueue emailQueue,
        TimeProvider timeProvider,
        LoggingPipelineMonitor? monitor = null)
    {
        this.monitor = monitor;
        this.alertOptions = alertOptions;
        this.emailOptions = emailOptions;
        this.systemIdentity = systemIdentity;
        this.emailQueue = emailQueue;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// 依剛寫入的例外決定要不要寄告警。絕不拋出。
    /// </summary>
    /// <returns>這次是否送出告警（供測試斷言）。</returns>
    public bool Evaluate(ExceptionRecordOutcome? outcome)
    {
        try
        {
            return EvaluateCore(outcome);
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Warn(ex, "Exception alert evaluation failed.");
            return false;
        }
    }

    private bool EvaluateCore(ExceptionRecordOutcome? outcome)
    {
        var settings = alertOptions.CurrentValue;
        if (outcome is null || settings.IsEnabled == false || outcome.IsOverflow
            || string.Equals(outcome.Page, EmailDispatchPage, StringComparison.Ordinal))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        lock (gate)
        {
            var reason = DetermineReason(outcome, settings, now);
            if (reason is null)
            {
                return false;
            }

            // 第一層：同一簽章冷卻中就不再寄（暴增時每次都會達標，靠這層擋住）。
            if (lastSentBySignature.TryGetValue(outcome.Id, out var lastSent)
                && now - lastSent < TimeSpan.FromMinutes(settings.PerSignatureCooldownMinutes))
            {
                return false;
            }

            // 第二層：全系統每小時上限。超過就不寄，筆數留到下一封告知。
            while (sentWithinHour.Count > 0 && now - sentWithinHour.Peek() >= GlobalWindow)
            {
                sentWithinHour.Dequeue();
            }

            if (sentWithinHour.Count >= settings.MaxEmailsPerHour)
            {
                suppressedCount++;
                return false;
            }

            Send(outcome, reason, settings);

            lastSentBySignature[outcome.Id] = now;
            sentWithinHour.Enqueue(now);
            suppressedCount = 0;
            return true;
        }
    }

    private string? DetermineReason(ExceptionRecordOutcome outcome, ExceptionAlertSettings settings, DateTimeOffset now)
    {
        // 暴增視窗：每次發生都要記，才算得出「N 分鐘內幾次」。
        if (occurrences.TryGetValue(outcome.Id, out var window) == false)
        {
            window = new Queue<DateTimeOffset>();
            occurrences[outcome.Id] = window;
        }

        window.Enqueue(now);
        var windowLength = TimeSpan.FromMinutes(settings.BurstWindowMinutes);
        while (window.Count > 0 && now - window.Peek() > windowLength)
        {
            window.Dequeue();
        }

        if (outcome.IsCritical)
        {
            return "Critical（應用程式無法繼續運作）";
        }

        if (outcome.IsNew)
        {
            return "新的例外類型第一次發生";
        }

        if (window.Count >= settings.BurstThreshold)
        {
            return $"{settings.BurstWindowMinutes} 分鐘內發生 {window.Count} 次";
        }

        return null;
    }

    private void Send(ExceptionRecordOutcome outcome, string reason, ExceptionAlertSettings settings)
    {
        var baseUrl = emailOptions.CurrentValue.PublicBaseUrl?.Trim().TrimEnd('/');
        var content = new ExceptionAlertContent(
            reason,
            outcome.ExceptionType,
            outcome.Source,
            outcome.Page,
            outcome.OccurrenceCount,
            outcome.FirstOccurredAt,
            outcome.LastOccurredAt,
            outcome.TraceId,
            string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/system-exceptions",
            suppressedCount);

        var systemName = systemIdentity.Name;
        foreach (var recipient in settings.Recipients.Where(x => string.IsNullOrWhiteSpace(x) == false).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 寄信佇列滿時 TryEnqueue 回 false，ChannelEmailQueue 自己會記 Warning；這裡不重試。
            if (emailQueue.TryEnqueue(EmailTemplates.BuildExceptionAlert(recipient.Trim(), systemName, content)) == false)
            {
                NLog.Common.InternalLogger.Warn("Exception alert email was not queued because the email queue is full.");
                monitor?.RecordAlertQueueFailure();
            }
        }
    }
}
