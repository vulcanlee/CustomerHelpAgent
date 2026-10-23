using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

public enum AiQuotaScope
{
    Global,
    User,
}

public enum AiQuotaPeriod
{
    Daily,
    Monthly,
}

/// <summary>一個上限目前的狀態。時間為伺服器本地時間。</summary>
public sealed record AiQuotaLimitStatus(AiQuotaScope Scope, AiQuotaPeriod Period, int LimitTwd, double UsedTwd, DateTime PeriodStart, DateTime ResetAt)
{
    public bool IsLimited => LimitTwd > 0;

    /// <summary>已用金額達到（含等於）上限。</summary>
    public bool IsReached => IsLimited && UsedTwd >= LimitTwd;

    public double Ratio => IsLimited ? UsedTwd / LimitTwd : 0;

    /// <summary>「全系統每日」「每人每月」。</summary>
    public string Label => (Scope == AiQuotaScope.Global ? "全系統" : "每人") + (Period == AiQuotaPeriod.Daily ? "每日" : "每月");

    /// <summary>「今日」或「本月」。</summary>
    public string PeriodLabel => Period == AiQuotaPeriod.Daily ? "今日" : "本月";
}

/// <summary>AI 用量上限（0.9.109 起）：送出前檢查、記帳後提醒、畫面顯示。</summary>
public interface IAiQuotaService
{
    /// <summary>
    /// 送出 AI 呼叫前檢查。已達到的上限中，回傳最晚才重置的那一個（它才是真正的限制）；都沒有就回 null。
    /// <paramref name="userId"/> 為 null 時只看全系統。⚠️ 不丟例外：讀不到資料庫時寫錯誤日誌並放行。
    /// </summary>
    Task<AiQuotaLimitStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default);

    /// <summary>全系統兩個上限（與 <paramref name="userId"/> 有值時的個人兩個上限）目前的狀態，不論是否設定上限。</summary>
    Task<IReadOnlyList<AiQuotaLimitStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 記帳後評估：達 80%、100% 各通知一次（每人上限通知本人，全系統上限通知全體管理員，全系統達 100% 另寄信）。
    /// ⚠️ 不丟例外。
    /// </summary>
    Task NotifyIfReachedAsync(int? userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiQuotaService" />
public sealed class AiQuotaService : IAiQuotaService
{
    internal const double WarningRatio = 0.8;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IOptionsMonitor<AiQuotaSettings> optionsMonitor;
    private readonly INotificationSender notificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AiQuotaService> logger;

    public AiQuotaService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IOptionsMonitor<AiQuotaSettings> optionsMonitor,
        INotificationSender notificationSender,
        TimeProvider timeProvider,
        ILogger<AiQuotaService> logger)
    {
        this.contextFactory = contextFactory;
        this.optionsMonitor = optionsMonitor;
        this.notificationSender = notificationSender;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<AiQuotaLimitStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var reached = (await BuildAsync(userId, onlyLimited: true, cancellationToken)).Where(x => x.IsReached).ToList();
            return reached.OrderByDescending(x => x.ResetAt).ThenBy(x => x.Scope == AiQuotaScope.User ? 0 : 1).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 用量讀不到時放行：配額是費用控管，不該因為它讓 AI 分析整個停擺（用量記錄同樣會失敗並留下日誌）。
            logger.LogError(ex, "Failed to check AI usage quota; the call is allowed.");
            return null;
        }
    }

    public async Task<IReadOnlyList<AiQuotaLimitStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default)
        => await BuildAsync(userId, onlyLimited: false, cancellationToken);

    public async Task NotifyIfReachedAsync(int? userId, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var status in await BuildAsync(userId, onlyLimited: true, cancellationToken))
            {
                var threshold = status.Ratio >= 1 ? 100 : status.Ratio >= WarningRatio ? 80 : 0;
                if (threshold == 0)
                {
                    continue;
                }

                var isGlobal = status.Scope == AiQuotaScope.Global;
                var scopeKey = isGlobal ? "global" : $"user:{userId}";
                var periodKey = status.Period == AiQuotaPeriod.Daily
                    ? $"daily:{status.PeriodStart:yyyy-MM-dd}"
                    : $"monthly:{status.PeriodStart:yyyy-MM}";
                var title = threshold == 100 ? $"AI 用量已達上限：{status.Label}" : $"AI 用量已達上限的 80%：{status.Label}";
                var body = $"{status.PeriodLabel}已用 NT$ {Money(status.UsedTwd)}／上限 NT$ {Money(status.LimitTwd)}。"
                    + $"達到上限後{(isGlobal ? "所有人的" : "你的")} AI 分析會暫停，{status.ResetAt:yyyy-MM-dd HH:mm} 重置。";
                await notificationSender.SendAsync(
                    new NotificationRequest(
                        NotificationCategories.AiQuota,
                        title,
                        body,
                        isGlobal ? "/token-usage" : null,
                        isGlobal ? NotificationTarget.AllAdmins() : NotificationTarget.Users(userId!.Value),
                        AlsoEmail: isGlobal && threshold == 100,
                        SourceKey: $"AiQuota:{scopeKey}:{periodKey}:{threshold}"),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to evaluate AI usage quota notifications.");
        }
    }

    /// <summary>被擋下時給使用者看的訊息。</summary>
    public static string BlockedMessage(AiQuotaLimitStatus status)
        => $"已達{status.Label} AI 用量上限（NT$ {Money(status.LimitTwd)}，{status.PeriodLabel}已用 NT$ {Money(status.UsedTwd)}），"
            + $"{status.ResetAt:yyyy-MM-dd HH:mm} 重置。需要提高上限請洽系統管理員（系統參數「AI 用量」）。";

    public static string Money(double value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    private async Task<List<AiQuotaLimitStatus>> BuildAsync(int? userId, bool onlyLimited, CancellationToken cancellationToken)
    {
        var settings = optionsMonitor.CurrentValue;
        var now = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone).DateTime;
        var dayStart = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1);

        var candidates = new List<(AiQuotaScope Scope, AiQuotaPeriod Period, int Limit)>
        {
            (AiQuotaScope.Global, AiQuotaPeriod.Daily, settings.GlobalDailyTwd),
            (AiQuotaScope.Global, AiQuotaPeriod.Monthly, settings.GlobalMonthlyTwd),
        };
        if (userId is not null)
        {
            candidates.Add((AiQuotaScope.User, AiQuotaPeriod.Daily, settings.PerUserDailyTwd));
            candidates.Add((AiQuotaScope.User, AiQuotaPeriod.Monthly, settings.PerUserMonthlyTwd));
        }

        var result = new List<AiQuotaLimitStatus>();
        var wanted = candidates.Where(x => !onlyLimited || x.Limit > 0).ToList();
        if (wanted.Count == 0)
        {
            return result;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var (scope, period, limit) in wanted)
        {
            var start = period == AiQuotaPeriod.Daily ? dayStart : monthStart;
            var reset = period == AiQuotaPeriod.Daily ? dayStart.AddDays(1) : monthStart.AddMonths(1);

            // Token 用量的 OccurredAt 存的是伺服器本地時間；未定價（CostTwd 為空）以 0 計。
            var rows = context.TokenUsageLog.Where(x => x.OccurredAt >= start && x.OccurredAt < reset);
            if (scope == AiQuotaScope.User)
            {
                rows = rows.Where(x => x.UserId == userId);
            }

            var used = await rows.SumAsync(x => x.CostTwd ?? 0, cancellationToken);
            result.Add(new AiQuotaLimitStatus(scope, period, limit, used, start, reset));
        }

        return result;
    }
}
