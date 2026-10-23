using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// 售後申請照片下載（售後展示 M1）。比照 <see cref="ProjectFileController"/>：**只收 Cookie 驗證**，
/// 呼叫端是後台「售後審核」畫面上的一般連結。顧客本人也能經此端點下載自己的照片（擁有權檢查），
/// 但 M1 顧客端「我的申請」畫面沒有照片連結。
///
/// 不掛 <c>[HasPermission]</c>：顧客本人沒有審核權限也要能看自己的照片。存取規則在
/// <see cref="AfterSalesRequestService.GetAttachmentForAsync"/>：申請人本人，或具備「售後審核」檢視權限者（管理員短路）。
/// 查無紀錄、不是本人也不是審核者、實體檔不存在一律回 404，不讓外部以狀態碼推斷哪些 Id 存在。
/// </summary>
[Route("api/after-sales-files")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class AfterSalesFileController : ControllerBase
{
    private readonly ILogger<AfterSalesFileController> logger;
    private readonly AfterSalesRequestService afterSalesRequestService;
    private readonly IPermissionChecker permissionChecker;
    private readonly IAuditLogService auditLogService;

    public AfterSalesFileController(
        ILogger<AfterSalesFileController> logger,
        AfterSalesRequestService afterSalesRequestService,
        IPermissionChecker permissionChecker,
        IAuditLogService auditLogService)
    {
        this.logger = logger;
        this.afterSalesRequestService = afterSalesRequestService;
        this.permissionChecker = permissionChecker;
        this.auditLogService = auditLogService;
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> Download(int id)
    {
        var (userId, account) = ResolveActor();
        logger.LogDebug("Received after-sales file download request. AttachmentId={AttachmentId}, UserId={UserId}", id, userId);

        try
        {
            var canReview = userId > 0 && await permissionChecker.HasPermissionAsync(
                userId, PermissionKey.For(MagicObjectHelper.角色_售後審核, PermissionActions.View));
            var file = await afterSalesRequestService.GetAttachmentForAsync(id, userId, canReview);
            if (file is null)
            {
                logger.LogInformation("After-sales file download returned no content. AttachmentId={AttachmentId}, UserId={UserId}", id, userId);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的售後照片"));
            }

            await WriteAuditAsync(id, userId, account, canReview);
            logger.LogInformation(
                "After-sales file downloaded. AttachmentId={AttachmentId}, UserId={UserId}, Account={Account}, ContentType={ContentType}",
                id, userId, account, file.Value.ContentType);

            // 成功回原生檔案（不包 ApiResult），錯誤才回 ApiResult —— 同 ProjectFileController。
            // 以 inline 回應：審核窗點縮圖要在新分頁直接看原圖，而不是下載。上傳端已限制只收點陣圖（jpg／png／webp），
            // 不會有 HTML／SVG 被瀏覽器當成頁面執行。檔名走 filename*（UTF-8），中文檔名不會變亂碼。
            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.FileNameStar = file.Value.FileName;
            Response.Headers.ContentDisposition = disposition.ToString();
            return PhysicalFile(file.Value.FullPath, file.Value.ContentType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download after-sales file. AttachmentId={AttachmentId}, UserId={UserId}", id, userId);
            return this.ApiServerError("下載售後照片失敗", ex);
        }
    }

    /// <summary>Cookie 驗證：Sid=使用者編號、NameIdentifier=帳號；ClaimTypes.Name 是姓名（個資），刻意不取用。</summary>
    private (int UserId, string Account) ResolveActor()
    {
        var userId = int.TryParse(User.FindFirstValue(ClaimTypes.Sid), out var id) ? id : 0;
        return (userId, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty);
    }

    /// <summary>照片可能拍到個人物品，成功下載留下長期軌跡；稽核失敗不擋下載（fail-open，同專案附件）。</summary>
    private async Task WriteAuditAsync(int attachmentId, int userId, string account, bool canReview)
    {
        try
        {
            await auditLogService.WriteAsync(
                AuditActions.Shop.AttachmentDownload,
                success: true,
                actorUserId: userId,
                actorAccount: account,
                targetType: "AfterSalesAttachment",
                targetId: attachmentId.ToString(CultureInfo.InvariantCulture),
                detail: canReview ? "as=reviewer" : "as=owner");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write after-sales file download audit log. AttachmentId={AttachmentId}", attachmentId);
        }
    }
}
