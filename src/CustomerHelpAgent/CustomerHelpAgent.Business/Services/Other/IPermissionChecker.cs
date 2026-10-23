namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 判斷使用者的有效權限。作為 UI 與 API 共用的權限判定單一來源。
/// 權限鍵讀自 RBAC 關聯表（UserRole → RolePermissionMap → Permission），多角色取聯集；
/// 角色來源以 UserRole 為主，並容錯併入 legacy 的 MyUser.RoleViewId。
/// ⚠️ RoleView.TabViewJson 已退為角色編輯畫面的回填欄位，**不參與**權限判定。
/// </summary>
public interface IPermissionChecker
{
    /// <summary>使用者是否具備指定權限鍵（管理員一律為 true）。</summary>
    Task<bool> HasPermissionAsync(int userId, string permissionKey);

    /// <summary>取得使用者的有效權限鍵集合（不含管理員全通過的隱含權限）。</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId);
}
