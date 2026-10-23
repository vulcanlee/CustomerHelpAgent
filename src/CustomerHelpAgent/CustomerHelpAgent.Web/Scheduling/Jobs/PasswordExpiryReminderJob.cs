using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 密碼即將到期的提醒（0.9.101 起）：密碼在 <see cref="ReminderDays"/> 天內到期的人，各收到一則站內通知（不寄信）。
/// 到期天數為 0 時不動作；已經到期的不提醒（登入時就會被導去變更）。同一個到期日只通知一次（<c>SourceKey</c>）。
/// 對象與豁免（support、只用 Google 登入）一律交給 <see cref="IPasswordPolicy.GetExpiresAtUtc"/> 判斷，與登入時的規則相同。
/// </summary>
public sealed class PasswordExpiryReminderJob : IScheduledJob
{
    public const string JobName = "PasswordExpiryReminder";
    internal const int ReminderDays = 7;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly INotificationSender notificationSender;
    private readonly IOptionsMonitor<PasswordPolicySettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PasswordExpiryReminderJob> logger;

    public PasswordExpiryReminderJob(
        IDbContextFactory<BackendDBContext> contextFactory,
        IPasswordPolicy passwordPolicy,
        INotificationSender notificationSender,
        IOptionsMonitor<PasswordPolicySettings> options,
        TimeProvider timeProvider,
        ILogger<PasswordExpiryReminderJob> logger)
    {
        this.contextFactory = contextFactory;
        this.passwordPolicy = passwordPolicy;
        this.notificationSender = notificationSender;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        if (options.CurrentValue.ExpiryDays <= 0)
        {
            return ScheduledJobResult.Success("密碼有效天數設為 0（不過期），不需要提醒。");
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        List<(int Id, string Account, bool HasLocalPassword, DateTime? ChangedAtUtc)> users;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            users = (await db.MyUser.AsNoTracking()
                    .Where(x => x.Status && x.PasswordChangedAtUtc != null)
                    .Select(x => new { x.Id, x.Account, HasLocalPassword = x.Password != string.Empty, x.PasswordChangedAtUtc })
                    .ToListAsync(cancellationToken))
                .Select(x => (x.Id, x.Account, x.HasLocalPassword, x.PasswordChangedAtUtc))
                .ToList();
        }

        var reminded = 0;
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (passwordPolicy.GetExpiresAtUtc(user.Account, user.HasLocalPassword, user.ChangedAtUtc) is not { } expiresAtUtc
                || expiresAtUtc <= nowUtc
                || expiresAtUtc > nowUtc.AddDays(ReminderDays))
            {
                continue;
            }

            var localExpiry = TimeZoneInfo.ConvertTimeFromUtc(expiresAtUtc, timeProvider.LocalTimeZone);
            var result = await notificationSender.SendAsync(new NotificationRequest(
                NotificationCategories.PasswordExpiring,
                $"密碼將於 {localExpiry:yyyy-MM-dd HH:mm} 到期",
                "到期後登入會被要求先變更密碼。現在就可以到「變更密碼」頁設定新密碼。",
                "/ChangePassword",
                NotificationTarget.Users(user.Id),
                SourceKey: $"PasswordExpiring:{user.Id}:{expiresAtUtc:yyyyMMddHHmm}"));
            reminded += result.Recipients;
        }

        logger.LogInformation("Password expiry reminders sent. Reminded={Reminded}, Candidates={Candidates}", reminded, users.Count);
        return ScheduledJobResult.Success($"提醒 {reminded} 位使用者密碼即將在 {ReminderDays} 天內到期。");
    }
}
