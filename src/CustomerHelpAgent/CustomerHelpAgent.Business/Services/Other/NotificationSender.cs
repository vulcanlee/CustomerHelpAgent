using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>通知的對象（可組合）。</summary>
public sealed class NotificationTarget
{
    private NotificationTarget(IReadOnlyList<int> userIds, IReadOnlyList<int> roleIds, IReadOnlyList<int> teamIds, bool admins)
    {
        UserIds = userIds;
        RoleIds = roleIds;
        TeamIds = teamIds;
        Admins = admins;
    }

    public IReadOnlyList<int> UserIds { get; }

    public IReadOnlyList<int> RoleIds { get; }

    public IReadOnlyList<int> TeamIds { get; }

    /// <summary>所有啟用中、勾選「管理員」的帳號。</summary>
    public bool Admins { get; }

    public static NotificationTarget Users(params int[] userIds) => new(userIds, [], [], false);

    public static NotificationTarget Role(int roleId) => new([], [roleId], [], false);

    public static NotificationTarget Team(int teamId) => new([], [], [teamId], false);

    public static NotificationTarget AllAdmins() => new([], [], [], true);

    public static NotificationTarget Union(params NotificationTarget[] targets)
        => new(
            targets.SelectMany(x => x.UserIds).Distinct().ToList(),
            targets.SelectMany(x => x.RoleIds).Distinct().ToList(),
            targets.SelectMany(x => x.TeamIds).Distinct().ToList(),
            targets.Any(x => x.Admins));
}

/// <summary>
/// 一則通知。內容一律是純文字；<paramref name="Link"/> 是站內相對網址。
/// <paramref name="SourceKey"/>：同一位收件人發過相同鍵就不再發（例如 <c>AccountPending:12</c>）；0.9.136 起看 <c>NotificationDispatch</c>，通知被刪除也不會重發。
/// <paramref name="AlsoEmail"/>：同時寄信（沒有 Email、格式不合或寄信停用的收件人只收站內通知）。
/// </summary>
public sealed record NotificationRequest(
    string Category,
    string Title,
    string? Body,
    string? Link,
    NotificationTarget Target,
    bool AlsoEmail = false,
    string? SourceKey = null);

public sealed record NotificationSendResult(int Recipients, int Emailed, bool Failed = false);

/// <summary>發送站內通知（0.9.100 起）。業務模組與內建事件都經過它。</summary>
public interface INotificationSender
{
    /// <summary>⚠️ 絕不丟例外：通知失敗不可中斷呼叫端的流程（登入、排程作業…），失敗時記錯誤並回 <c>Failed</c>。</summary>
    Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>寄出通知信（實作在 Web，因為要用寄信佇列與網址設定）。</summary>
public interface INotificationMailer
{
    /// <summary>回傳實際放進寄信佇列的封數。</summary>
    int Send(IReadOnlyList<string> emails, string title, string? body, string? link);
}

/// <summary>
/// 站內通知的發送（0.9.100 起）：展開收件人 → 去重 → 一位收件人寫一列 → 發出即時訊號 →（可選）寄信。
///
/// 收件人只算啟用、未刪除的帳號（軟刪除過濾器自動排除已刪除者）。角色＝<c>UserRole</c> ∪ 主要角色，已刪除的角色沒有收件人；
/// 團隊＝<see cref="IEffectiveTeamResolver.GetUserIdsInTeamAsync"/>（與列級權控同一個定義）。
/// ⚠️ 不可在例外記錄管線內呼叫（例外告警服務、例外寫入器）：發送失敗會記錯誤，形成遞迴。
/// </summary>
public sealed class NotificationSender : INotificationSender
{
    internal const int MaxTitleLength = 100;
    internal const int MaxBodyLength = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IEffectiveTeamResolver teamResolver;
    private readonly INotificationSignal signal;
    private readonly INotificationMailer mailer;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationSender> logger;

    public NotificationSender(
        IDbContextFactory<BackendDBContext> contextFactory,
        IEffectiveTeamResolver teamResolver,
        INotificationSignal signal,
        INotificationMailer mailer,
        TimeProvider timeProvider,
        ILogger<NotificationSender> logger)
    {
        this.contextFactory = contextFactory;
        this.teamResolver = teamResolver;
        this.signal = signal;
        this.mailer = mailer;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var recipients = await ResolveRecipientsAsync(context, request.Target, cancellationToken);
            if (request.SourceKey is { } sourceKey && recipients.Count > 0)
            {
                var already = await context.NotificationDispatch
                    .Where(x => x.SourceKey == sourceKey && recipients.Contains(x.RecipientUserId))
                    .Select(x => x.RecipientUserId)
                    .ToListAsync(cancellationToken);
                recipients.ExceptWith(already);
            }

            if (recipients.Count == 0)
            {
                return new(0, 0);
            }

            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var userId in recipients)
            {
                context.Notification.Add(new Notification
                {
                    RecipientUserId = userId,
                    Category = Truncate(request.Category, 32)!,
                    Title = Truncate(request.Title, MaxTitleLength)!,
                    Body = Truncate(request.Body, MaxBodyLength),
                    Link = Truncate(request.Link, 200),
                    SourceKey = request.SourceKey,
                    CreatedAtUtc = nowUtc,
                });

                if (request.SourceKey is { } key)
                {
                    context.NotificationDispatch.Add(new NotificationDispatch { SourceKey = key, RecipientUserId = userId, SentAtUtc = nowUtc });
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            signal.Publish(recipients);

            var emailed = 0;
            if (request.AlsoEmail)
            {
                var emails = await context.MyUser
                    .Where(x => recipients.Contains(x.Id) && x.Email != null && x.Email != string.Empty)
                    .Select(x => x.Email!)
                    .ToListAsync(cancellationToken);
                emailed = mailer.Send(emails, request.Title, request.Body, request.Link);
            }

            logger.LogInformation(
                "Notification sent. Category={Category}, Recipients={Recipients}, Queued={Queued}",
                request.Category, recipients.Count, emailed);
            return new(recipients.Count, emailed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send notification. Category={Category}", request.Category);
            return new(0, 0, Failed: true);
        }
    }

    private async Task<HashSet<int>> ResolveRecipientsAsync(BackendDBContext context, NotificationTarget target, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<int>(target.UserIds);

        if (target.Admins)
        {
            candidates.UnionWith(await context.MyUser.Where(x => x.IsAdmin && x.Status).Select(x => x.Id).ToListAsync(cancellationToken));
        }

        foreach (var roleId in target.RoleIds)
        {
            // 經 context.RoleView 確認角色未刪除（速查表：權限與角色判斷一律經它過濾）。
            if (!await context.RoleView.AnyAsync(x => x.Id == roleId, cancellationToken))
            {
                continue;
            }

            candidates.UnionWith(await context.UserRole.Where(x => x.RoleViewId == roleId).Select(x => x.MyUserId).ToListAsync(cancellationToken));
            candidates.UnionWith(await context.MyUser.Where(x => x.RoleViewId == roleId).Select(x => x.Id).ToListAsync(cancellationToken));
        }

        foreach (var teamId in target.TeamIds)
        {
            var teamName = await context.Team.Where(x => x.Id == teamId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken);
            if (teamName is not null)
            {
                candidates.UnionWith(await teamResolver.GetUserIdsInTeamAsync(teamName));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // 只留啟用、未刪除的帳號（軟刪除過濾器會排除已刪除者）。
        var ids = candidates.ToList();
        return (await context.MyUser.Where(x => ids.Contains(x.Id) && x.Status).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
