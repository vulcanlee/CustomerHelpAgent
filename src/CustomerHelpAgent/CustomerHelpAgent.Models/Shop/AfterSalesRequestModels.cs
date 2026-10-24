using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Models.Shop;

/// <summary>送出售後申請的輸入（傳統表單與 M2 Agent 共用）。</summary>
public sealed class SubmitAfterSalesInput
{
    /// <summary>同一個案件在傳統流程與埋點之間共用的識別碼。</summary>
    public Guid CaseKey { get; set; }

    public int OrderItemId { get; set; }
    public AfterSalesType Type { get; set; }
    public string? TargetSize { get; set; }
    public string? TargetColor { get; set; }
    public bool? IsWorn { get; set; }
    public string Description { get; set; } = string.Empty;
    public AfterSalesChannel Channel { get; set; } = AfterSalesChannel.Traditional;
}

/// <summary>上傳的照片；<see cref="Content"/> 由呼叫端負責釋放。</summary>
public sealed class AfterSalesUploadFile
{
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public Stream Content { get; set; } = Stream.Null;
}

/// <summary>售後申請附件（只給檔名與大小，不給實體路徑）。</summary>
public sealed record AfterSalesAttachmentView(int Id, string OriginalFileName, long FileSize);

/// <summary>售後申請（列表與明細共用）。不含收件人地址與手機。</summary>
public sealed record AfterSalesRequestView
{
    public int Id { get; init; }
    public string RequestNo { get; init; } = string.Empty;
    public Guid CaseKey { get; init; }

    /// <summary>申請人、訂單商品與訂單的 Id：給程式查庫存與出貨時間用（AI 初審的規則核對），不送給模型。</summary>
    public int CustomerUserId { get; init; }
    public int OrderItemId { get; init; }
    public int SalesOrderId { get; init; }

    public AfterSalesType Type { get; init; }
    public AfterSalesStatus Status { get; init; }
    public AfterSalesChannel Channel { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ProductCategory { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string? TargetSize { get; init; }
    public string? TargetColor { get; init; }
    public bool? IsWorn { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? StaffNote { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public int? AppliedPolicyId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public IReadOnlyList<AfterSalesAttachmentView> Attachments { get; init; } = [];
}

/// <summary>送件／補件結果。</summary>
public sealed record SubmitResult(bool Success, string Message, int? RequestId, string? RequestNo);
