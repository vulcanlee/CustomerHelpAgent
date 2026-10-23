namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>一個系統參數覆寫值（唯讀，0.9.98 起）。時間為 UTC。</summary>
public class SystemParameterAdapterModel
{
    public string ParameterKey { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>開啟修改窗時記下，存檔與還原時比對；別人先改過就回衝突。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
