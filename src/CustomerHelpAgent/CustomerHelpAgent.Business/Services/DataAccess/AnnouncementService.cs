using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>公告橫幅要顯示的一則公告（0.9.100 起）：對象已解析，團隊以名稱比對使用者的有效團隊。</summary>
public sealed record ActiveAnnouncement(int Id, string Title, string Content, DateTime StartAtUtc, DateTime? EndAtUtc, string TargetKind, int? TargetId, string? TeamName)
{
    /// <summary>現在是否在顯示期間內（開始含、結束不含）。</summary>
    public bool IsShowingAt(DateTime nowUtc) => StartAtUtc <= nowUtc && (EndAtUtc is null || nowUtc < EndAtUtc);

    /// <summary>這位使用者是否為對象。</summary>
    public bool IsFor(IReadOnlyCollection<int> roleIds, IReadOnlyCollection<string> teamNames) => TargetKind switch
    {
        AnnouncementTargetKinds.All => true,
        AnnouncementTargetKinds.Role => TargetId is { } roleId && roleIds.Contains(roleId),
        AnnouncementTargetKinds.Team => TeamName is { } team && teamNames.Contains(team, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };
}

/// <summary>
/// 公告的管理、顯示與個人關閉（0.9.100 起）。
/// 時間：畫面用伺服器本地時間（<see cref="AnnouncementAdapterModel"/>），資料庫存 UTC。
/// 版本號以條件式 UPDATE 比對（不實作 <c>IConcurrencyStamped</c>）。
/// </summary>
public class AnnouncementService
{
    private const int SqliteConstraintErrorCode = 19;
    internal const int MaxTitleLength = 100;
    internal const int MaxContentLength = 2000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AnnouncementService> logger;

    public AnnouncementService(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<AnnouncementService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>管理頁清單（開始時間新到舊），附上對象名稱。</summary>
    public async Task<List<AnnouncementAdapterModel>> GetAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.Announcement.AsNoTracking()
            .OrderByDescending(x => x.StartAtUtc).ThenByDescending(x => x.Id)
            .ToListAsync();
        var roles = await context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
            .Select(x => new { x.Id, x.Name, x.IsDeleted }).ToDictionaryAsync(x => x.Id);
        var teams = await context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
            .Select(x => new { x.Id, x.Name, x.IsDeleted }).ToDictionaryAsync(x => x.Id);

        return rows.Select(x => new AnnouncementAdapterModel
        {
            Id = x.Id,
            Title = x.Title,
            Content = x.Content,
            StartAt = ToLocal(x.StartAtUtc),
            EndAt = x.EndAtUtc is { } end ? ToLocal(end) : null,
            TargetKind = x.TargetKind,
            TargetId = x.TargetId,
            TargetName = x.TargetKind switch
            {
                AnnouncementTargetKinds.Role => x.TargetId is { } r && roles.TryGetValue(r, out var role)
                    ? role.Name + (role.IsDeleted ? "（已刪除）" : string.Empty) : "（已刪除）",
                AnnouncementTargetKinds.Team => x.TargetId is { } t && teams.TryGetValue(t, out var team)
                    ? team.Name + (team.IsDeleted ? "（已刪除）" : string.Empty) : "（已刪除）",
                _ => "全體",
            },
            CreatedByAccount = x.CreatedByAccount,
            UpdatedAt = ToLocal(x.UpdatedAtUtc),
            ConcurrencyStamp = x.ConcurrencyStamp,
        }).ToList();
    }

    /// <summary>還沒結束的公告（含尚未開始的），給橫幅快取用；團隊對象附上目前的團隊名稱。</summary>
    public async Task<List<ActiveAnnouncement>> GetCurrentAndUpcomingAsync()
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.Announcement.AsNoTracking()
            .Where(x => x.EndAtUtc == null || x.EndAtUtc > nowUtc)
            .OrderByDescending(x => x.StartAtUtc).ThenByDescending(x => x.Id)
            .ToListAsync();
        var teamIds = rows.Where(x => x.TargetKind == AnnouncementTargetKinds.Team && x.TargetId != null).Select(x => x.TargetId!.Value).ToList();
        var teamNames = await context.Team.AsNoTracking().Where(x => teamIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name);

        return rows.Select(x => new ActiveAnnouncement(
            x.Id, x.Title, x.Content, x.StartAtUtc, x.EndAtUtc, x.TargetKind, x.TargetId,
            x.TargetId is { } id && teamNames.TryGetValue(id, out var name) ? name : null)).ToList();
    }

    public async Task<VerifyRecordResult> AddAsync(AnnouncementAdapterModel model, string? account)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var error = await ValidateAsync(context, model);
        if (error is not null)
        {
            return VerifyRecordResultFactory.Build(false, error);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Announcement
        {
            Title = model.Title.Trim(),
            Content = model.Content.Trim(),
            StartAtUtc = ToUtc(model.StartAt),
            EndAtUtc = model.EndAt is { } end ? ToUtc(end) : null,
            TargetKind = model.TargetKind,
            TargetId = model.TargetKind == AnnouncementTargetKinds.All ? null : model.TargetId,
            CreatedByAccount = account,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            ConcurrencyStamp = ConcurrencyStampHelper.New(),
        };
        context.Announcement.Add(entity);
        await context.SaveChangesAsync();
        model.Id = entity.Id;
        logger.LogInformation("Announcement created. AnnouncementId={AnnouncementId}, TargetKind={TargetKind}", entity.Id, entity.TargetKind);
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> UpdateAsync(AnnouncementAdapterModel model)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var error = await ValidateAsync(context, model);
        if (error is not null)
        {
            return VerifyRecordResultFactory.Build(false, error);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var title = model.Title.Trim();
        var content = model.Content.Trim();
        var start = ToUtc(model.StartAt);
        DateTime? end = model.EndAt is { } e ? ToUtc(e) : null;
        var targetId = model.TargetKind == AnnouncementTargetKinds.All ? null : model.TargetId;
        var newStamp = ConcurrencyStampHelper.New();
        var affected = await context.Announcement
            .Where(x => x.Id == model.Id && x.ConcurrencyStamp == model.ConcurrencyStamp)
            .ExecuteUpdateAsync(x => x
                .SetProperty(a => a.Title, title)
                .SetProperty(a => a.Content, content)
                .SetProperty(a => a.StartAtUtc, start)
                .SetProperty(a => a.EndAtUtc, end)
                .SetProperty(a => a.TargetKind, model.TargetKind)
                .SetProperty(a => a.TargetId, targetId)
                .SetProperty(a => a.UpdatedAtUtc, nowUtc)
                .SetProperty(a => a.ConcurrencyStamp, newStamp));
        if (affected == 0)
        {
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        model.ConcurrencyStamp = newStamp;
        logger.LogInformation("Announcement updated. AnnouncementId={AnnouncementId}", model.Id);
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id, string expectedStamp)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var affected = await context.Announcement.Where(x => x.Id == id && x.ConcurrencyStamp == expectedStamp).ExecuteDeleteAsync();
        if (affected == 0)
        {
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        logger.LogInformation("Announcement deleted. AnnouncementId={AnnouncementId}", id);
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>使用者關閉一則公告（之後不再對他顯示）。重複關閉視為成功。</summary>
    public async Task DismissAsync(int userId, int announcementId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        if (await context.AnnouncementDismissal.AnyAsync(x => x.AnnouncementId == announcementId && x.MyUserId == userId))
        {
            return;
        }

        context.AnnouncementDismissal.Add(new AnnouncementDismissal { AnnouncementId = announcementId, MyUserId = userId, DismissedAtUtc = timeProvider.GetUtcNow().UtcDateTime });
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
        {
            // 同一個人在兩個分頁同時關閉，或公告剛好被刪除：都不需要處理。
            logger.LogDebug("Announcement dismissal was not saved. AnnouncementId={AnnouncementId}", announcementId);
        }
    }

    public async Task<HashSet<int>> GetDismissedIdsAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return (await context.AnnouncementDismissal.Where(x => x.MyUserId == userId).Select(x => x.AnnouncementId).ToListAsync()).ToHashSet();
    }

    /// <summary>使用者的角色（額外角色 ∪ 主要角色），經 <c>context.RoleView</c> 排除已刪除的角色。</summary>
    public async Task<HashSet<int>> GetUserRoleIdsAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var ids = await context.UserRole.Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).ToListAsync();
        var primary = await context.MyUser.Where(x => x.Id == userId).Select(x => x.RoleViewId).FirstOrDefaultAsync();
        if (primary is { } p)
        {
            ids.Add(p);
        }

        return (await context.RoleView.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync()).ToHashSet();
    }

    /// <summary>對象下拉選單：未刪除的角色與團隊（名稱排序）。</summary>
    public async Task<(List<(int Id, string Name)> Roles, List<(int Id, string Name)> Teams)> GetTargetOptionsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var roles = await context.RoleView.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync();
        var teams = await context.Team.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync();
        return (roles.Select(x => (x.Id, x.Name)).ToList(), teams.Select(x => (x.Id, x.Name)).ToList());
    }

    private async Task<string?> ValidateAsync(BackendDBContext context, AnnouncementAdapterModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Title) || model.Title.Trim().Length > MaxTitleLength)
        {
            return $"標題不可留空，最多 {MaxTitleLength} 個字。";
        }

        if (string.IsNullOrWhiteSpace(model.Content) || model.Content.Trim().Length > MaxContentLength)
        {
            return $"內容不可留空，最多 {MaxContentLength} 個字。";
        }

        if (model.EndAt is { } end && end <= model.StartAt)
        {
            return "結束時間必須晚於開始時間。";
        }

        switch (model.TargetKind)
        {
            case AnnouncementTargetKinds.All:
                return null;
            case AnnouncementTargetKinds.Role:
                return model.TargetId is { } roleId && await context.RoleView.AnyAsync(x => x.Id == roleId) ? null : "請選擇對象角色。";
            case AnnouncementTargetKinds.Team:
                return model.TargetId is { } teamId && await context.Team.AnyAsync(x => x.Id == teamId) ? null : "請選擇對象團隊。";
            default:
                return "對象種類不正確。";
        }
    }

    private DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeProvider.LocalTimeZone);

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone);
}
