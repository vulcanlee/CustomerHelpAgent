using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 一次登入嘗試的結果（0.9.104 起取代 <c>(string, MyUser?)</c>，仍可用 <c>var (message, user) = …</c> 解構）。
/// ⚠️ <see cref="RequiresTwoFactor"/> 為 true 時 <see cref="User"/> 有值但**還不能發 Cookie 或 token** ——
/// 必須先經 <see cref="MyUserServiceLogin.CompleteSecondFactorAsync"/> 完成第二步。
/// </summary>
public sealed record LoginAttemptResult(string Message, MyUser? User, bool RequiresTwoFactor = false)
{
    public void Deconstruct(out string message, out MyUser? user)
    {
        message = Message;
        user = RequiresTwoFactor ? null : User;
    }
}

public class MyUserServiceLogin
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LockoutSettings> lockoutOptions;
    private readonly INotificationSender notificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ITwoFactorService twoFactorService;

    /// <summary>
    /// 帳號不存在、密碼錯誤、帳號鎖定中一律回這一則（0.9.101 起），不讓人從訊息分辨帳號是否存在。
    /// 登入頁另外固定顯示鎖定規則，被鎖住的人知道可以等、用忘記密碼或找管理員。
    /// </summary>
    public const string InvalidCredentialsMessage = "帳號或密碼不正確，或帳號已被暫時鎖定。";

    /// <summary>第二步驗證碼錯誤或帳號鎖定中（0.9.104 起）。</summary>
    public const string InvalidSecondFactorMessage = "驗證碼不正確，或帳號已被暫時鎖定。";

    public IMapper Mapper { get; }
    public IConfiguration Configuration { get; }
    public ILogger<MyUserServiceLogin> Logger { get; }

    public MyUserServiceLogin(
        BackendDBContext context,
        IMapper mapper,
        IConfiguration configuration,
        ILogger<MyUserServiceLogin> logger,
        RolePermissionService rolePermissionService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LockoutSettings> lockoutOptions,
        INotificationSender notificationSender,
        TimeProvider timeProvider,
        ITwoFactorService twoFactorService)
    {
        this.context = context;
        Mapper = mapper;
        Configuration = configuration;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
        this.auditLogService = auditLogService;
        this.lockoutOptions = lockoutOptions;
        this.notificationSender = notificationSender;
        this.timeProvider = timeProvider;
        this.twoFactorService = twoFactorService;
    }

    /// <summary>
    /// 取回仍可使用的帳號；查無此人或已停用時回 null。
    /// 供 refresh token 換發時重新確認使用者現況（token claim 內的資料可能已過期）。
    /// </summary>
    public async Task<MyUser?> GetActiveUserAsync(int userId)
    {
        MyUser? item = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        return item is { Status: true } ? item : null;
    }

    public async Task<LoginAttemptResult> LoginAsync(string username, string password)
    {
        Logger.LogInformation("Login attempt started for Account={Account}.", username);

        try
        {
            MyUser? item = await context.MyUser
                .FirstOrDefaultAsync(x => x.Account == username);

            if (item is null)
            {
                Logger.LogWarning("Login failed because account was not found. Account={Account}", username);
                await auditLogService.WriteAsync(AuditActions.Login.Failed, success: false, actorAccount: username, detail: "帳號不存在");
                return new LoginAttemptResult(InvalidCredentialsMessage, null);
            }

            // 停用帳號一律擋在發證之前。
            // 沿革：0.4.34 之前這裡沒有檢查 Status —— UI 路徑靠 AuthenticationStateHelper.Check
            // 在後續每次頁面載入時擋下，但 API 的 /api/Auth/login 會直接發出 JWT，
            // 而 PermissionChecker 與 HasPermissionAttribute 也都不看 Status，
            // 等於停用帳號仍可正常呼叫 API。
            if (!item.Status)
            {
                Logger.LogWarning("Login blocked because account is disabled. Account={Account}, UserId={UserId}", username, item.Id);
                await auditLogService.WriteAsync(AuditActions.Login.Disabled, success: false, actorUserId: item.Id, actorAccount: username);
                return new LoginAttemptResult("帳號已停用，請聯絡系統管理員。", null);
            }

            var (blocked, changed) = await CheckLockoutAsync(item, username);
            if (blocked)
            {
                return new LoginAttemptResult(InvalidCredentialsMessage, null);
            }

            PasswordVerificationOutcome outcome = SecurePasswordHasher.VerifyPassword(password, item.Password, item.Salt);
            if (outcome == PasswordVerificationOutcome.Failed)
            {
                await RegisterFailureAsync(item, username, AuditActions.Login.Failed);
                return new LoginAttemptResult(InvalidCredentialsMessage, null);
            }

            if (outcome == PasswordVerificationOutcome.SuccessRehashNeeded)
            {
                item.Password = SecurePasswordHasher.HashPassword(password);
                changed = true;
                Logger.LogInformation("Password hash upgraded to PBKDF2 for UserId={UserId}.", item.Id);
            }

            // 0.9.100 之前以「密碼是 123456」代表必須變更密碼，既有資料無法以 SQL 轉換（只有雜湊）——
            // 登入成功時看明文，還在用 123456 的人補上旗標，之後一律只看旗標。
            if (!item.MustChangePassword && string.Equals(password, MagicObjectHelper.NeedChangePassword, StringComparison.Ordinal))
            {
                item.MustChangePassword = true;
                changed = true;
                Logger.LogInformation("Legacy default password detected; password change is now required. UserId={UserId}", item.Id);
            }

            // ⚠️ 已啟用兩步驟驗證（0.9.104 起）：密碼對了也**不**歸零失敗次數、不寫 Login.Success ——
            // 否則知道密碼的人可以每猜幾次驗證碼就重輸一次密碼把次數洗掉，無限猜下去。由第二步完成這些事。
            if (RequiresSecondFactor(item))
            {
                if (changed)
                {
                    await context.SaveChangesAsync();
                }

                Logger.LogInformation("Password accepted; second factor required. UserId={UserId}", item.Id);
                return new LoginAttemptResult(string.Empty, item, RequiresTwoFactor: true);
            }

            await CompleteSuccessAsync(item, changed, AuditActions.Login.Success, detail: null);
            Logger.LogInformation("Login validation succeeded for Account={Account}, UserId={UserId}.", username, item.Id);
            return new LoginAttemptResult(string.Empty, item);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login attempt failed unexpectedly for Account={Account}.", username);
            throw;
        }
    }

    /// <summary>
    /// 登入第二步（0.9.104 起）：密碼（或 Google）已通過、帳號已啟用兩步驟驗證之後呼叫。
    /// 先重查啟用與鎖定，再驗證碼（或已記住的裝置）；錯了計入登入失敗次數（達門檻鎖定並通知管理員），對了才歸零並寫成功稽核。
    /// </summary>
    /// <param name="rememberedDevice">呼叫端已確認這台裝置在「記住裝置」期限內（不再驗證碼）。</param>
    /// <param name="provider">Google 登入時為 <c>"Google"</c>，成功稽核改寫 <c>Login.Sso.Success</c>。</param>
    public async Task<LoginAttemptResult> CompleteSecondFactorAsync(int userId, string? code, bool rememberedDevice = false, string? provider = null)
    {
        var item = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
        if (item is null || !item.Status || !RequiresSecondFactor(item))
        {
            Logger.LogWarning("Second factor refused because the account is not eligible. UserId={UserId}", userId);
            return new LoginAttemptResult(InvalidSecondFactorMessage, null);
        }

        var (blocked, changed) = await CheckLockoutAsync(item, item.Account);
        if (blocked)
        {
            return new LoginAttemptResult(InvalidSecondFactorMessage, null);
        }

        var method = rememberedDevice
            ? "device"
            : await twoFactorService.VerifyAsync(userId, code) switch
            {
                SecondFactorMethod.Totp => "totp",
                SecondFactorMethod.BackupCode => "backup",
                _ => null,
            };
        if (method is null)
        {
            await RegisterFailureAsync(item, item.Account, AuditActions.Login.TwoFactorFailed);
            return new LoginAttemptResult(InvalidSecondFactorMessage, null);
        }

        var detail = provider is null ? $"mfa={method}" : $"provider={provider}; mfa={method}";
        await CompleteSuccessAsync(item, changed, provider is null ? AuditActions.Login.Success : AuditActions.Login.SsoSuccess, detail);
        Logger.LogInformation("Second factor accepted. UserId={UserId}, Method={Method}", userId, method);
        return new LoginAttemptResult(string.Empty, item);
    }

    private static bool RequiresSecondFactor(MyUser user) => user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret);

    /// <summary>鎖定中回 blocked；鎖定已到期則先歸零（changed）。</summary>
    private async Task<(bool Blocked, bool Changed)> CheckLockoutAsync(MyUser item, string account)
    {
        if (item.LockoutEndUtc is not { } lockoutEndUtc)
        {
            return (false, false);
        }

        if (lockoutEndUtc > timeProvider.GetUtcNow().UtcDateTime)
        {
            // 鎖定中不驗證、不累加次數；稽核記 Failed（LockedOut 只記造成鎖定的那一次）。
            Logger.LogWarning("Login blocked because account is locked. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", account, item.Id, lockoutEndUtc);
            await auditLogService.WriteAsync(AuditActions.Login.Failed, success: false, actorUserId: item.Id, actorAccount: account, detail: "reason=Locked");
            return (true, false);
        }

        // ⚠️ 鎖定已到期：次數先歸零再驗證。0.9.100 之前次數留在門檻上，到期後只要再錯一次就立刻又鎖。
        item.AccessFailedCount = 0;
        item.LockoutEndUtc = null;
        return (false, true);
    }

    /// <summary>密碼或第二步錯誤：累加次數、達門檻鎖定並通知管理員；稽核標籤只在造成鎖定的那一次是 LockedOut。</summary>
    private async Task RegisterFailureAsync(MyUser item, string account, string failureAction)
    {
        var settings = lockoutOptions.CurrentValue;
        item.AccessFailedCount++;
        bool lockedNow = item.AccessFailedCount >= settings.MaxFailedAttempts;
        if (lockedNow)
        {
            item.LockoutEndUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(settings.LockoutMinutes);
            Logger.LogWarning("Account locked after too many failed attempts. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", account, item.Id, item.LockoutEndUtc);
        }
        else
        {
            Logger.LogWarning("Login failed because validation failed. Account={Account}, UserId={UserId}, AccessFailedCount={AccessFailedCount}", account, item.Id, item.AccessFailedCount);
        }

        await context.SaveChangesAsync();
        await auditLogService.WriteAsync(
            lockedNow ? AuditActions.Login.LockedOut : failureAction,
            success: false, actorUserId: item.Id, actorAccount: account, detail: $"AccessFailedCount={item.AccessFailedCount}");
        if (lockedNow)
        {
            await NotifyLockedAsync(item, settings.MaxFailedAttempts);
        }
    }

    /// <summary>登入成功的收尾：歸零失敗次數、補工作階段版本、存檔、寫成功稽核。</summary>
    private async Task CompleteSuccessAsync(MyUser item, bool changed, string successAction, string? detail)
    {
        if (item.AccessFailedCount != 0)
        {
            item.AccessFailedCount = 0;
            changed = true;
        }

        // 重疊回收時舊版程式新增的帳號沒有工作階段版本：補一個，否則登入後的每一次檢查都會判定不符（0.9.103 起）。
        if (string.IsNullOrEmpty(item.SecurityStamp))
        {
            item.SecurityStamp = SecurityStamps.New();
            changed = true;
        }

        if (changed)
        {
            await context.SaveChangesAsync();
        }

        await auditLogService.WriteAsync(successAction, success: true, actorUserId: item.Id, actorAccount: item.Account, detail: detail);
    }

    /// <summary>通知全體管理員並寄信；<see cref="INotificationSender"/> 不會丟例外，不影響登入回應。</summary>
    private async Task NotifyLockedAsync(MyUser user, int attempts)
    {
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(user.LockoutEndUtc!.Value, timeProvider.LocalTimeZone);
        await notificationSender.SendAsync(new NotificationRequest(
            NotificationCategories.AccountLocked,
            $"帳號已鎖定：{user.Account}",
            $"連續輸錯密碼或驗證碼 {attempts} 次，鎖定到 {localEnd:yyyy-MM-dd HH:mm}。可在「使用者管理」解鎖，或等鎖定時間結束。",
            "/myusers",
            NotificationTarget.AllAdmins(),
            AlsoEmail: true));
    }
}
