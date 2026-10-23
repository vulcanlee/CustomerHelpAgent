using System.ComponentModel.DataAnnotations;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>流程度量（比較傳統與 Agent 管道的操作步驟）。</summary>
public class ProcessMetric
{
    public int Id { get; set; }
    public Guid CaseKey { get; set; }
    public AfterSalesChannel Channel { get; set; }
    public ProcessActor Actor { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    public int? UserId { get; set; }
    public int? AfterSalesRequestId { get; set; }
    public DateTime OccurredAt { get; set; }
}
