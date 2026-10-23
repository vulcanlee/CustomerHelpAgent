namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>排程作業的一次執行紀錄（唯讀，只做實體 → 畫面模型，0.9.96 起）。時間為 UTC，顯示前換成本地時間。</summary>
public class JobRunAdapterModel
{
    public int Id { get; set; }

    public string JobName { get; set; } = string.Empty;

    public string Trigger { get; set; } = string.Empty;

    public string? TriggeredByAccount { get; set; }

    public DateTime? ScheduledForUtc { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public long? DurationMs { get; set; }

    public string? TraceId { get; set; }
}
