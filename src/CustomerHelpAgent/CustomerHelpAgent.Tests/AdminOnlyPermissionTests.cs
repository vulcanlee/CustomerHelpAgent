using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 「系統管理」群組（含帳號與權限、監控與診斷、AI 管理、系統設定與維運四個子群組）的頁面刻意設計為管理員專屬：權限鍵不列入
/// <see cref="RolePermissionService.GetRoleListPermissionAllName"/>，因此不會種出
/// Permission 資料列、角色矩陣不顯示、任何角色都無法被授予，只有
/// AuthenticationStateHelper.CheckAccessPage 的管理員短路能通過。
///
/// MagicObjectHelper 與 SidebarMenuService 都寫了「請勿補上」的註解，但註解只是建議 ——
/// 這個測試才會真的擋下 PR。日後若有人「順手補齊」漏掉的權限鍵，這裡會紅燈。
/// </summary>
public sealed class AdminOnlyPermissionTests
{
    public static TheoryData<string> AdminOnlyPermissionKeys =>
    [
        MagicObjectHelper.角色_監控與診斷,
        MagicObjectHelper.角色_AI管理,
        MagicObjectHelper.角色_系統設定與維運,
        MagicObjectHelper.角色_系統健康監控,
        MagicObjectHelper.角色_日誌檢視,
        MagicObjectHelper.角色_資料庫用量,
        MagicObjectHelper.角色_日誌等級設定,
        MagicObjectHelper.角色_Token用量,
        MagicObjectHelper.角色_AI對話紀錄,
        MagicObjectHelper.角色_排程作業,
        MagicObjectHelper.角色_系統參數,
        MagicObjectHelper.角色_系統備份,
        MagicObjectHelper.角色_資料保留與清理,
        MagicObjectHelper.角色_展示控制台,
        MagicObjectHelper.角色_公告管理,
        MagicObjectHelper.角色_AI提示詞,
        MagicObjectHelper.角色_系統管理,
        MagicObjectHelper.角色_帳號與權限,
        MagicObjectHelper.角色_使用者管理,
        MagicObjectHelper.角色_角色管理,
        MagicObjectHelper.角色_系統例外紀錄,
        MagicObjectHelper.角色_稽核紀錄,
    ];

    [Theory]
    [MemberData(nameof(AdminOnlyPermissionKeys))]
    public void AdminOnlyKeys_ShouldNotAppearInRolePermissionMatrix(string permissionKey)
    {
        var allNames = new RolePermissionService()
            .GetRoleListPermissionAllName()
            .SelectMany(group => group)
            .ToList();

        Assert.DoesNotContain(permissionKey, allNames);
    }

    /// <summary>
    /// 0.9.37 起 export 自矩陣下架：它曾經勾得到，但全系統沒有任何檢查點，
    /// 屬於「勾了等於沒勾」的死權限（與 0.4.33 修掉的使用者管理／角色管理同一類）。
    ///
    /// 要重新上架，必須**先**有真正會檢查它的地方（`[HasPermission(頁面, "export")]`
    /// 或 `CheckAccessAction(頁面, "export")`），否則只是把誤導再放回去。
    /// </summary>
    [Fact]
    public void ExportAction_ShouldNotAppearInRolePermissionMatrix()
    {
        Assert.DoesNotContain("export", RolePermissionService.SupportedActions);
    }
}
