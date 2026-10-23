namespace CustomerHelpAgent.Business.Services.Other;

public enum AuthenticationCheckResult
{
    Succeeded,
    Unauthenticated,
    InvalidUser,
    RequiresPasswordChange,
    /// <summary>工作階段已失效（0.9.103 起）：改密碼、停用、角色變更、強制登出後的舊登入。</summary>
    SessionRevoked,
    /// <summary>必須使用兩步驟驗證卻還沒設定（0.9.104 起），已導向 /TwoFactorSetup。</summary>
    RequiresTwoFactorSetup
}
