using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Filters;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// 專案附件下載。
///
/// 這個端點**只收 Cookie 驗證**，與其他走 JWT 的 Web API 不同：呼叫端是 Blazor 畫面上的
/// 一般連結（&lt;a href&gt;），由瀏覽器直接導覽，帶的是登入 Cookie 而不是 Bearer token。
///
/// 路由明確寫成 kebab-case，而非慣用的 api/[controller]，是為了維持 UI 既有的網址
/// /api/project-files/{id}/download。
/// </summary>
[Route("api/project-files")]
[Route("api/v1/project-files")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class ProjectFileController : ControllerBase
{
    private readonly ILogger<ProjectFileController> logger;
    private readonly ProjectService projectService;
    private readonly IAuditLogService auditLogService;

    public ProjectFileController(
        ILogger<ProjectFileController> logger,
        ProjectService projectService,
        IAuditLogService auditLogService)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.auditLogService = auditLogService;
    }

    [HttpGet("{id}/download")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<IActionResult> Download(int id)
    {
        var (userId, account) = ResolveActor();
        logger.LogDebug(
            "Received project file download request. ProjectFileId={ProjectFileId}, UserId={UserId}",
            id, userId);

        ProjectService.ProjectFileDownloadResult? file = null;
        try
        {
            file = await projectService.GetFileDownloadAsync(id);

            if (file is null)
            {
                // 查無紀錄、團隊越界、實體檔案不存在三種情況都回 404 ——
                // 統一回應才不會讓外部用狀態碼推斷哪些 ProjectFileId 真的存在。
                // 三者的差異已由 ProjectService 分別以 Warning 記錄，維運端仍分得出來。
                logger.LogInformation(
                    "Project file download returned no content. ProjectFileId={ProjectFileId}, UserId={UserId}",
                    id, userId);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案附件"));
            }

            await WriteAuditAsync(id, userId, account, file.DownloadFileName);

            logger.LogInformation(
                "Project file downloaded. ProjectFileId={ProjectFileId}, UserId={UserId}, Account={Account}, ContentType={ContentType}",
                id, userId, account, file.ContentType);

            // 成功一律回原生 File stream（不包 ApiResult），錯誤才回 ApiResult ——
            // 見 docs/operations/正式部署與安全檢查清單.md 的既有原則。
            // enableRangeProcessing 讓 1GB 上限的附件可以續傳。
            return File(file.Content, file.ContentType, file.DownloadFileName, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            // GetFileDownloadAsync 回傳的是已開啟的 FileStream。走到這裡代表它不會被
            // File(...) 接手關閉，必須自己關掉，否則檔案控制代碼會外洩。
            file?.Content.Dispose();

            logger.LogError(ex, "Failed to download project file. ProjectFileId={ProjectFileId}, UserId={UserId}", id, userId);
            return this.ApiServerError("下載專案附件失敗", ex);
        }
    }

    /// <summary>
    /// 本端點走 Cookie 驗證：Sid=使用者編號、NameIdentifier=帳號。
    /// ClaimTypes.Name 在這裡是「姓名」，屬個資，刻意不取用。
    /// </summary>
    private (int UserId, string Account) ResolveActor()
    {
        var userId = int.TryParse(User.FindFirstValue(ClaimTypes.Sid), out var id) ? id : 0;
        return (userId, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty);
    }

    /// <summary>
    /// 附件內容可能敏感，因此成功下載要留下長期軌跡（日誌只保留 30 天）。
    /// 稽核寫入失敗不應害使用者拿不到檔案，沿用專案既有的 fail-open 作法。
    /// </summary>
    private async Task WriteAuditAsync(int projectFileId, int userId, string account, string fileName)
    {
        try
        {
            await auditLogService.WriteAsync(
                AuditActions.Project.FileDownload,
                success: true,
                actorUserId: userId,
                actorAccount: account,
                targetType: "ProjectFile",
                targetId: projectFileId.ToString(),
                detail: $"file={fileName}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write project file download audit log. ProjectFileId={ProjectFileId}", projectFileId);
        }
    }
}
