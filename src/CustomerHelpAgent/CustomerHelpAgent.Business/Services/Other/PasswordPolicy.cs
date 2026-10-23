using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 密碼原則（0.9.101 起）：規則、歷史、套用、是否必須變更。
///
/// ⚠️ 所有「設定新密碼」的路徑都必須經過 <see cref="ApplyAsync"/>（管理員建立與修改、變更密碼頁、忘記密碼重設）——
/// 它是唯一呼叫 <see cref="SecurePasswordHasher.HashPassword"/> 的地方（support 種子與登入時的舊雜湊升級除外，
/// <c>PasswordPolicyTests</c> 守門）。規則與歷史要在動手之前各自檢查，<see cref="ApplyAsync"/> 不再檢查一次。
/// </summary>
public interface IPasswordPolicy
{
    /// <summary>不符合的規則組成的訊息；null 代表通過。</summary>
    string? Check(string? password);

    /// <summary>給畫面顯示的規則說明，例如「至少 8 個字元，需包含英文字母、數字」。</summary>
    string Describe();

    /// <summary>產生一組必定通過 <see cref="Check"/> 的隨機密碼（「複製使用者」的「產生」鈕，0.9.127 起）。</summary>
    string Generate();

    /// <summary>新密碼是否與目前或最近 N 次用過的密碼相同（<see cref="PasswordPolicySettings.HistoryCount"/> 為 0 時一律 false）。</summary>
    Task<bool> IsReusedAsync(BackendDBContext context, MyUser user, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 寫入新密碼：雜湊、設定時間、「下次登入須變更密碼」旗標、解除鎖定、換工作階段版本、寫入歷史並裁到 N 筆。不呼叫 SaveChanges。
    /// </summary>
    Task ApplyAsync(BackendDBContext context, MyUser user, string password, bool mustChangeAtNextLogin, CancellationToken cancellationToken = default);

    /// <summary>登入後是否必須先變更密碼：旗標或已到期；support 帳號與沒有本機密碼的帳號豁免。</summary>
    bool RequiresChange(string account, bool hasLocalPassword, bool mustChangePassword, DateTime? passwordChangedAtUtc);

    /// <summary>密碼到期時間（UTC）；不會到期（到期天數 0、豁免、沒有設定時間）時為 null。</summary>
    DateTime? GetExpiresAtUtc(string account, bool hasLocalPassword, DateTime? passwordChangedAtUtc);
}

public sealed class PasswordPolicy : IPasswordPolicy
{
    public const string ReusedMessage = "新密碼不可與最近使用過的密碼相同。";

    private readonly IOptionsMonitor<PasswordPolicySettings> options;
    private readonly BootstrapSettings bootstrapSettings;
    private readonly TimeProvider timeProvider;

    public PasswordPolicy(IOptionsMonitor<PasswordPolicySettings> options, IOptions<BootstrapSettings> bootstrapOptions, TimeProvider timeProvider)
    {
        this.options = options;
        bootstrapSettings = bootstrapOptions.Value;
        this.timeProvider = timeProvider;
    }

    public string? Check(string? password)
    {
        var settings = options.CurrentValue;
        password ??= string.Empty;
        var failures = new List<string>();
        if (password.Length < settings.MinimumLength)
        {
            failures.Add($"至少 {settings.MinimumLength} 個字元");
        }

        if (settings.RequireLetter && !password.Any(char.IsAsciiLetter))
        {
            failures.Add("要有英文字母");
        }

        if (settings.RequireDigit && !password.Any(char.IsAsciiDigit))
        {
            failures.Add("要有數字");
        }

        if (settings.RequireUppercase && !password.Any(char.IsAsciiLetterUpper))
        {
            failures.Add("要有大寫英文字母");
        }

        if (settings.RequireSymbol && !password.Any(IsSymbol))
        {
            failures.Add("要有符號");
        }

        return failures.Count == 0 ? null : $"密碼不符合規則：{string.Join("、", failures)}。";
    }

    public string Describe()
    {
        var settings = options.CurrentValue;
        var kinds = new List<string>();
        if (settings.RequireLetter)
        {
            kinds.Add("英文字母");
        }

        if (settings.RequireUppercase)
        {
            kinds.Add("大寫英文字母");
        }

        if (settings.RequireDigit)
        {
            kinds.Add("數字");
        }

        if (settings.RequireSymbol)
        {
            kinds.Add("符號");
        }

        var text = $"至少 {settings.MinimumLength} 個字元";
        if (kinds.Count > 0)
        {
            text += $"，需包含{string.Join("、", kinds)}";
        }

        if (settings.HistoryCount > 0)
        {
            text += $"，不可與最近 {settings.HistoryCount} 次用過的密碼相同";
        }

        return text + "。";
    }

    public async Task<bool> IsReusedAsync(BackendDBContext context, MyUser user, string password, CancellationToken cancellationToken = default)
    {
        var count = options.CurrentValue.HistoryCount;
        if (count <= 0)
        {
            return false;
        }

        // 目前的密碼一定算（升級前的帳號沒有歷史紀錄，目前的密碼也可能是舊格式）。
        if (SecurePasswordHasher.VerifyPassword(password, user.Password, user.Salt) != PasswordVerificationOutcome.Failed)
        {
            return true;
        }

        if (user.Id == 0)
        {
            return false;
        }

        var hashes = await context.PasswordHistory.AsNoTracking()
            .Where(x => x.MyUserId == user.Id)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(count)
            .Select(x => x.PasswordHash)
            .ToListAsync(cancellationToken);
        return hashes.Any(hash => SecurePasswordHasher.VerifyPassword(password, hash, null) != PasswordVerificationOutcome.Failed);
    }

    public async Task ApplyAsync(BackendDBContext context, MyUser user, string password, bool mustChangeAtNextLogin, CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        user.Salt = string.IsNullOrWhiteSpace(user.Salt) ? Guid.NewGuid().ToString() : user.Salt;
        user.Password = SecurePasswordHasher.HashPassword(password);
        user.PasswordChangedAtUtc = nowUtc;
        user.MustChangePassword = mustChangeAtNextLogin;

        // 改了密碼，其他已登入的瀏覽器與 API refresh token 一律失效（0.9.103 起）。登入時的舊雜湊升級不經這裡，不會輪替。
        user.SecurityStamp = SecurityStamps.New();

        // 設了新密碼就解除鎖定（與忘記密碼重設、管理員改密碼一直以來的行為一致）。
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;

        var keep = options.CurrentValue.HistoryCount;
        if (user.Id != 0)
        {
            var existing = await context.PasswordHistory
                .Where(x => x.MyUserId == user.Id)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .ToListAsync(cancellationToken);
            context.PasswordHistory.RemoveRange(existing.Skip(Math.Max(keep - 1, 0)));
        }

        if (keep > 0)
        {
            context.PasswordHistory.Add(new PasswordHistory { MyUser = user, PasswordHash = user.Password, CreatedAtUtc = nowUtc });
        }
    }

    public bool RequiresChange(string account, bool hasLocalPassword, bool mustChangePassword, DateTime? passwordChangedAtUtc)
    {
        if (IsExempt(account, hasLocalPassword))
        {
            return false;
        }

        return mustChangePassword
            || GetExpiresAtUtc(account, hasLocalPassword, passwordChangedAtUtc) is { } expiresAt && timeProvider.GetUtcNow().UtcDateTime >= expiresAt;
    }

    public DateTime? GetExpiresAtUtc(string account, bool hasLocalPassword, DateTime? passwordChangedAtUtc)
    {
        var days = options.CurrentValue.ExpiryDays;
        if (days <= 0 || passwordChangedAtUtc is null || IsExempt(account, hasLocalPassword))
        {
            return null;
        }

        return DateTime.SpecifyKind(passwordChangedAtUtc.Value, DateTimeKind.Utc).AddDays(days);
    }

    /// <summary>
    /// support 帳號每次啟動都被重設回設定檔的密碼（要求它改密碼只會讓它永遠卡在變更頁）；
    /// 只用 Google 登入、沒有本機密碼的帳號也無從變更。
    /// </summary>
    private bool IsExempt(string account, bool hasLocalPassword)
        => !hasLocalPassword || string.Equals(account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase);

    public string Generate()
    {
        var settings = options.CurrentValue;
        var length = Math.Max(settings.MinimumLength, GeneratedMinimumLength);

        // 大小寫與數字一律各放一個（不論規則是否要求）；符號只在規則要求時才放，口頭轉交時比較不會念錯。
        List<string> required = [GeneratedLower, GeneratedUpper, GeneratedDigits];
        if (settings.RequireSymbol)
        {
            required.Add(GeneratedSymbols);
        }

        var pool = string.Concat(required);
        var chars = required.Select(set => RandomNumberGenerator.GetItems<char>(set, 1)[0])
            .Concat(RandomNumberGenerator.GetItems<char>(pool, length - required.Count))
            .ToArray();
        RandomNumberGenerator.Shuffle<char>(chars);
        return new string(chars);
    }

    private const int GeneratedMinimumLength = 12;

    // 排除容易看錯的 0/O、1/l/I。
    private const string GeneratedLower = "abcdefghijkmnopqrstuvwxyz";
    private const string GeneratedUpper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string GeneratedDigits = "23456789";
    private const string GeneratedSymbols = "!@#$%*?-_";

    private static bool IsSymbol(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
}
