namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>排程作業的狀態（唯讀，只做實體 → 畫面模型，0.9.96 起）。時間為 UTC。</summary>
public class ScheduledJobStateAdapterModel
{
    public string JobName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime? LastScheduledForUtc { get; set; }
}
