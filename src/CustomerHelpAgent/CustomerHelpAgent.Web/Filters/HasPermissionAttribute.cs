using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Filters;

/// <summary>
/// API 功能級授權：要求目前使用者具備指定權限鍵，否則回傳 ApiResult 403。
/// 與 UI 共用 <see cref="IPermissionChecker"/> 作為權限判定來源。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string permissionKey;

    /// <summary>頁面級（裸鍵）授權。</summary>
    public HasPermissionAttribute(string permissionKey)
    {
        this.permissionKey = permissionKey;
    }

    /// <summary>動作級授權，權限鍵為「頁面:動作」。</summary>
    public HasPermissionAttribute(string page, string action)
    {
        this.permissionKey = PermissionKey.For(page, action);
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILogger<HasPermissionAttribute>>();
        var path = context.HttpContext.Request.Path.Value;

        var principal = context.HttpContext.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            // 匿名請求在這裡就被擋掉，不會進到下面的權限檢查 ——
            // 因此稽核寫入只會發生在已登入的使用者身上，匿名流量無法灌爆稽核表。
            logger.LogWarning(
                "API request rejected because the caller is not authenticated. PermissionKey={PermissionKey}, Path={Path}",
                permissionKey, path);

            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("尚未登入或憑證無效。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var (userId, account) = ResolveActor(principal);
        if (userId <= 0)
        {
            logger.LogWarning(
                "API request rejected because the user identifier could not be parsed. PermissionKey={PermissionKey}, Path={Path}",
                permissionKey, path);

            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("無法識別使用者身分。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var checker = services.GetRequiredService<IPermissionChecker>();
        if (!await checker.HasPermissionAsync(userId, permissionKey))
        {
            // 權限被拒屬於「不尋常」的事件：正常使用者不會去打自己沒有權限的端點。
            logger.LogWarning(
                "API request denied by permission check. UserId={UserId}, Account={Account}, PermissionKey={PermissionKey}, Path={Path}",
                userId, account, permissionKey, path);

            await WriteAuditAsync(services, logger, userId, account, path);

            context.Result = new ObjectResult(ApiResult.ForbiddenResult($"目前使用者沒有「{permissionKey}」權限。"))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        logger.LogDebug(
            "API request passed permission check. UserId={UserId}, PermissionKey={PermissionKey}, Path={Path}",
            userId, permissionKey, path);
    }

    /// <summary>
    /// 取出「是誰」——本屬性同時掛在 JWT 與 Cookie 兩種端點上，而**兩套 claim 對應是相反的**：
    ///   JWT   ：NameIdentifier=UserId、Name=帳號、display_name=姓名
    ///   Cookie：NameIdentifier=帳號、Sid=UserId、Name=**姓名（個資）**
    ///
    /// 因此以「NameIdentifier 是不是數字」來判斷來源，並且 <see cref="ClaimTypes.Name"/>
    /// 只在確定是 JWT 的那一支取用 —— Cookie 這一支永遠不碰它，否則會把使用者姓名寫進
    /// 日誌與稽核表的 ActorAccount。可記錄的身分資訊只有帳號與 UserId。
    /// </summary>
    private static (int UserId, string Account) ResolveActor(ClaimsPrincipal principal)
    {
        var nameIdentifier = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (int.TryParse(nameIdentifier, out var jwtUserId) && jwtUserId > 0)
        {
            return (jwtUserId, principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty);
        }

        if (int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var cookieUserId) && cookieUserId > 0)
        {
            return (cookieUserId, nameIdentifier ?? string.Empty);
        }

        return (0, string.Empty);
    }

    /// <summary>
    /// 稽核寫入失敗不應影響「請求已被拒絕」這個結果，因此只記錯誤不向外拋。
    /// </summary>
    private async Task WriteAuditAsync(
        IServiceProvider services, ILogger logger, int userId, string account, string? path)
    {
        try
        {
            var auditLogService = services.GetRequiredService<IAuditLogService>();
            await auditLogService.WriteAsync(
                AuditActions.Permission.Denied,
                success: false,
                actorUserId: userId,
                actorAccount: account,
                targetType: "ApiEndpoint",
                targetId: path,
                detail: $"缺少權限：{permissionKey}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write permission-denied audit log. UserId={UserId}", userId);
        }
    }
}
