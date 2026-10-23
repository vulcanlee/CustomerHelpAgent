using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>個人資料頁要顯示的內容（0.9.102 起）。時間一律 UTC。</summary>
public sealed record ProfileInfo(
    int Id,
    string Account,
    string Name,
    string? Email,
    bool IsGoogleAccount,
    bool HasLocalPassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Teams,
    DateTime? PasswordChangedAtUtc,
    DateTime? PasswordExpiresAtUtc,
    bool MustChangePassword,
    string ConcurrencyStamp,
    bool TwoFactorEnabled = false,
    IReadOnlyList<string>? SubordinateTeams = null);

/// <summary>一筆自己的登入或登出紀錄。<c>ClientIp</c>（0.9.123 起）：0.9.117 之前的舊紀錄為 null。</summary>
public sealed record LoginRecord(DateTime OccurredAtUtc, string Action, bool Success, string? ClientIp = null);

/// <summary>
/// 使用者自己的個人資料（0.9.102 起）：讀取、最近的登入紀錄、修改姓名。
///
/// ⚠️ 只做「本人」：每個方法都以呼叫端傳入的目前使用者 Id 為準，登入紀錄以精確的 <c>ActorUserId</c> 篩選（不用帳號比對 ——
/// 帳號刪除後可能被別人重新使用）。Email、管理員、啟用與鎖定一律不在這裡改。
/// </summary>
public class ProfileService
{
    public const int LoginHistorySize = 20;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<ProfileService> logger;

    public ProfileService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPasswordPolicy passwordPolicy,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<ProfileService> logger)
    {
        this.contextFactory = contextFactory;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.passwordPolicy = passwordPolicy;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public async Task<ProfileInfo?> GetAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            logger.LogWarning("Profile requested for a user that does not exist. UserId={UserId}", userId);
            return null;
        }

        // 角色＝額外角色 ∪ 主要角色，經 RoleView 排除已刪除的角色（與權限判斷相同的規則）。
        var roleIds = await context.UserRole.Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).ToListAsync();
        if (user.RoleViewId is { } primary)
        {
            roleIds.Add(primary);
        }

        var roles = await context.RoleView.AsNoTracking().Where(x => roleIds.Contains(x.Id)).Select(x => x.Name).OrderBy(x => x).ToListAsync();
        // 0.9.105 起分開顯示：指派的團隊（直接加入＋角色預設），與因此看得到的下屬部門。
        var teams = (await effectiveTeamResolver.GetAssignedTeamNamesAsync(userId)).Order(StringComparer.Ordinal).ToList();
        var subordinates = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(userId))
            .Where(x => !teams.Contains(x, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        var hasLocalPassword = !string.IsNullOrEmpty(user.Password);

        return new ProfileInfo(
            user.Id,
            user.Account,
            user.Name,
            user.Email,
            string.Equals(user.OAuthProvider, "Google", StringComparison.OrdinalIgnoreCase),
            hasLocalPassword,
            roles,
            teams,
            user.PasswordChangedAtUtc,
            passwordPolicy.GetExpiresAtUtc(user.Account, hasLocalPassword, user.PasswordChangedAtUtc),
            user.MustChangePassword,
            user.ConcurrencyStamp,
            user.TwoFactorEnabled,
            subordinates);
    }

    /// <summary>自己最近 <see cref="LoginHistorySize"/> 筆登入與登出紀錄（新到舊）。</summary>
    public async Task<List<LoginRecord>> GetRecentLoginsAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId == userId && (x.Action.StartsWith("Login.") || x.Action == AuditActions.Logout))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(LoginHistorySize)
            .Select(x => new LoginRecord(x.OccurredAt, x.Action, x.Success, x.ClientIp))
            .ToListAsync();
    }

    /// <summary>
    /// 上一次成功登入（0.9.106 起，首頁「我的帳號」）：密碼或 Google 登入成功的倒數第二筆 —— 最近一筆通常就是這次登入，顯示它沒有意義。
    /// 一樣以精確的 <c>ActorUserId</c> 篩選；只登入過一次時回 null。
    /// </summary>
    public async Task<LoginRecord?> GetPreviousLoginAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId == userId && x.Success && (x.Action == AuditActions.Login.Success || x.Action == AuditActions.Login.SsoSuccess))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip(1)
            .Select(x => new LoginRecord(x.OccurredAt, x.Action, x.Success, x.ClientIp))
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 修改自己的姓名。只寫 <c>Name</c>（與更新時間）；以開啟頁面時的版本號比對，管理員同時修改就回衝突。成功後通知右上角更新。
    /// </summary>
    public async Task<VerifyRecordResult> UpdateNameAsync(int userId, string? name, string? expectedStamp)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return VerifyRecordResultFactory.Build(false, "姓名不可空白。");
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            logger.LogWarning("Profile update rejected because the user does not exist. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        var diff = new AuditDiff().Value(nameof(MyUser.Name), user.Name, trimmed).Render($"使用者 #{user.Id}（{user.Account}）修改個人資料");
        user.Name = trimmed;
        user.UpdateAt = DateTime.Now;
        var entry = context.Entry(user);
        ConcurrencyStampHelper.Apply(entry, expectedStamp);
        SoftDeleteHelper.ProtectFlags(entry);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Profile update rejected because the record changed. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        // 姓名是個資：資料庫摘要與日誌只記「改了姓名」；舊值→新值只寫進稽核的詳細內容檔（0.9.131 起，僅管理員可看）。
        await auditLogService.WriteAsync(
            AuditActions.User.ProfileUpdate, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: nameof(MyUser), targetId: user.Id.ToString(), detail: "field=Name", content: diff);
        logger.LogInformation("Profile name updated. UserId={UserId}", userId);

        // 右上角的姓名與縮寫立即更新（MainLayout 聽 CurrentUserService.Changed）。
        if (currentUserService.CurrentUser.Id == userId)
        {
            currentUserService.CurrentUser.Name = trimmed;
            currentUserService.NotifyChanged();
        }

        return VerifyRecordResultFactory.Build(true);
    }
}
