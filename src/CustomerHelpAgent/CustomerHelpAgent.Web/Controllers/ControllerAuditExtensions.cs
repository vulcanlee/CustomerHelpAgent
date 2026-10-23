using Microsoft.AspNetCore.Mvc;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// Web API 寫稽核（LOG-14）。
///
/// API 的 Controller 走 Repository 而不是 Blazor 用的 DataAccess 服務，<see cref="CurrentUserService"/> 在 API 情境是空的，
/// 所以操作者一律由 JWT／Cookie 身分經 <see cref="RequestActorResolver"/> 解析。
/// 稽核寫入失敗不拋出（<see cref="AuditLogService"/> 本身即如此），不影響 API 回應。
/// </summary>
public static class ControllerAuditExtensions
{
    public static Task WriteAuditAsync(
        this ControllerBase controller,
        string action,
        string targetType,
        string? targetId,
        string? detail = null,
        bool success = true,
        string? content = null)
    {
        var (account, userId) = RequestActorResolver.Resolve(controller.User);
        var auditLogService = controller.HttpContext.RequestServices.GetRequiredService<IAuditLogService>();

        return auditLogService.WriteAsync(
            action,
            success: success,
            actorUserId: userId,
            actorAccount: account,
            targetType: targetType,
            targetId: targetId,
            detail: detail,
            content: content);
    }
}
