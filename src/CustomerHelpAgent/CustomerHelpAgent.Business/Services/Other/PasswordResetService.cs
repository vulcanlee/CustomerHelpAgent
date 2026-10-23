using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 忘記密碼／重設密碼（0.9.60 起）。匿名流程，呼叫端是 <c>/Auths/ForgotPassword</c> 與 <c>/Auths/ResetPassword</c>。
///
/// <para><b>防列舉</b>：<see cref="RequestAsync"/> 不透露帳號存不存在、有沒有寄信，
/// 呼叫端都只能顯示同一句話；信一律交給 <see cref="IEmailQueue"/>，回應時間也不會因為「有寄信」而變長。
/// 原因只寫進稽核（<c>Password.ResetRequested</c> 的 detail）。
/// 唯一例外是輸入內建帳號名稱（<see cref="MagicObjectHelper.開發者帳號"/>）：名稱人人都知道，明說不提供服務不洩漏資訊。</para>
///
/// <para><b>單次使用</b>：重設時在交易內用 <c>ExecuteDelete</c> 搶占 token，刪到 1 列才繼續改密碼 ——
/// 同一個連結被並發送出兩次，只有一次會成功。</para>
///
/// <para>⚠️ 日誌不得記錄使用者輸入的帳號／Email（identifier）、token 或連結；只記 <c>UserId</c> 與筆數。</para>
/// </summary>
public sealed class PasswordResetService
{
    private const int TokenByteLength = 32;
    private const int AuditIdentifierMaxLength = 64;

    /// <summary>重設頁顯示給使用者的共同訊息：不區分「不存在／過期／已用過」，避免透露細節。</summary>
    public const string InvalidLinkMessage = "重設連結無效或已過期，請重新申請。";

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IEmailQueue emailQueue;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<PasswordResetSettings> resetOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ILogger<PasswordResetService> logger;

    public PasswordResetService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IEmailQueue emailQueue,
        IAuditLogService auditLogService,
        IOptionsMonitor<PasswordResetSettings> resetOptions,
        ISystemIdentity systemIdentity,
        IPasswordPolicy passwordPolicy,
        ILogger<PasswordResetService> logger)
    {
        this.contextFactory = contextFactory;
        this.emailQueue = emailQueue;
        this.auditLogService = auditLogService;
        this.resetOptions = resetOptions;
        this.systemIdentity = systemIdentity;
        this.passwordPolicy = passwordPolicy;
        this.logger = logger;
    }

    /// <summary>
    /// 申請重設：依帳號（優先）或 Email 找出帳號，對每個符合資格的帳號產生 token 並寄出重設信。
    /// </summary>
    /// <param name="identifier">使用者輸入的帳號或 Email。</param>
    /// <param name="resetPageAbsoluteUrl">重設頁的完整網址（不含 query），由呼叫端依 <c>PublicBaseUrl</c> 組出。</param>
    /// <returns>
    /// 只有輸入內建帳號名稱時回 <see cref="PasswordResetRequestResult.SupportAccountNotAllowed"/>；
    /// 其餘（含找不到、不符資格、冷卻中）一律 <see cref="PasswordResetRequestResult.Accepted"/>。
    /// </returns>
    public async Task<PasswordResetRequestResult> RequestAsync(
        string? identifier, string resetPageAbsoluteUrl, CancellationToken cancellationToken = default)
    {
        var input = identifier?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return PasswordResetRequestResult.Accepted;
        }

        // 照常往下走：support 帳號由 GetIneligibleReason 擋下並以 UserId 寫稽核，這裡只決定畫面要不要明說。
        // 帳號比對區分大小寫，先正規化成內建名稱，「SUPPORT」才會記成 reason=Support 而不是 NotFound。
        var result = PasswordResetRequestResult.Accepted;
        if (string.Equals(input, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase))
        {
            input = MagicObjectHelper.開發者帳號;
            result = PasswordResetRequestResult.SupportAccountNotAllowed;
        }

        var settings = resetOptions.CurrentValue;
        var nowUtc = DateTime.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 沒有排程清理，過期的 token 趁每次申請順手刪掉；每個帳號最多一筆，所以不會無限成長。
        await context.PasswordResetToken.Where(t => t.ExpiresAtUtc <= nowUtc).ExecuteDeleteAsync(cancellationToken);

        var users = await FindUsersAsync(context, input, cancellationToken);
        if (users.Count == 0)
        {
            logger.LogInformation("Password reset requested but no account matched.");
            await auditLogService.WriteAsync(
                AuditActions.Password.ResetRequested, success: false, actorAccount: TruncateForAudit(input), detail: "reason=NotFound");
            return result;
        }

        var systemName = systemIdentity.Name;

        foreach (var user in users)
        {
            var reason = GetIneligibleReason(user);
            if (reason is null && settings.RequestCooldownSeconds > 0)
            {
                var cooldownStartUtc = nowUtc.AddSeconds(-settings.RequestCooldownSeconds);
                var inCooldown = await context.PasswordResetToken
                    .AnyAsync(t => t.MyUserId == user.Id && t.CreatedAtUtc > cooldownStartUtc, cancellationToken);
                if (inCooldown)
                {
                    reason = "Cooldown";
                }
            }

            if (reason is not null)
            {
                logger.LogInformation("Password reset request skipped. UserId={UserId}, Reason={Reason}", user.Id, reason);
                await auditLogService.WriteAsync(
                    AuditActions.Password.ResetRequested, success: false, actorUserId: user.Id, actorAccount: user.Account,
                    targetType: nameof(MyUser), targetId: user.Id.ToString(), detail: $"reason={reason}");
                continue;
            }

            var rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

            // 新申請作廢舊 token：同一帳號永遠只有最新的一封信有效。
            await context.PasswordResetToken.Where(t => t.MyUserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            context.PasswordResetToken.Add(new PasswordResetToken
            {
                MyUserId = user.Id,
                TokenHash = HashToken(rawToken),
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddMinutes(settings.TokenLifetimeMinutes),
            });
            await context.SaveChangesAsync(cancellationToken);

            var link = $"{resetPageAbsoluteUrl}?token={rawToken}";
            var message = EmailTemplates.BuildPasswordReset(
                user.Email!.Trim(), systemName, user.Account, link, settings.TokenLifetimeMinutes);

            if (!emailQueue.TryEnqueue(message))
            {
                logger.LogWarning("Password reset message could not be queued. UserId={UserId}", user.Id);
                await auditLogService.WriteAsync(
                    AuditActions.Password.ResetRequested, success: false, actorUserId: user.Id, actorAccount: user.Account,
                    targetType: nameof(MyUser), targetId: user.Id.ToString(), detail: "reason=QueueFull");
                continue;
            }

            logger.LogInformation("Password reset message queued. UserId={UserId}", user.Id);
            await auditLogService.WriteAsync(
                AuditActions.Password.ResetRequested, success: true, actorUserId: user.Id, actorAccount: user.Account,
                targetType: nameof(MyUser), targetId: user.Id.ToString());
        }

        return result;
    }

    /// <summary>
    /// 檢查連結是否仍可使用（重設頁開啟時呼叫，用來決定顯示表單或「連結無效」）。
    /// 只讀不寫，不消耗 token。
    /// </summary>
    public async Task<(bool Valid, string? Account)> ValidateTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await FindUserByTokenAsync(context, token, DateTime.UtcNow, cancellationToken);

        return user is not null && GetIneligibleReason(user) is null
            ? (true, user.Account)
            : (false, null);
    }

    /// <summary>
    /// 以連結設定新密碼。密碼規則或歷史不過時**不消耗** token，使用者改正後可以用同一個連結再送一次。
    /// </summary>
    public async Task<VerifyRecordResult> ResetAsync(
        string? token, string? newPassword, string? confirmPassword, CancellationToken cancellationToken = default)
    {
        var ruleError = ValidateNewPassword(newPassword, confirmPassword);
        if (ruleError is not null)
        {
            return VerifyRecordResultFactory.Build(false, ruleError);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await WriteResetFailedAsync(null, "InvalidToken");
            return VerifyRecordResultFactory.Build(false, InvalidLinkMessage);
        }

        var nowUtc = DateTime.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var hash = HashToken(token);
        var tokenRow = await context.PasswordResetToken.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAtUtc > nowUtc, cancellationToken);
        if (tokenRow is null)
        {
            logger.LogInformation("Password reset rejected because the link is invalid or expired.");
            await WriteResetFailedAsync(null, "InvalidToken");
            return VerifyRecordResultFactory.Build(false, InvalidLinkMessage);
        }

        var user = await context.MyUser.FirstOrDefaultAsync(x => x.Id == tokenRow.MyUserId, cancellationToken);
        if (user is null || GetIneligibleReason(user) is not null)
        {
            // 申請之後帳號被停用或刪除：連結作廢，不給重設。
            await context.PasswordResetToken.Where(t => t.MyUserId == tokenRow.MyUserId).ExecuteDeleteAsync(cancellationToken);
            logger.LogWarning("Password reset rejected because the account is no longer eligible. UserId={UserId}", tokenRow.MyUserId);
            await WriteResetFailedAsync(user, "Ineligible");
            return VerifyRecordResultFactory.Build(false, InvalidLinkMessage);
        }

        // 歷史檢查要在搶占 token 之前（0.9.101 起）：不合格就讓使用者換一個密碼再送，同一個連結仍可用。
        if (await passwordPolicy.IsReusedAsync(context, user, newPassword!, cancellationToken))
        {
            logger.LogInformation("Password reset rejected because the new password was used recently. UserId={UserId}", user.Id);
            return VerifyRecordResultFactory.Build(false, PasswordPolicy.ReusedMessage);
        }

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            // 搶占：刪到 1 列代表這個請求拿到了 token；0 列代表另一個請求先用掉了。
            var claimed = await context.PasswordResetToken
                .Where(t => t.Id == tokenRow.Id)
                .ExecuteDeleteAsync(cancellationToken);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation("Password reset rejected because the link was already used. UserId={UserId}", user.Id);
                await WriteResetFailedAsync(user, "InvalidToken");
                return VerifyRecordResultFactory.Build(false, InvalidLinkMessage);
            }

            await passwordPolicy.ApplyAsync(context, user, newPassword!, mustChangeAtNextLogin: false, cancellationToken);
            user.UpdateAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);

            await context.PasswordResetToken.Where(t => t.MyUserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Password reset completed. UserId={UserId}", user.Id);
        await auditLogService.WriteAsync(
            AuditActions.Password.ResetCompleted, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: nameof(MyUser), targetId: user.Id.ToString());

        var notice = EmailTemplates.BuildPasswordChanged(
            user.Email!.Trim(), systemIdentity.Name, user.Account, DateTime.Now);
        if (!emailQueue.TryEnqueue(notice))
        {
            logger.LogWarning("Password changed notice could not be queued. UserId={UserId}", user.Id);
        }

        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>新密碼規則（0.9.101 起依密碼原則）；回傳 null 代表通過。</summary>
    public string? ValidateNewPassword(string? newPassword, string? confirmPassword)
    {
        if (passwordPolicy.Check(newPassword) is { } ruleError)
        {
            return ruleError;
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return "新密碼與確認密碼不一致。";
        }

        return null;
    }

    /// <summary>
    /// 不能用忘記密碼的帳號；回傳 null 代表符合資格。
    /// support 每次啟動會被重設回 BootstrapSettings 的密碼；停用者重設也登不進來；
    /// 沒有本地密碼的 Google 帳號本來就不用密碼登入。
    /// </summary>
    private static string? GetIneligibleReason(MyUser user)
    {
        if (string.Equals(user.Account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase))
        {
            return "Support";
        }

        if (!user.Status)
        {
            return "Disabled";
        }

        if (string.IsNullOrEmpty(user.Password))
        {
            return "NoLocalPassword";
        }

        if (string.IsNullOrWhiteSpace(user.Email) || !MailAddress.TryCreate(user.Email.Trim(), out _))
        {
            return "InvalidEmail";
        }

        return null;
    }

    /// <summary>先比帳號（與登入相同，區分大小寫）；對不到再比 Email（不分大小寫，可能多筆）。</summary>
    private static async Task<List<MyUser>> FindUsersAsync(BackendDBContext context, string input, CancellationToken cancellationToken)
    {
        var byAccount = await context.MyUser.AsNoTracking()
            .Where(x => x.Account == input)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (byAccount.Count > 0)
        {
            return byAccount;
        }

        // ⚠️ SQLite 的 lower() 只摺 ASCII；Email 幾乎都是 ASCII，這是知情接受的限制。
        var lowered = input.ToLowerInvariant();
        return await context.MyUser.AsNoTracking()
            .Where(x => x.Email != null && x.Email.ToLower() == lowered)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private static async Task<MyUser?> FindUserByTokenAsync(
        BackendDBContext context, string token, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        var userId = await context.PasswordResetToken.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.ExpiresAtUtc > nowUtc)
            .Select(t => (int?)t.MyUserId)
            .FirstOrDefaultAsync(cancellationToken);

        return userId is null
            ? null
            : await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    private async Task WriteResetFailedAsync(MyUser? user, string reason)
    {
        await auditLogService.WriteAsync(
            AuditActions.Password.ResetFailed, success: false, actorUserId: user?.Id, actorAccount: user?.Account,
            targetType: user is null ? null : nameof(MyUser), targetId: user?.Id.ToString(), detail: $"reason={reason}");
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string TruncateForAudit(string value) =>
        value.Length <= AuditIdentifierMaxLength ? value : value[..AuditIdentifierMaxLength];
}
