using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 畫面上依動作顯示按鈕的判斷（0.9.110 起抽成純函式，<c>AuthenticationStateHelper.CheckAccessAction</c> 與
/// <c>&lt;RequirePermission&gt;</c> 元件共用，才能直接寫單元測試）。
/// </summary>
public static class PermissionRules
{
    /// <summary>
    /// 管理員一律通過；擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。
    /// 只有「頁面:view」不代表可以新增、修改或刪除。
    /// </summary>
    public static bool CanAccessAction(CurrentUser user, string page, string action)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        var keys = user.RoleList;
        return keys.Contains(PermissionKey.For(page, action)) || keys.Contains(page);
    }
}
