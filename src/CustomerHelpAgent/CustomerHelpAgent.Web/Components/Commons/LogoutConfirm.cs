using AntDesign;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 「使用者主動按下登出」的二次確認。
///
/// 登出有三個 UI 入口（使用者選單、側邊欄展開、側邊欄收合），文案與確認後的導向
/// 收斂在這裡，呼叫端只負責觸發 —— 否則三個入口的行為必定漂移。
///
/// ⚠️ **只給使用者主動點擊的入口用。**
/// <c>AuthenticationStateHelper</c> 有六處程式化的強制登出（未登入、找不到使用者、
/// 角色遺失…），那些是系統行為不是使用者意圖 —— 對一個 session 已經失效的人跳出
/// 「確定要登出嗎？」只會讓他卡在一個毫無意義的問題上。那些路徑一律不得走這裡。
/// </summary>
public static class LogoutConfirm
{
    /// <summary>
    /// 詢問使用者是否真的要登出；選「登出」才導向登出頁。
    /// 登出不會毀掉任何資料，因此走非破壞性的 <see cref="ConfirmDialog.AskAsync"/>（粉梅調）——
    /// 把每個提醒都染紅，紅色就不再代表任何事。
    /// </summary>
    /// <returns>true 表示使用者確認登出（已導向登出頁）；false 表示取消。</returns>
    public static async Task<bool> RequestAsync(ModalService modalService, NavigationManager navigationManager)
    {
        var confirmed = await ConfirmDialog.AskAsync(
            modalService,
            "確認登出",
            "確定要登出嗎？",
            "登出");

        if (confirmed == false)
        {
            return false;
        }

        // 登出頁是 SSR（要拿 HttpContext 才能 SignOutAsync），必須整頁載入。
        navigationManager.NavigateTo(MagicObjectHelper.SignoutUrl, forceLoad: true);

        return true;
    }

    /// <summary>
    /// 這個選單項是不是登出？三個入口共用同一個判斷，不要各自硬寫網址字串。
    /// </summary>
    public static bool IsLogoutUrl(string? url)
        => string.Equals(url?.Trim(), MagicObjectHelper.SignoutUrl, StringComparison.OrdinalIgnoreCase);
}
