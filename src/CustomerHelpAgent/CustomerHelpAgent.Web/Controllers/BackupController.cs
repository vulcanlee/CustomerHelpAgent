using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Backup;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// 系統備份下載（0.9.99 起，管理員專屬）。
///
/// 只收 Cookie 驗證（呼叫端是管理頁上的連結）；Cookie 沒有「是否為管理員」的宣告，所以<b>每次從資料庫確認</b>。
/// ⚠️ 備份含全部資料與 Data Protection 金鑰環，拿到的人可以偽造登入 Cookie。成功回原生檔案（支援續傳），錯誤一律回 <see cref="ApiResult"/>。
/// </summary>
[Route("api/backups")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class BackupController : ControllerBase
{
    private readonly BackupStore store;
    private readonly MyUserServiceLogin userLookup;
    private readonly IAuditLogService auditLogService;
    private readonly ILogger<BackupController> logger;

    public BackupController(BackupStore store, MyUserServiceLogin userLookup, IAuditLogService auditLogService, ILogger<BackupController> logger)
    {
        this.store = store;
        this.userLookup = userLookup;
        this.auditLogService = auditLogService;
        this.logger = logger;
    }

    [HttpGet("{fileName}/download")]
    public async Task<IActionResult> Download(string fileName)
    {
        var userId = int.TryParse(User.FindFirstValue(ClaimTypes.Sid), out var id) ? id : 0;
        var account = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var user = userId > 0 ? await userLookup.GetActiveUserAsync(userId) : null;
        if (user is not { IsAdmin: true })
        {
            logger.LogWarning("Backup download denied because the user is not an administrator. UserId={UserId}", userId);
            return StatusCode(StatusCodes.Status403Forbidden, ApiResult.FailureResult("只有管理員可以下載系統備份。", StatusCodes.Status403Forbidden));
        }

        var path = store.TryResolveFullPath(fileName);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound(ApiResult.NotFoundResult("找不到這份備份。"));
        }

        // 續傳的後續區段不重複寫稽核（一次下載只記一筆）。
        if (IsFirstRange(Request.Headers.Range.ToString()))
        {
            await WriteAuditAsync(userId, account, fileName);
        }

        logger.LogInformation("Backup download started. FileName={FileName}, UserId={UserId}", fileName, userId);
        return PhysicalFile(path, "application/zip", fileName, enableRangeProcessing: true);
    }

    /// <summary>沒有 Range 標頭、或從第 0 個位元組開始，才算一次新的下載。</summary>
    internal static bool IsFirstRange(string? range)
        => string.IsNullOrWhiteSpace(range) || range.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);

    private async Task WriteAuditAsync(int userId, string account, string fileName)
    {
        try
        {
            await auditLogService.WriteAsync(
                AuditActions.Backup.Download, success: true, actorUserId: userId, actorAccount: account,
                targetType: "Backup", targetId: fileName, detail: $"file={fileName}");
        }
        catch (Exception ex)
        {
            // 稽核寫不進去不該讓管理員拿不到備份（比照附件下載的 fail-open）。
            logger.LogError(ex, "Failed to write backup download audit log. FileName={FileName}", fileName);
        }
    }
}
