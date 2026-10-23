using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試共用的密碼原則與鎖定設定（0.9.101 起）。預設值與 appsettings 相同（至少 8 字、要有英文與數字、不重複最近 3 組、不過期；5 次鎖 15 分鐘）。
/// 直接建構 <c>MyUserService</c>、<c>MyUserServiceLogin</c>、<c>PasswordResetService</c>、<c>AuthenticationStateHelper</c> 的測試一律從這裡取。
/// </summary>
internal static class PasswordTestDefaults
{
    public static IPasswordPolicy Policy(PasswordPolicySettings? settings = null, TimeProvider? timeProvider = null, string supportAccount = "support")
        => new PasswordPolicy(
            new StaticOptionsMonitor<PasswordPolicySettings>(settings ?? new PasswordPolicySettings()),
            Options.Create(new BootstrapSettings { SupportAccount = supportAccount }),
            timeProvider ?? TimeProvider.System);

    public static IOptionsMonitor<LockoutSettings> Lockout(int maxFailedAttempts = 5, int lockoutMinutes = 15)
        => new StaticOptionsMonitor<LockoutSettings>(new LockoutSettings { MaxFailedAttempts = maxFailedAttempts, LockoutMinutes = lockoutMinutes });
}
