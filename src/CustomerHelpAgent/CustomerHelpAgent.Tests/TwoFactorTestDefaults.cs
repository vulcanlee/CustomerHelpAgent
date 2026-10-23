using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試共用的兩步驟驗證服務（0.9.104 起）：真的 <see cref="TwoFactorService"/>，密鑰以 <see cref="EphemeralDataProtectionProvider"/> 加密。
/// 直接建構 <c>MyUserServiceLogin</c>、<c>AuthenticationStateHelper</c> 的測試一律從這裡取。
/// </summary>
internal static class TwoFactorTestDefaults
{
    public static TwoFactorService Service(
        IDbContextFactory<BackendDBContext> factory,
        IAuditLogService? audit = null,
        TimeProvider? timeProvider = null,
        TwoFactorSettings? settings = null,
        string supportAccount = "support",
        CurrentUserService? currentUser = null,
        IDataProtectionProvider? dataProtectionProvider = null)
        => new(
            factory,
            new TotpService(),
            Protector(dataProtectionProvider),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(new SystemSettings())),
            new StaticOptionsMonitor<TwoFactorSettings>(settings ?? new TwoFactorSettings()),
            Options.Create(new BootstrapSettings { SupportAccount = supportAccount }),
            timeProvider ?? TimeProvider.System,
            audit ?? new RecordingAuditLogService(),
            currentUser ?? new CurrentUserService(),
            NullLogger<TwoFactorService>.Instance);

    public static DataProtectionTwoFactorSecretProtector Protector(IDataProtectionProvider? dataProtectionProvider = null)
        => new(dataProtectionProvider ?? new EphemeralDataProtectionProvider(), NullLogger<DataProtectionTwoFactorSecretProtector>.Instance);
}
