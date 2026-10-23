namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>鈴鐺清單的一則通知（0.9.100 起）。時間為 UTC。</summary>
public class NotificationAdapterModel
{
    public int Id { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? Link { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ReadAtUtc { get; set; }
}
