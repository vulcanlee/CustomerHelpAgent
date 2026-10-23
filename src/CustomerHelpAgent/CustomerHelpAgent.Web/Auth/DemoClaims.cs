namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 展示身分切換（1.0.5 起，只在 <c>DemoSettings:Enabled</c> 時發生）寫進登入 Cookie 的額外 claims。
/// 展示者（管理員）切換成示範帳號後，Cookie 的 Sid／版本是示範帳號的，另帶這兩個 claim 記住是誰切過來的；
/// 「回到展示者」時由 <c>/Auths/DemoSwitch</c> 驗證展示者仍是啟用中的管理員，而且版本沒有換過。
/// </summary>
public static class DemoClaims
{
    /// <summary>展示者的使用者 Id。</summary>
    public const string PresenterId = "demo_presenter_id";

    /// <summary>切換當下展示者的工作階段版本；展示者被強制登出、改密碼後就對不上，不能再切回。</summary>
    public const string PresenterStamp = "demo_presenter_stamp";
}
