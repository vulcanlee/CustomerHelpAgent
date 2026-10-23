namespace CustomerHelpAgent.Share.Helpers;

public class MagicObjectHelper
{
    #region 系統層面用到神奇字串
    public const string DefaultSQLiteConnectionStringKey = "SQLiteDefaultConnection";
    public const string SQLiteDatabaseFilename = "BackendDB.db";
    public static string GetSQLiteConnectionString(string databasePath)
    {
        return $"Data Source={Path.Combine(databasePath, SQLiteDatabaseFilename)}";
    }
    public const string CookieScheme = "CookieAuthenticationScheme";
    /// <summary>
    /// OAuth 外部登入流程暫存身分用的 Cookie 配置名稱
    /// </summary>
    public const string ExternalCookieScheme = "ExternalCookieScheme";

    /// <summary>
    /// 登入 Cookie 與 JWT 裡的工作階段版本 claim（0.9.103 起）。值是 <c>MyUser.SecurityStamp</c>；
    /// 與資料庫不符代表這個工作階段已失效（改密碼、停用、刪除、角色變更、管理員強制登出）。
    /// </summary>
    public const string SecurityStampClaimType = "security_stamp";

    /// <summary>
    /// Data Protection 的應用程式識別名稱，決定金鑰環的「用途」判別子。
    ///
    /// ⚠️ **改掉這個值等於讓全站所有既有登入 Cookie 立刻失效**（連同記住我）。
    /// 不設定的話框架會改用 content root 路徑當判別子，於是**換部署路徑也會失效** ——
    /// 這正是把它固定成常數的理由。
    /// ⚠️ 腳手架改名時**不要**跟著改；若真的要改，請安排在可以接受全體重新登入的時機。
    /// </summary>
    public const string DataProtectionApplicationName = "CustomerHelpAgent";

    public const string 開發者帳號 = "support";
    public const string 預設角色 = "預設角色";
    /// <summary>
    /// 0.9.100 之前代表「必須變更密碼」的哨兵密碼。0.9.101 起改用 <c>MyUser.MustChangePassword</c> 旗標，
    /// 這個常數只用在登入成功時替仍使用它的舊帳號補上旗標；新程式不可再以密碼內容表達狀態。
    /// </summary>
    public const string NeedChangePassword = "123456";

    public static readonly int PageSize = 8;

    public const string Menu結構定義 = "Datas/Menu.json";
    public const string 頁面說明索引定義 = "Datas/HelpTopics.json";
    public const string 頁面說明內容目錄 = "Datas/Help";
    public const string SignoutUrl = "/auths/logout";
    #endregion

    #region 角色
    public const string 角色_首頁 = "首頁";
    public const string 角色_專案管理 = "專案管理功能";
    public const string 角色_專案項目 = "專案項目";
    /// <summary>
    /// 系統管理群組。與「監控與診斷」相同，刻意不列入
    /// <c>RolePermissionService.GetRoleListPermissionAllName()</c>：不種 Permission 資料列、
    /// 角色矩陣不顯示、任何角色都無法被授予，僅由 <c>CheckIsAdmin()</c> 通過。
    ///
    /// 沿革：0.4.33 之前這三個鍵有上架角色矩陣，但 MyUserView / RoleViewView 實際是以
    /// CheckIsAdmin() 守門，形成「勾得到卻永遠無效」的死權限。已由
    /// <c>AdminOnlyPermissionTests</c> 守住，請勿補上。
    /// </summary>
    public const string 角色_系統管理 = "系統管理功能";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_帳號與權限 = "帳號與權限";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_AI管理 = "AI 管理功能";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_系統設定與維運 = "系統設定與維運功能";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_使用者管理 = "使用者管理";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_角色管理 = "角色管理";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_稽核紀錄 = "稽核紀錄";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_系統參數 = "系統參數";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_系統備份 = "系統備份";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_資料保留與清理 = "資料保留與清理";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_公告管理 = "公告管理";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_AI提示詞 = "AI 提示詞";

    /// <inheritdoc cref="角色_系統管理"/>
    public const string 角色_系統例外紀錄 = "系統例外紀錄";
    public const string 角色_資料定義 = "資料定義管理功能";
    public const string 角色_分類清單 = "分類清單";
    public const string 角色_團隊清單 = "團隊清單";
    public const string 角色_登出 = "登出";

    /// <summary>
    /// 監控與診斷群組（0.9.113 前名為「統計與分析」）。刻意不列入 <c>RolePermissionService.GetRoleListPermissionAllName()</c>：
    /// 不種 Permission 資料列、角色矩陣不顯示、任何角色都無法被授予；
    /// 僅由 <c>AuthenticationStateHelper.CheckAccessPage</c> 的管理員短路通過。
    /// 這是讓「日誌檢視」成為管理員專屬頁面的機制，不是漏掉的步驟，請勿補上。
    /// </summary>
    public const string 角色_監控與診斷 = "監控與診斷功能";

    /// <inheritdoc cref="角色_監控與診斷"/>
    public const string 角色_系統健康監控 = "系統健康監控";
    /// <inheritdoc cref="角色_監控與診斷"/>
    public const string 角色_日誌檢視 = "日誌檢視";

    /// <inheritdoc cref="角色_監控與診斷"/>
    public const string 角色_資料庫用量 = "資料庫用量";

    /// <inheritdoc cref="角色_監控與診斷"/>
    public const string 角色_日誌等級設定 = "日誌等級設定";

    /// <summary>「AI 管理」子群組（0.9.113 起）的管理員專屬頁面；不授予任何角色的機制同 <see cref="角色_監控與診斷"/>。</summary>
    public const string 角色_Token用量 = "Token 用量";

    /// <inheritdoc cref="角色_Token用量"/>
    public const string 角色_AI對話紀錄 = "AI 對話紀錄";

    /// <summary>「系統設定與維運」子群組（0.9.113 起）的管理員專屬頁面；不授予任何角色的機制同 <see cref="角色_監控與診斷"/>。</summary>
    public const string 角色_排程作業 = "排程作業";
    public const string 使用者角色 = "使用者角色";

    #endregion

    #region 認證與授權
    public const string 你沒有權限存取此頁面 = "你沒有權限存取此頁面";

    #endregion
}
