namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// <see cref="PasswordResetService.RequestAsync"/> 的結果。刻意只分兩種：
/// 帳號存不存在、有沒有寄信都歸在 <see cref="Accepted"/>，防止用忘記密碼頁試探帳號。
/// </summary>
public enum PasswordResetRequestResult
{
    /// <summary>已受理（不代表有寄信）。畫面顯示同一句說明。</summary>
    Accepted,

    /// <summary>輸入的是內建帳號名稱；內建帳號不提供忘記密碼，畫面可以明說。</summary>
    SupportAccountNotAllowed,
}
