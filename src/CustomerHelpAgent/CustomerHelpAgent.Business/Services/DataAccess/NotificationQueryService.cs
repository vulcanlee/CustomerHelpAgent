using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 鈴鐺的查詢、標為已讀與刪除（0.9.100 起；刪除 0.9.125 起）。
/// ⚠️ <c>userId</c> 一律由呼叫端從 <c>CurrentUserService</c> 取得，每一條 UPDATE／DELETE 都帶 <c>RecipientUserId = userId</c>：
/// 不可以讓人把別人的通知標為已讀或刪掉。
/// 0.9.122 起標為已讀與刪除（真的改到資料時）會發出這位使用者的訊號，鈴鐺、首頁「我的通知」與他的其他分頁一起更新。
/// </summary>
public class NotificationQueryService
{
    private const int PurgeBatchSize = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly INotificationSignal signal;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationQueryService> logger;

    public NotificationQueryService(IDbContextFactory<BackendDBContext> contextFactory, INotificationSignal signal, TimeProvider timeProvider, ILogger<NotificationQueryService> logger)
    {
        this.contextFactory = contextFactory;
        this.signal = signal;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification.CountAsync(x => x.RecipientUserId == userId && x.ReadAtUtc == null);
    }

    /// <summary>有沒有任何已讀的通知（0.9.136 起鈴鐺的「清除已讀」據此決定能不能按，不只看下拉顯示的那幾則）。</summary>
    public async Task<bool> HasReadAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification.AnyAsync(x => x.RecipientUserId == userId && x.ReadAtUtc != null);
    }

    /// <summary>最新的幾則（新到舊）。</summary>
    public async Task<List<NotificationAdapterModel>> GetLatestAsync(int userId, int take = 10)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification
            .AsNoTracking()
            .Where(x => x.RecipientUserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new NotificationAdapterModel
            {
                Id = x.Id,
                Category = x.Category,
                Title = x.Title,
                Body = x.Body,
                Link = x.Link,
                CreatedAtUtc = x.CreatedAtUtc,
                ReadAtUtc = x.ReadAtUtc,
            })
            .ToListAsync();
    }

    public async Task MarkReadAsync(int userId, int notificationId)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.Id == notificationId && x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
        if (changed > 0)
        {
            signal.Publish([userId]);
        }
    }

    public async Task<int> MarkAllReadAsync(int userId)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed;
    }

    /// <summary>刪除一則自己的通知（0.9.125 起）；不是自己的或已不存在時不動，回傳 false。</summary>
    public async Task<bool> DeleteAsync(int userId, int notificationId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.Id == notificationId && x.RecipientUserId == userId)
            .ExecuteDeleteAsync();
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed > 0;
    }

    /// <summary>刪除自己所有已讀的通知（0.9.125 起），未讀的不動。</summary>
    public async Task<int> DeleteReadAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.RecipientUserId == userId && x.ReadAtUtc != null)
            .ExecuteDeleteAsync();
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed;
    }

    /// <summary>
    /// 刪除建立時間早於門檻的通知（不論已讀未讀），分批進行，批次之間可取消。
    /// 同一個門檻也清掉去重紀錄 <see cref="NotificationDispatch"/>（0.9.136 起），去重的有效期間與通知保存期限相同；回傳值只算通知。
    /// </summary>
    public async Task<int> PurgeBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var total = await PurgeAsync(CreatedBefore(cutoffUtc), cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dispatches = await context.NotificationDispatch.Where(x => x.SentAtUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
        logger.LogInformation("Notification dispatch records purged. Rows={Rows}", dispatches);
        return total;
    }

    /// <summary>刪除標為已讀的時間早於門檻的通知（0.9.125 起），分批進行，批次之間可取消。</summary>
    public Task<int> PurgeReadBeforeAsync(DateTime readCutoffUtc, CancellationToken cancellationToken)
        => PurgeAsync(ReadBefore(readCutoffUtc), cancellationToken);

    // 清理條件：清理方法與 GetRetentionStatsAsync 共用（「資料保留與清理」頁的「下次會清掉幾筆」）。
    private static Expression<Func<Notification, bool>> CreatedBefore(DateTime cutoffUtc)
        => x => x.CreatedAtUtc < cutoffUtc;

    private static Expression<Func<Notification, bool>> ReadBefore(DateTime readCutoffUtc)
        => x => x.ReadAtUtc != null && x.ReadAtUtc < readCutoffUtc;

    /// <summary>
    /// 全部使用者的通知筆數、最舊一筆（建立時間，UTC）與兩條規則會刪掉的筆數。
    /// 兩條規則可能命中同一則，取聯集不重複計；兩個門檻都是 null（都不自動清理）時 Expired 為 null。
    /// </summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? cutoffUtc, DateTime? readCutoffUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.Notification.CountAsync();
        var oldest = await context.Notification.MinAsync(x => (DateTime?)x.CreatedAtUtc);

        int? expired = (cutoffUtc, readCutoffUtc) switch
        {
            ({ } c, { } r) => await context.Notification.Where(CreatedBefore(c)).Select(x => x.Id)
                .Union(context.Notification.Where(ReadBefore(r)).Select(x => x.Id))
                .CountAsync(),
            ({ } c, null) => await context.Notification.CountAsync(CreatedBefore(c)),
            (null, { } r) => await context.Notification.CountAsync(ReadBefore(r)),
            _ => null,
        };
        return new RetentionStats(total, oldest, expired);
    }

    private async Task<int> PurgeAsync(Expression<Func<Notification, bool>> predicate, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await context.Notification
                .Where(predicate)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(PurgeBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            total += await context.Notification.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        logger.LogInformation("Notifications purged. Rows={Rows}", total);
        return total;
    }
}
