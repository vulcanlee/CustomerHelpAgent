namespace CustomerHelpAgent.Web.Components.Auths;

/// <summary>
/// 登入頁與忘記密碼頁共用的 4 碼數字驗證碼。
///
/// ⚠️ 答案放在表單的隱藏欄位隨表單往返（不是 session），讀得到頁面原始碼的腳本就讀得到答案 ——
/// 它只擋「隨手連點」，**不是防機器人的機制**。忘記密碼真正的防線是同帳號冷卻時間。
/// </summary>
internal static class AuthCaptcha
{
    public const int Length = 4;

    public static string Generate()
    {
        return Random.Shared.Next(0, (int)Math.Pow(10, Length)).ToString($"D{Length}");
    }
}
