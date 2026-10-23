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

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 兩步驟驗證密鑰的加解密（0.9.104 起）。實作在 Web 端（ASP.NET Core Data Protection）。
/// ⚠️ 金鑰環遺失時解不開，所有已啟用的帳號都要由管理員「重設兩步驟驗證」；support 帳號不受強制，是救援入口。
/// </summary>
public interface ITwoFactorSecretProtector
{
    string Protect(string secret);

    /// <summary>解不開（金鑰環換過、資料被竄改）時回 null。</summary>
    string? Unprotect(string? protectedSecret);
}

/// <summary>開始設定時產生的密鑰與給驗證器 App 掃描的網址。密鑰在確認驗證碼之前不存進資料庫。</summary>
public sealed record TwoFactorEnrollment(string Secret, string ProvisioningUri);

/// <summary>啟用或重新產生備用碼的結果；備用碼原文只在這一次出現。</summary>
public sealed record TwoFactorCodesResult(bool Success, string? Message, IReadOnlyList<string> BackupCodes);

/// <summary>第二步驗證用的是哪一種。</summary>
public enum SecondFactorMethod
{
    None,
    Totp,
    BackupCode,
}

/// <summary>
/// 兩步驟驗證（0.9.104 起）：設定、驗證（防重放）、備用碼、停用、管理員重設，以及「這個人是否必須使用」。
/// 啟用、停用、重設都會換工作階段版本（其他已登入的裝置登出）。
/// </summary>
public interface ITwoFactorService
{
    /// <summary>必須使用：任一有效角色勾了「需要兩步驟驗證」，或是管理員且 <see cref="TwoFactorSettings.RequireForAdmins"/>；support 與沒有本機密碼的帳號豁免。</summary>
    Task<bool> IsRequiredAsync(int userId);

    TwoFactorEnrollment BeginEnrollment(string account);

    /// <summary>以掃描後 App 顯示的驗證碼確認，成功才存密鑰、啟用並產生 10 組備用碼。</summary>
    Task<TwoFactorCodesResult> EnableAsync(int userId, string secret, string? code);

    /// <summary>驗證 6 位數驗證碼（同一個時間步只能用一次）或備用碼（每組只能用一次）。</summary>
    Task<SecondFactorMethod> VerifyAsync(int userId, string? code);

    Task<TwoFactorCodesResult> RegenerateBackupCodesAsync(int userId, string? code);

    /// <summary>本人停用（需要驗證碼；必須使用的人不能停用）。</summary>
    Task<VerifyRecordResult> DisableAsync(int userId, string? code);

    /// <summary>管理員重設（手機遺失）：清掉密鑰與備用碼，對方下次登入只需密碼（若必須使用，會被帶去重新設定）。</summary>
    Task<VerifyRecordResult> ResetAsync(int userId);

    Task<int> CountUnusedBackupCodesAsync(int userId);
}

public sealed class TwoFactorService : ITwoFactorService
{
    internal const int BackupCodeCount = 10;
    private const string BackupCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ITotpService totpService;
    private readonly ITwoFactorSecretProtector secretProtector;
    private readonly ISystemIdentity systemIdentity;
    private readonly IOptionsMonitor<TwoFactorSettings> options;
    private readonly BootstrapSettings bootstrapSettings;
    private readonly TimeProvider timeProvider;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<TwoFactorService> logger;

    public TwoFactorService(
        IDbContextFactory<BackendDBContext> contextFactory,
        ITotpService totpService,
        ITwoFactorSecretProtector secretProtector,
        ISystemIdentity systemIdentity,
        IOptionsMonitor<TwoFactorSettings> options,
        IOptions<BootstrapSettings> bootstrapOptions,
        TimeProvider timeProvider,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<TwoFactorService> logger)
    {
        this.contextFactory = contextFactory;
        this.totpService = totpService;
        this.secretProtector = secretProtector;
        this.systemIdentity = systemIdentity;
        this.options = options;
        bootstrapSettings = bootstrapOptions.Value;
        this.timeProvider = timeProvider;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public async Task<bool> IsRequiredAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Account, HasPassword = x.Password != string.Empty, x.IsAdmin, x.RoleViewId })
            .FirstOrDefaultAsync();
        if (user is null || !user.HasPassword || string.Equals(user.Account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (user.IsAdmin && options.CurrentValue.RequireForAdmins)
        {
            return true;
        }

        var roleIds = await context.UserRole.Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).ToListAsync();
        if (user.RoleViewId is { } primary)
        {
            roleIds.Add(primary);
        }

        // 經 RoleView 的全域過濾器排除已刪除的角色（與權限判斷相同）。
        return await context.RoleView.AnyAsync(x => roleIds.Contains(x.Id) && x.RequireTwoFactor);
    }

    public TwoFactorEnrollment BeginEnrollment(string account)
    {
        var secret = totpService.GenerateSecret();
        return new TwoFactorEnrollment(secret, totpService.GenerateProvisioningUri(secret, account, systemIdentity.Name));
    }

    public async Task<TwoFactorCodesResult> EnableAsync(int userId, string secret, string? code)
    {
        var step = totpService.FindMatchingStep(secret, NormalizeTotp(code), timeProvider.GetUtcNow().ToUnixTimeSeconds());
        if (step is null)
        {
            return new TwoFactorCodesResult(false, "驗證碼不正確。請確認手機時間正確，輸入 App 上目前顯示的 6 位數。", []);
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            return new TwoFactorCodesResult(false, "找不到使用者資料。", []);
        }

        user.TwoFactorSecret = secretProtector.Protect(secret);
        user.TwoFactorEnabled = true;
        user.TwoFactorLastStep = step;
        user.SecurityStamp = SecurityStamps.New();
        var codes = ReplaceBackupCodes(context, userId);
        await context.SaveChangesAsync();

        await auditLogService.WriteAsync(AuditActions.User.TwoFactorEnable, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: nameof(MyUser), targetId: user.Id.ToString());
        logger.LogInformation("Two-factor authentication enabled. UserId={UserId}", userId);
        return new TwoFactorCodesResult(true, null, codes);
    }

    public async Task<SecondFactorMethod> VerifyAsync(int userId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return SecondFactorMethod.None;
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var totp = NormalizeTotp(code);
        if (totp.Length == 6 && totp.All(char.IsAsciiDigit))
        {
            var stored = await context.MyUser.AsNoTracking()
                .Where(x => x.Id == userId && x.TwoFactorEnabled)
                .Select(x => x.TwoFactorSecret)
                .FirstOrDefaultAsync();
            var secret = secretProtector.Unprotect(stored);
            if (secret is null || totpService.FindMatchingStep(secret, totp, timeProvider.GetUtcNow().ToUnixTimeSeconds()) is not { } step)
            {
                return SecondFactorMethod.None;
            }

            // ⚠️ 防重放：只接受比上次用過的時間步更新的碼；條件式 UPDATE，兩個請求同時用同一組只有一個成功。
            var accepted = await context.MyUser
                .Where(x => x.Id == userId && (x.TwoFactorLastStep == null || x.TwoFactorLastStep < step))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorLastStep, step));
            return accepted == 1 ? SecondFactorMethod.Totp : SecondFactorMethod.None;
        }

        var hash = HashBackupCode(userId, code);
        var used = await context.TwoFactorBackupCode
            .Where(x => x.MyUserId == userId && x.CodeHash == hash && x.UsedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, timeProvider.GetUtcNow().UtcDateTime));
        if (used == 1)
        {
            logger.LogInformation("Two-factor backup code used. UserId={UserId}", userId);
            return SecondFactorMethod.BackupCode;
        }

        return SecondFactorMethod.None;
    }

    public async Task<TwoFactorCodesResult> RegenerateBackupCodesAsync(int userId, string? code)
    {
        if (await VerifyAsync(userId, code) == SecondFactorMethod.None)
        {
            return new TwoFactorCodesResult(false, "驗證碼不正確。", []);
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var account = await context.MyUser.Where(x => x.Id == userId).Select(x => x.Account).FirstAsync();
        var codes = ReplaceBackupCodes(context, userId);
        await context.SaveChangesAsync();
        await auditLogService.WriteAsync(AuditActions.User.TwoFactorBackupCodesRegenerate, success: true, actorUserId: userId, actorAccount: account,
            targetType: nameof(MyUser), targetId: userId.ToString());
        return new TwoFactorCodesResult(true, null, codes);
    }

    public async Task<VerifyRecordResult> DisableAsync(int userId, string? code)
    {
        if (await IsRequiredAsync(userId))
        {
            return VerifyRecordResultFactory.Build(false, "你的帳號必須使用兩步驟驗證，不能停用。");
        }

        if (await VerifyAsync(userId, code) == SecondFactorMethod.None)
        {
            return VerifyRecordResultFactory.Build(false, "驗證碼不正確。");
        }

        var account = await ClearAsync(userId);
        await auditLogService.WriteAsync(AuditActions.User.TwoFactorDisable, success: true, actorUserId: userId, actorAccount: account,
            targetType: nameof(MyUser), targetId: userId.ToString());
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> ResetAsync(int userId)
    {
        var account = await ClearAsync(userId);
        if (account is null)
        {
            return VerifyRecordResultFactory.Build(false, "找不到這位使用者。");
        }

        var actor = currentUserService.CurrentUser;
        await auditLogService.WriteAsync(AuditActions.User.TwoFactorReset, success: true,
            actorUserId: actor.Id > 0 ? actor.Id : null, actorAccount: actor.Id > 0 ? actor.Account : null,
            targetType: nameof(MyUser), targetId: userId.ToString(), detail: $"account={account}");
        logger.LogInformation("Two-factor authentication reset by an administrator. UserId={UserId}", userId);
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<int> CountUnusedBackupCodesAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.TwoFactorBackupCode.CountAsync(x => x.MyUserId == userId && x.UsedAtUtc == null);
    }

    /// <summary>清掉密鑰、備用碼並換工作階段版本；回傳帳號（找不到回 null）。</summary>
    private async Task<string?> ClearAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            return null;
        }

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorLastStep = null;
        user.SecurityStamp = SecurityStamps.New();
        context.TwoFactorBackupCode.RemoveRange(await context.TwoFactorBackupCode.Where(x => x.MyUserId == userId).ToListAsync());
        await context.SaveChangesAsync();
        return user.Account;
    }

    private List<string> ReplaceBackupCodes(BackendDBContext context, int userId)
    {
        context.TwoFactorBackupCode.RemoveRange(context.TwoFactorBackupCode.Where(x => x.MyUserId == userId));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var codes = new List<string>(BackupCodeCount);
        for (var i = 0; i < BackupCodeCount; i++)
        {
            var raw = RandomNumberGenerator.GetString(BackupCodeAlphabet, 10);
            var display = $"{raw[..5]}-{raw[5..]}";
            codes.Add(display);
            context.TwoFactorBackupCode.Add(new TwoFactorBackupCode { MyUserId = userId, CodeHash = HashBackupCode(userId, display), CreatedAtUtc = now });
        }

        return codes;
    }

    /// <summary>備用碼的雜湊：去掉 <c>-</c> 與空白、轉大寫後，以使用者 Id 為鹽做 HMAC-SHA256。</summary>
    internal static string HashBackupCode(int userId, string code)
    {
        var normalized = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        var key = Encoding.UTF8.GetBytes($"two-factor-backup:{userId}");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalized)));
    }

    private static string NormalizeTotp(string? code) => new((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
}
