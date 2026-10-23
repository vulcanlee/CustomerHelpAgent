using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 以 ASP.NET Core Data Protection 加密兩步驟驗證密鑰（0.9.104 起）。資料庫單獨外流時拿不到密鑰；
/// ⚠️ 但系統備份同時包含資料庫與金鑰環 —— 備份檔外流仍等於密鑰外流（見備份與還原操作手冊）。
/// </summary>
public sealed class DataProtectionTwoFactorSecretProtector : ITwoFactorSecretProtector
{
    private readonly IDataProtector protector;
    private readonly ILogger<DataProtectionTwoFactorSecretProtector> logger;

    public DataProtectionTwoFactorSecretProtector(IDataProtectionProvider dataProtectionProvider, ILogger<DataProtectionTwoFactorSecretProtector> logger)
    {
        protector = dataProtectionProvider.CreateProtector("CustomerHelpAgent.TwoFactorSecret.v1");
        this.logger = logger;
    }

    public string Protect(string secret) => protector.Protect(secret);

    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException ex)
        {
            // 金鑰環換過或遺失：這位使用者需要管理員「重設兩步驟驗證」。
            logger.LogWarning(ex, "Two-factor secret could not be decrypted; an administrator reset is required.");
            return null;
        }
    }
}
