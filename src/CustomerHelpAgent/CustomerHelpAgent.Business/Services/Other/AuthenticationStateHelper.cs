using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Admins;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Share.Helpers;
using System.Security.Claims;
using System.Text.Json;

namespace CustomerHelpAgent.Business.Services.Other;

public class AuthenticationStateHelper
{
    private readonly ILogger<AuthenticationStateHelper> logger;
    private readonly IMapper mapper;
    private readonly MyUserService myUserService;
    private readonly CurrentUserService currentUserService;
    private readonly RolePermissionService rolePermissionService;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPermissionChecker permissionChecker;
    private readonly IAuditLogService auditLogService;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ISecurityStampService securityStampService;
    private readonly ITwoFactorService twoFactorService;

    /// <summary>
    /// 換頁時工作階段版本的查詢可以沿用幾秒內的結果：同一次換頁裡版面、選單、頁面各檢查一次，不必各查一次資料庫。
    /// 同一個行程的強制登出會立即清掉快取，所以這段時間只影響別的行程。
    /// </summary>
    private static readonly TimeSpan SessionRecheckWindow = TimeSpan.FromSeconds(5);

    public AuthenticationStateHelper(
        ILogger<AuthenticationStateHelper> logger,
        IMapper mapper,
        MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPermissionChecker permissionChecker,
        IAuditLogService auditLogService,
        IPasswordPolicy passwordPolicy,
        ISecurityStampService securityStampService,
        ITwoFactorService twoFactorService)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.permissionChecker = permissionChecker;
        this.auditLogService = auditLogService;
        this.passwordPolicy = passwordPolicy;
        this.securityStampService = securityStampService;
        this.twoFactorService = twoFactorService;
    }

    public async Task<AuthenticationCheckResult> Check(AuthenticationStateProvider authStateProvider, NavigationManager navigationManager)
    {
        logger.LogDebug("Checking authentication state for current request.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Authentication check failed because the current principal is not authenticated.");
            await Task.Delay(200);

            // ⚠️ 這裡導向登入頁而**不是**登出頁（0.9.39 起），其餘分支則維持導向登出。
            //
            // 原因：/Auths/Logout 會無條件呼叫 SignOutAsync 刪掉 Cookie。而「未認證」這個
            // 分支同時涵蓋「Cookie 一時讀不出來」（伺服器重啟、Data Protection 金鑰換掉、
            // 換連接埠或網域）—— 那種情況下把 Cookie 毀掉，會讓使用者原本還有效的
            // 「記住我」永久消失，症狀還會自我延續（每次回來都重演一次）。
            // 真正失效的 Cookie 由 Cookie handler 自己清理，不需要我們動手。
            //
            // ⚠️ 不會產生重導迴圈：登入頁用的是 NoFooterLayout，而那個版面不呼叫本方法
            //（只有 MainLayout 與 NavMenu 會）。**若日後改動登入頁的版面，這個保證就沒了。**
            navigationManager.NavigateTo("/Auths/Login", true, true);
            return AuthenticationCheckResult.Unauthenticated;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Authentication check failed because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        var myUser = await myUserService.GetAsync(id);
        if (myUser.Id == 0)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} was not found.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        // ⚠️ 工作階段版本在「帳號存在」之後、其他檢查之前比（0.9.103 起）：改密碼、停用、角色變更、強制登出之後，已開著的頁面在下一次換頁被登出。
        // 已開著的 Blazor 連線不經過 Cookie 驗證器，只能靠這裡。升級前簽發的 Cookie 沒有這個 claim，會被登出一次。
        // 這裡只比版本；停用由下方的檢查處理（結果不同：InvalidUser）。
        if (!await securityStampService.IsValidAsync(id, user.FindFirst(MagicObjectHelper.SecurityStampClaimType)?.Value, SessionRecheckWindow, requireActive: false))
        {
            logger.LogInformation("Authentication check failed because the session was revoked. UserId={UserId}", id);
            await auditLogService.WriteAsync(
                AuditActions.Login.SessionExpired, success: false, actorUserId: id, actorAccount: user.FindFirst(ClaimTypes.NameIdentifier)?.Value, detail: "reason=SessionRevoked");
            navigationManager.NavigateTo("/Auths/Logout?reason=session", true, true);
            return AuthenticationCheckResult.SessionRevoked;
        }

        if (!myUser.Status)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} is disabled.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        logger.LogDebug("Resolved authenticated user information for UserId={UserId}.", id);

        // 0.9.101 起只看旗標與到期時間（不再每次換頁都以 PBKDF2 比對 123456）。
        bool needChangePassword = passwordPolicy.RequiresChange(myUser.Account, myUser.HasLocalPassword, myUser.MustChangePassword, myUser.PasswordChangedAtUtc);
        if (needChangePassword && !IsChangePasswordPage(navigationManager))
        {
            logger.LogWarning("User {UserId} is required to change password before continuing.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/ChangePassword", true);
            return AuthenticationCheckResult.RequiresPasswordChange;
        }

        // 必須使用兩步驟驗證卻還沒設定（0.9.104 起）：在改密碼之後判斷，而且兩個頁面互不導向，否則會來回跳。
        if (!needChangePassword && !myUser.TwoFactorEnabled && !IsPage(navigationManager, "TwoFactorSetup")
            && await twoFactorService.IsRequiredAsync(id))
        {
            logger.LogInformation("User {UserId} must set up two-factor authentication before continuing.", id);
            navigationManager.NavigateTo("/TwoFactorSetup", true);
            return AuthenticationCheckResult.RequiresTwoFactorSetup;
        }

        CurrentUser currentUser = mapper.Map<CurrentUser>(myUser);
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();

        if (myUser.RoleView == null)
        {
            logger.LogWarning("User {UserId} does not have a role view assigned. Redirecting to logout.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        try
        {
            List<string> permissions = JsonSerializer.Deserialize<List<string>>(myUser.RoleView.TabViewJson) ?? [];
            rolePermissionService.SetPermissionInput(rolePermission, permissions);
            currentUser.RoleJson = myUser.RoleView.TabViewJson;
            currentUser.TeamList = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(myUser.Id)).ToList();
            currentUser.IsAuthenticated = true;
            currentUserService.CurrentUser.CopyFrom(currentUser);

            // UI 權限判定改以 RBAC 表為單一權威來源（含多角色聯集），與 API 端 IPermissionChecker 對齊；
            // 覆寫 CopyFrom 由 TabViewJson 反序列化得到的 RoleList。TabViewJson 僅保留供角色編輯畫面回填。
            currentUserService.CurrentUser.RoleList =
                (await permissionChecker.GetEffectivePermissionKeysAsync(myUser.Id)).ToList();
            currentUserService.NotifyChanged();

            // 每次導覽都會跑，屬於流程細節而非使用者意圖，因此記在 Debug。
            logger.LogDebug(
                "Authentication state initialized for UserId={UserId}, Account={Account}, IsAdmin={IsAdmin}.",
                myUser.Id,
                myUser.Account,
                myUser.IsAdmin);

            return AuthenticationCheckResult.Succeeded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize permission data for UserId={UserId}.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }
    }

    private static bool IsChangePasswordPage(NavigationManager navigationManager) => IsPage(navigationManager, "ChangePassword");

    private static bool IsPage(NavigationManager navigationManager, string page)
    {
        var currentPath = new Uri(navigationManager.Uri).AbsolutePath.Trim('/');
        return string.Equals(currentPath, page, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MyUserAdapterModel?> GetUserInformation(AuthenticationStateProvider authStateProvider)
    {
        logger.LogDebug("Loading current user information from authentication state.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Cannot load current user information because the principal is not authenticated.");
            return null;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Cannot load current user information because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            return null;
        }

        return await myUserService.GetAsync(id);
    }

    /// <summary>
    /// 記錄 Blazor 頁面的權限拒絕（LOG-14），與 API 的 <c>HasPermissionAttribute</c> 共用同一個動作代碼。
    ///
    /// ⚠️ 不寫在 <see cref="CheckIsAdmin"/>／<see cref="CheckAccessPage"/> 裡：它們也被側邊欄用來過濾選單，
    /// 寫在那裡會讓每個被隱藏的選單項都記一筆「拒絕」。只在頁面真的擋下使用者時呼叫。
    /// </summary>
    public Task RecordPageAccessDeniedAsync(string page)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            AuditActions.Permission.Denied,
            success: false,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: "Page",
            targetId: page,
            detail: "via=Blazor");
    }

    public bool CheckIsAdmin()
    {
        var isAdmin = currentUserService.CurrentUser.IsAdmin;
        logger.LogDebug("Checked admin permission for UserId={UserId}. IsAdmin={IsAdmin}", currentUserService.CurrentUser.Id, isAdmin);
        return isAdmin;
    }

    /// <summary>
    /// 檢查目前使用者能否進入某個頁面（也用於側邊選單過濾）。
    ///
    /// 通過條件：管理員、擁有裸頁面鍵、或擁有該頁的 <c>view</c> 動作鍵。
    /// ⚠️ 第三項不可省略 —— 角色矩陣只勾「檢視」時產生的是「頁面:view」，**不含裸鍵**，
    /// 少了這一項，唯讀角色會連頁面都打不開、選單也不顯示，「可看不可改」就只剩 API 端生效。
    /// 這是 <see cref="CheckAccessAction"/>「動作鍵不中則退回裸鍵」的鏡像。
    /// </summary>
    public bool CheckAccessPage(string name)
    {
        // 管理員一律通過：GetEffectivePermissionKeysAsync 不含 admin 隱含全通過（回空集合），
        // 故此處需比照 CheckAccessAction 短路，否則管理員選單/頁面會全被隱藏。
        if (currentUserService.CurrentUser.IsAdmin)
        {
            return true;
        }

        var keys = currentUserService.CurrentUser.RoleList;
        var result = keys.Contains(name)
            || keys.Contains(PermissionKey.For(name, PermissionActions.View));
        logger.LogDebug(
            "Checked page access for UserId={UserId}, Page={PageName}, Allowed={Allowed}.",
            currentUserService.CurrentUser.Id,
            name,
            result);
        return result;
    }

    /// <summary>
    /// 檢查目前使用者是否具備某頁面的特定動作權限（view/create/edit/delete）。
    /// 管理員一律通過；擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。
    /// 規則在 <see cref="PermissionRules.CanAccessAction"/>；Razor 檢視請改用 <c>&lt;RequirePermission&gt;</c> 元件（0.9.110 起）。
    /// </summary>
    public bool CheckAccessAction(string page, string action)
        => PermissionRules.CanAccessAction(currentUserService.CurrentUser, page, action);
}
