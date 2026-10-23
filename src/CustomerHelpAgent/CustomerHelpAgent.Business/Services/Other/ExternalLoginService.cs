using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// Google 登入查找的結果；<see cref="IsDeleted"/> 為 true 表示命中的是已刪除的使用者，呼叫端必須拒絕登入（0.9.95 起）。
/// </summary>
public sealed record ExternalLoginResult(MyUser User, bool IsDeleted)
{
    /// <summary>
    /// 這次 Google 登入該怎麼處理（0.9.101 起集中在這裡，回呼端只依結果導向）。順序有意義：已刪除 → 待開通（停用）→ 鎖定 → 登入。
    /// ⚠️ 鎖定一定要檢查：之前 Google 登入不看鎖定，密碼被猜到鎖住的帳號可以改用 Google 進來。
    /// </summary>
    public ExternalLoginOutcome Evaluate(DateTime nowUtc)
        => IsDeleted ? ExternalLoginOutcome.Deleted
            : !User.Status ? ExternalLoginOutcome.Pending
            : User.LockoutEndUtc is { } lockoutEndUtc && lockoutEndUtc > nowUtc ? ExternalLoginOutcome.Locked
            : ExternalLoginOutcome.SignIn;
}

public enum ExternalLoginOutcome
{
    SignIn,
    Deleted,
    Pending,
    Locked,
}

/// <summary>
/// 處理第三方（Google）登入時的帳號查找、連結與自動建立。
/// </summary>
public class ExternalLoginService
{
    private readonly BackendDBContext context;
    private readonly ILogger<ExternalLoginService> logger;
    private readonly IAuditLogService auditLogService;
    private readonly INotificationSender notificationService;

    public ExternalLoginService(BackendDBContext context, ILogger<ExternalLoginService> logger, IAuditLogService auditLogService, INotificationSender notificationService)
    {
        this.context = context;
        this.logger = logger;
        this.auditLogService = auditLogService;
        this.notificationService = notificationService;
    }

    /// <summary>
    /// 依 Google 身分查找或建立本地使用者（0.9.95 起的比對順序）：
    /// 1) 有效的 GoogleId；2) 已刪除的 GoogleId → 拒絕；3) 有效的 Email → 連結；4) 已刪除的 Email → 拒絕；5) 自動建立停用中的新帳號。
    ///
    /// GoogleId 是 Google 給的穩定識別，排在 Email 前面：否則已刪除使用者的 Google 帳號會被連結到另一位同 Email 的使用者，
    /// 之後那位已刪除的使用者就因 GoogleId 衝突而無法還原。命中已刪除的資料時不寫入、不連結、不新建 ——
    /// 全域過濾器看不到已刪除的使用者，不特別處理的話會建出一個重複的新帳號。
    /// </summary>
    public async Task<ExternalLoginResult> FindOrCreateAsync(
        string provider,
        string googleSubject,
        string email,
        string displayName,
        string defaultRoleName)
    {
        // 不記 Email 與 Google subject：前者是個資，後者是穩定的外部識別碼。
        logger.LogInformation("External login resolving user. Provider={Provider}.", provider);

        // 1) 以 GoogleId 比對既有連結
        MyUser? user = await context.MyUser
            .FirstOrDefaultAsync(x => x.GoogleId == googleSubject);
        if (user is not null)
        {
            logger.LogInformation("External login matched existing GoogleId. UserId={UserId}.", user.Id);
            return new ExternalLoginResult(user, IsDeleted: false);
        }

        // 2) 已刪除的使用者綁著這個 Google 帳號
        var deletedByGoogleId = await FindDeletedAsync(x => x.GoogleId == googleSubject);
        if (deletedByGoogleId is not null)
        {
            logger.LogInformation("External login refused because the GoogleId belongs to a deleted user. UserId={UserId}.", deletedByGoogleId.Id);
            return new ExternalLoginResult(deletedByGoogleId, IsDeleted: true);
        }

        // 3) 以 Email 連結既有本地帳號（不改動 Status / 權限）
        if (!string.IsNullOrWhiteSpace(email))
        {
            user = await context.MyUser
                .FirstOrDefaultAsync(x => x.Email != null && x.Email.ToLower() == email.ToLower());
            if (user is not null)
            {
                user.GoogleId = googleSubject;
                user.OAuthProvider = provider;
                user.UpdateAt = DateTime.Now;
                await context.SaveChangesAsync();
                logger.LogInformation("External login linked Google to existing account. UserId={UserId}.", user.Id);
                // 以 Email 把外部身分連到既有帳號是安全相關事件：日後該帳號可直接用 Google 登入（LOG-14）。
                await auditLogService.WriteAsync(
                    AuditActions.User.SsoLink, success: true, actorUserId: user.Id, actorAccount: user.Account,
                    targetType: "MyUser", targetId: user.Id.ToString(), detail: $"provider={provider}");
                return new ExternalLoginResult(user, IsDeleted: false);
            }

            // 4) 已刪除的使用者用的是這個 Email
            var deletedByEmail = await FindDeletedAsync(x => x.Email != null && x.Email.ToLower() == email.ToLower());
            if (deletedByEmail is not null)
            {
                logger.LogInformation("External login refused because the email belongs to a deleted user. UserId={UserId}.", deletedByEmail.Id);
                return new ExternalLoginResult(deletedByEmail, IsDeleted: true);
            }
        }

        // 5) 自動建立新帳號（預設停用，待管理者啟用）
        int? defaultRoleId = await context.RoleView
            .Where(x => x.Name == defaultRoleName)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
        if (defaultRoleId is null)
        {
            // 設定的預設角色不存在（改名或已刪除）時，新帳號會沒有角色；管理員啟用前要先替他指定。
            logger.LogWarning("External login default role was not found; the new account has no role. RoleName={RoleName}.", defaultRoleName);
        }

        var newUser = new MyUser
        {
            Account = email,
            Name = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
            Email = email,
            Password = string.Empty,
            Salt = null,
            Status = false,
            IsAdmin = false,
            OAuthProvider = provider,
            GoogleId = googleSubject,
            RoleViewId = defaultRoleId,
            CreateAt = DateTime.Now,
            UpdateAt = DateTime.Now,
        };

        await context.MyUser.AddAsync(newUser);
        await context.SaveChangesAsync();

        logger.LogInformation(
            "External login created new disabled account awaiting approval. UserId={UserId}, Provider={Provider}.",
            newUser.Id, provider);
        await auditLogService.WriteAsync(
            AuditActions.User.SsoCreate, success: true, actorUserId: newUser.Id, actorAccount: newUser.Account,
            targetType: "MyUser", targetId: newUser.Id.ToString(), detail: $"provider={provider}; status=disabled");
        await notificationService.SendAsync(new NotificationRequest(
            NotificationCategories.AccountPending, "有新帳號等待開通",
            $"{newUser.Name}（{newUser.Account}）第一次用 Google 登入，帳號目前停用，請到使用者管理確認身分後啟用並指派角色。",
            "/myusers", NotificationTarget.AllAdmins(), SourceKey: $"AccountPending:{newUser.Id}"));
        return new ExternalLoginResult(newUser, IsDeleted: false);
    }

    private Task<MyUser?> FindDeletedAsync(Expression<Func<MyUser, bool>> predicate)
        => context.MyUser
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted)
            .Where(predicate)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();
}
