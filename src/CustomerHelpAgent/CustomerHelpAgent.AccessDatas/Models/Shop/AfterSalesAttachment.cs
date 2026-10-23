namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>售後申請附件。</summary>
public class AfterSalesAttachment
{
    public int Id { get; set; }
    public int AfterSalesRequestId { get; set; }
    public AfterSalesRequest? AfterSalesRequest { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}
