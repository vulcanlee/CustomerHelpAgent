using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 售後申請服務：顧客送件／補件、客服審核佇列、要求補件、核准／駁回、附件存取檢查。
/// ⚠️ 顧客端方法一律以 <c>customerUserId</c> 做擁有權檢查（M2 Agent 也會呼叫 <see cref="SubmitAsync"/>）；
/// 客服的狀態變更以 <c>ExecuteUpdateAsync</c> 加上狀態條件，避免兩位客服同時處理同一筆。
/// </summary>
public class AfterSalesRequestService
{
    public const int MaxFileCount = 3;
    public const long MaxFileSize = 10 * 1024 * 1024;
    public const string StatusChangedMessage = "這筆申請的狀態已經變更，請重新整理後再操作。";
    private const string SameVariantMessage = "目標規格與原規格相同";
    private const string FileTooLargeMessage = "照片超過 10 MB";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AfterSalesPolicyService policyService;
    private readonly ShopOrderService shopOrderService;
    private readonly AfterSalesFileStore fileStore;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AfterSalesRequestService> logger;

    public AfterSalesRequestService(
        IDbContextFactory<BackendDBContext> contextFactory,
        AfterSalesPolicyService policyService,
        ShopOrderService shopOrderService,
        AfterSalesFileStore fileStore,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<AfterSalesRequestService> logger)
    {
        this.contextFactory = contextFactory;
        this.policyService = policyService;
        this.shopOrderService = shopOrderService;
        this.fileStore = fileStore;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 共用投影。⚠️ 顧客名稱不可經 <c>x.CustomerUser</c> 導覽取得：<see cref="MyUser"/> 有軟刪除過濾器、外鍵又是必要的，
    /// EF 會 INNER JOIN 而讓已刪除顧客的申請靜默消失（速查表 §7）；改以關掉過濾器的使用者集合明確查詢。
    /// </summary>
    private static Expression<Func<AfterSalesRequest, AfterSalesRequestView>> ToView(BackendDBContext context)
    {
        var users = context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]);
        return x => new AfterSalesRequestView
        {
            Id = x.Id,
            RequestNo = x.RequestNo,
            CaseKey = x.CaseKey,
            Type = x.Type,
            Status = x.Status,
            Channel = x.Channel,
            OrderNo = x.SalesOrderItem!.SalesOrder!.OrderNo,
            ProductName = x.SalesOrderItem.ProductVariant!.Product!.Name,
            ProductCategory = x.SalesOrderItem.ProductVariant.Product.Category,
            Color = x.SalesOrderItem.ProductVariant.Color,
            Size = x.SalesOrderItem.ProductVariant.Size,
            TargetSize = x.TargetSize,
            TargetColor = x.TargetColor,
            IsWorn = x.IsWorn,
            Description = x.Description,
            StaffNote = x.StaffNote,
            DeliveredAt = x.SalesOrderItem.SalesOrder.DeliveredAt,
            AppliedPolicyId = x.AppliedPolicyId,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt,
            CustomerName = users.Where(u => u.Id == x.CustomerUserId).Select(u => u.Name).FirstOrDefault() ?? string.Empty,
            Attachments = x.Attachments
                .OrderBy(a => a.Id)
                .Select(a => new AfterSalesAttachmentView(a.Id, a.OriginalFileName, a.FileSize))
                .ToList(),
        };
    }

    private static SubmitResult Fail(string message) => new(false, message, null, null);

    private static string? ValidateFiles(IReadOnlyList<AfterSalesUploadFile> files)
    {
        if (files.Count > MaxFileCount)
        {
            return $"照片最多 {MaxFileCount} 張。";
        }

        foreach (var file in files)
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(file.FileName)))
            {
                return "照片只接受 jpg、jpeg、png、webp 格式。";
            }

            if (file.FileSize > MaxFileSize || (file.Content.CanSeek && file.Content.Length > MaxFileSize))
            {
                return "每張照片不可超過 10 MB。";
            }
        }

        return null;
    }

    private DateTime Now() => timeProvider.GetLocalNow().DateTime;

    /// <summary>先寫檔；任何一張失敗就刪掉這一次已寫入的檔，並丟出例外。</summary>
    private async Task<List<AfterSalesAttachment>> SaveFilesAsync(IReadOnlyList<AfterSalesUploadFile> files, DateTime now)
    {
        var attachments = new List<AfterSalesAttachment>();
        try
        {
            foreach (var file in files)
            {
                var originalFileName = Path.GetFileName(file.FileName);
                var (relativePath, bytesWritten) = await fileStore.SaveAsync(file.Content, Path.GetExtension(originalFileName).ToLowerInvariant(), now, MaxFileSize);
                attachments.Add(new AfterSalesAttachment
                {
                    OriginalFileName = originalFileName,
                    StoredFileName = Path.GetFileName(relativePath),
                    RelativePath = relativePath,
                    // 不採用呼叫端宣稱的 ContentType，一律依（已通過白名單的）副檔名決定。
                    ContentType = UploadFileTypePolicy.ResolveContentType(originalFileName),
                    // 呼叫端宣稱的 FileSize 不可信，存實際寫入的位元組數。
                    FileSize = bytesWritten,
                    CreatedAt = now,
                });
            }

            return attachments;
        }
        catch
        {
            DeleteFiles(attachments);
            throw;
        }
    }

    private void DeleteFiles(IEnumerable<AfterSalesAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            fileStore.Delete(attachment.RelativePath);
        }
    }

    /// <summary>
    /// 換尺碼／換顏色的目標規格檢查：換尺碼是同顏色的其他尺碼、換顏色是同尺碼的其他顏色（與表單的選項一致），
    /// 而且必須真的存在於同款商品。其他類型不檢查。通過回 null。
    /// </summary>
    private async Task<string?> ValidateTargetVariantAsync(int customerUserId, SubmitAfterSalesInput input, ShopOrderItemView item)
    {
        if (input.Type is not (AfterSalesType.SizeExchange or AfterSalesType.ColorExchange))
        {
            return null;
        }

        var variants = await shopOrderService.GetVariantOptionsAsync(customerUserId, item.OrderItemId);
        if (input.Type == AfterSalesType.SizeExchange)
        {
            var size = input.TargetSize!.Trim();
            if (size == item.Size)
            {
                return SameVariantMessage;
            }

            return variants.Any(x => x.Color == item.Color && x.Size == size) ? null : "找不到這個尺碼";
        }

        var color = input.TargetColor!.Trim();
        if (color == item.Color)
        {
            return SameVariantMessage;
        }

        return variants.Any(x => x.Size == item.Size && x.Color == color) ? null : "找不到這個顏色";
    }

    /// <summary>同一個訂單商品、同類型、仍在處理中（已提交／待補件）的申請編號；沒有回 null。</summary>
    private static Task<string?> FindOpenRequestNoAsync(BackendDBContext context, int orderItemId, AfterSalesType type)
        => context.AfterSalesRequest
            .AsNoTracking()
            .Where(x => x.SalesOrderItemId == orderItemId
                && x.Type == type
                && (x.Status == AfterSalesStatus.Submitted || x.Status == AfterSalesStatus.NeedMoreInfo))
            .OrderBy(x => x.Id)
            .Select(x => (string?)x.RequestNo)
            .FirstOrDefaultAsync();

    private SubmitResult DuplicateFail(SubmitAfterSalesInput input, string openRequestNo)
    {
        logger.LogInformation(
            "After-sales submission rejected because an open request of the same type exists. OrderItemId={OrderItemId}, RequestType={RequestType}",
            input.OrderItemId, input.Type);
        return Fail($"這個商品已有處理中的同類申請（{openRequestNo}），請到「我的申請」查看。");
    }

    public async Task<SubmitResult> SubmitAsync(int customerUserId, SubmitAfterSalesInput input, IReadOnlyList<AfterSalesUploadFile> files)
    {
        var item = await shopOrderService.GetMyOrderItemAsync(customerUserId, input.OrderItemId);
        if (item is null)
        {
            return Fail("找不到這筆訂單");
        }

        if (input.Type == AfterSalesType.SizeExchange && string.IsNullOrWhiteSpace(input.TargetSize))
        {
            return Fail("換尺碼請選擇想換的尺碼。");
        }

        if (input.Type == AfterSalesType.ColorExchange && string.IsNullOrWhiteSpace(input.TargetColor))
        {
            return Fail("換顏色請選擇想換的顏色。");
        }

        var variantError = await ValidateTargetVariantAsync(customerUserId, input, item);
        if (variantError is not null)
        {
            logger.LogInformation("After-sales submission rejected by target variant validation. OrderItemId={OrderItemId}, RequestType={RequestType}", input.OrderItemId, input.Type);
            return Fail(variantError);
        }

        await using (var lookupContext = await contextFactory.CreateDbContextAsync())
        {
            if (await FindOpenRequestNoAsync(lookupContext, item.OrderItemId, input.Type) is { } openRequestNo)
            {
                return DuplicateFail(input, openRequestNo);
            }
        }

        var fileError = ValidateFiles(files);
        if (fileError is not null)
        {
            logger.LogInformation("After-sales submission rejected by file validation. OrderItemId={OrderItemId}, FileCount={FileCount}", input.OrderItemId, files.Count);
            return Fail(fileError);
        }

        var now = Now();
        var lookup = await policyService.GetEffectiveAsync(input.Type, item.ProductCategory, now);
        int? appliedPolicyId = lookup.Status == PolicyLookupStatus.Found ? lookup.Policies[0].Id : null;

        List<AfterSalesAttachment> attachments;
        try
        {
            attachments = await SaveFilesAsync(files, now);
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("After-sales submission rejected because a file exceeds the size limit. OrderItemId={OrderItemId}", input.OrderItemId);
            return Fail(FileTooLargeMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save after-sales files. OrderItemId={OrderItemId}", input.OrderItemId);
            return Fail("照片儲存失敗，請稍後再試。");
        }

        AfterSalesRequest row;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();
            // 交易內再查一次：上面的檢查到這裡之間（寫檔期間）可能有另一個送出已經成功。
            if (await FindOpenRequestNoAsync(context, item.OrderItemId, input.Type) is { } openRequestNo)
            {
                DeleteFiles(attachments);
                return DuplicateFail(input, openRequestNo);
            }

            row = new AfterSalesRequest
            {
                // 編號要用到 Id，先放唯一的暫時值，取得 Id 後在同一個交易內改成正式編號。
                RequestNo = Guid.NewGuid().ToString("N"),
                CaseKey = input.CaseKey,
                CustomerUserId = customerUserId,
                SalesOrderItemId = item.OrderItemId,
                Type = input.Type,
                Status = AfterSalesStatus.Submitted,
                Channel = input.Channel,
                TargetSize = input.Type == AfterSalesType.SizeExchange ? input.TargetSize!.Trim() : null,
                TargetColor = input.Type == AfterSalesType.ColorExchange ? input.TargetColor!.Trim() : null,
                IsWorn = input.IsWorn,
                Description = input.Description.Trim(),
                AppliedPolicyId = appliedPolicyId,
                CreatedAt = now,
                UpdatedAt = now,
                Attachments = attachments,
            };
            context.AfterSalesRequest.Add(row);
            await context.SaveChangesAsync();
            row.RequestNo = string.Create(CultureInfo.InvariantCulture, $"AS{now:yyyyMMdd}{row.Id:D4}");
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            DeleteFiles(attachments);
            logger.LogError(ex, "Failed to submit after-sales request. OrderItemId={OrderItemId}", input.OrderItemId);
            return Fail("送出售後申請失敗，請稍後再試。");
        }

        logger.LogInformation(
            "After-sales request submitted. RequestId={RequestId}, RequestType={RequestType}, Channel={Channel}, AttachmentCount={AttachmentCount}, AppliedPolicyId={AppliedPolicyId}",
            row.Id, row.Type, row.Channel, attachments.Count, appliedPolicyId);
        await auditLogService.WriteAsync(
            AuditActions.Shop.RequestSubmit,
            success: true,
            actorUserId: customerUserId,
            actorAccount: ActorAccountFor(customerUserId),
            targetType: nameof(AfterSalesRequest),
            targetId: row.Id.ToString(CultureInfo.InvariantCulture),
            detail: $"requestNo={row.RequestNo}, type={row.Type}, channel={row.Channel}");
        return new SubmitResult(true, "已送出售後申請。", row.Id, row.RequestNo);
    }

    public async Task<IReadOnlyList<AfterSalesRequestView>> GetMyRequestsAsync(int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AfterSalesRequest
            .AsNoTracking()
            .Where(x => x.CustomerUserId == customerUserId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(ToView(context))
            .ToListAsync();
    }

    public async Task<SubmitResult> ResubmitAsync(int customerUserId, int requestId, bool? isWorn, string supplement, IReadOnlyList<AfterSalesUploadFile> files)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var current = await context.AfterSalesRequest
            .AsNoTracking()
            .Where(x => x.Id == requestId && x.CustomerUserId == customerUserId)
            .Select(x => new { x.RequestNo, x.Status })
            .FirstOrDefaultAsync();
        if (current is null)
        {
            logger.LogInformation("After-sales request not found for customer. RequestId={RequestId}", requestId);
            return Fail("找不到這筆申請。");
        }

        if (current.Status != AfterSalesStatus.NeedMoreInfo)
        {
            return Fail("這筆申請目前不需要補件。");
        }

        var text = supplement?.Trim() ?? string.Empty;
        if (text.Length == 0 && files.Count == 0)
        {
            return Fail("請填寫補充說明或上傳照片。");
        }

        var fileError = ValidateFiles(files);
        if (fileError is not null)
        {
            return Fail(fileError);
        }

        var now = Now();
        List<AfterSalesAttachment> attachments;
        try
        {
            attachments = await SaveFilesAsync(files, now);
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("After-sales resubmission rejected because a file exceeds the size limit. RequestId={RequestId}", requestId);
            return Fail(FileTooLargeMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save after-sales files. RequestId={RequestId}", requestId);
            return Fail("照片儲存失敗，請稍後再試。");
        }

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var appended = text.Length == 0 ? string.Empty : $"\n［補充］{text}";
            // 以狀態為條件更新：重複送出（連點）只有第一次會成功。
            var affected = await context.AfterSalesRequest
                .Where(x => x.Id == requestId && x.CustomerUserId == customerUserId && x.Status == AfterSalesStatus.NeedMoreInfo)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(x => x.Description, x => x.Description + appended);
                    s.SetProperty(x => x.Status, AfterSalesStatus.Submitted);
                    s.SetProperty(x => x.UpdatedAt, now);
                    if (isWorn.HasValue)
                    {
                        s.SetProperty(x => x.IsWorn, isWorn);
                    }
                });
            if (affected == 0)
            {
                DeleteFiles(attachments);
                return Fail(StatusChangedMessage);
            }

            foreach (var attachment in attachments)
            {
                attachment.AfterSalesRequestId = requestId;
            }

            context.AfterSalesAttachment.AddRange(attachments);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            DeleteFiles(attachments);
            logger.LogError(ex, "Failed to resubmit after-sales request. RequestId={RequestId}", requestId);
            return Fail("補件送出失敗，請稍後再試。");
        }

        logger.LogInformation("After-sales request resubmitted. RequestId={RequestId}, AttachmentCount={AttachmentCount}", requestId, attachments.Count);
        await auditLogService.WriteAsync(
            AuditActions.Shop.RequestResubmit,
            success: true,
            actorUserId: customerUserId,
            actorAccount: ActorAccountFor(customerUserId),
            targetType: nameof(AfterSalesRequest),
            targetId: requestId.ToString(CultureInfo.InvariantCulture),
            detail: $"requestNo={current.RequestNo}, attachments={attachments.Count}");
        return new SubmitResult(true, "已送出補件。", requestId, current.RequestNo);
    }

    public async Task<DataRequestResult<AfterSalesRequestView>> GetReviewQueueAsync(DataRequest request, AfterSalesStatus? status)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<AfterSalesRequest> source = context.AfterSalesRequest.AsNoTracking();
        if (status is not null)
        {
            source = source.Where(x => x.Status == status);
        }

        source = source.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

        var result = new DataRequestResult<AfterSalesRequestView> { Count = await source.CountAsync() };
        source = source.Skip((request.CurrentPage - 1) * request.PageSize);
        if (request.Take != 0)
        {
            source = source.Take(request.PageSize);
        }

        result.Result = await source.Select(ToView(context)).ToListAsync();
        return result;
    }

    public async Task<AfterSalesRequestView?> GetReviewDetailAsync(int requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AfterSalesRequest
            .AsNoTracking()
            .Where(x => x.Id == requestId)
            .Select(ToView(context))
            .FirstOrDefaultAsync();
    }

    public async Task<VerifyRecordResult> RequestMoreInfoAsync(int requestId, string note)
    {
        var text = note?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return VerifyRecordResultFactory.Build(false, "請填寫需要顧客補充的內容。");
        }

        var now = Now();
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var affected = await context.AfterSalesRequest
                .Where(x => x.Id == requestId && x.Status == AfterSalesStatus.Submitted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AfterSalesStatus.NeedMoreInfo)
                    .SetProperty(x => x.StaffNote, text)
                    .SetProperty(x => x.UpdatedAt, now));
            if (affected == 0)
            {
                logger.LogInformation("Request more info skipped because the status changed. RequestId={RequestId}", requestId);
                return VerifyRecordResultFactory.Build(false, StatusChangedMessage);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to request more info. RequestId={RequestId}", requestId);
            return VerifyRecordResultFactory.Build(false, "要求補件失敗。", ex);
        }

        logger.LogInformation("After-sales request needs more info. RequestId={RequestId}", requestId);
        await WriteStaffAuditAsync(AuditActions.Shop.RequestMoreInfo, requestId, "status=NeedMoreInfo");
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> DecideAsync(int requestId, bool approve, string? note)
    {
        var now = Now();
        var staffId = currentUserService.CurrentUser.Id;
        int? staffUserId = staffId > 0 ? staffId : null;
        var newStatus = approve ? AfterSalesStatus.Approved : AfterSalesStatus.Rejected;
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var affected = await context.AfterSalesRequest
                .Where(x => x.Id == requestId && x.Status == AfterSalesStatus.Submitted)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(x => x.Status, newStatus);
                    s.SetProperty(x => x.UpdatedAt, now);
                    s.SetProperty(x => x.DecidedAt, now);
                    s.SetProperty(x => x.DecidedByUserId, staffUserId);
                    // 一律覆寫：沒填備註時要清掉要求補件時留下的備註，不能讓顧客在結案後還看到「請補照片」。
                    s.SetProperty(x => x.StaffNote, text);
                });
            if (affected == 0)
            {
                logger.LogInformation("Decision skipped because the status changed. RequestId={RequestId}", requestId);
                return VerifyRecordResultFactory.Build(false, StatusChangedMessage);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decide after-sales request. RequestId={RequestId}", requestId);
            return VerifyRecordResultFactory.Build(false, "審核失敗。", ex);
        }

        logger.LogInformation("After-sales request decided. RequestId={RequestId}, RequestStatus={RequestStatus}", requestId, newStatus);
        await WriteStaffAuditAsync(AuditActions.Shop.RequestDecide, requestId, $"status={newStatus}");
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>附件存取：申請人本人或審核者才能取得；否則（或實體檔不存在、路徑不合法）回 null。</summary>
    public async Task<(string FullPath, string ContentType, string FileName)?> GetAttachmentForAsync(int attachmentId, int userId, bool canReview)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var attachment = await context.AfterSalesAttachment
            .AsNoTracking()
            .Where(x => x.Id == attachmentId)
            .Select(x => new { x.RelativePath, x.ContentType, x.OriginalFileName, x.AfterSalesRequest!.CustomerUserId })
            .FirstOrDefaultAsync();
        if (attachment is null)
        {
            return null;
        }

        if (!canReview && attachment.CustomerUserId != userId)
        {
            logger.LogWarning("After-sales attachment access denied. AttachmentId={AttachmentId}, UserId={UserId}", attachmentId, userId);
            return null;
        }

        var fullPath = fileStore.TryResolveFullPath(attachment.RelativePath);
        if (fullPath is null || !File.Exists(fullPath))
        {
            logger.LogWarning("After-sales attachment file is missing or outside the configured root. AttachmentId={AttachmentId}", attachmentId);
            return null;
        }

        return (fullPath, attachment.ContentType, attachment.OriginalFileName);
    }

    /// <summary>顧客動作的帳號：只在呼叫者就是該顧客時填（M2 Agent 也可能代為呼叫）。</summary>
    private string? ActorAccountFor(int customerUserId)
    {
        var user = currentUserService.CurrentUser;
        return user.Id == customerUserId ? user.Account : null;
    }

    private Task WriteStaffAuditAsync(string action, int requestId, string detail)
    {
        var user = currentUserService.CurrentUser;
        int? userId = user.Id > 0 ? user.Id : null;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: userId,
            actorAccount: userId is null ? null : user.Account,
            targetType: nameof(AfterSalesRequest),
            targetId: requestId.ToString(CultureInfo.InvariantCulture),
            detail: detail);
    }
}
