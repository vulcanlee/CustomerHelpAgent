namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>
/// Token 用量的畫面繫結模型。本頁唯讀（只有刪除，沒有編輯），因此不需要 ICloneable。
/// </summary>
public class TokenUsageLogAdapterModel
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string CallKind { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public int? InputCount { get; set; }

    public int? OutputCount { get; set; }

    public int? TotalCount { get; set; }

    public int? CachedInputCount { get; set; }

    public int? ReasoningCount { get; set; }

    /// <summary>圖片輸入 token，是 InputCount 的子集。</summary>
    public int? ImageInputCount { get; set; }

    /// <summary>圖片輸入中的快取命中，是 ImageInputCount 與 CachedInputCount 的交集子集。</summary>
    public int? ImageCachedInputCount { get; set; }

    /// <summary>圖片輸出 token，是 OutputCount 的子集。</summary>
    public int? ImageOutputCount { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary>語音合成的計費字元數。</summary>
    public int? CharacterCount { get; set; }

    /// <summary>估算費用（美金）。null 代表未定價。</summary>
    public double? CostUsd { get; set; }

    /// <summary>估算費用（台幣）。</summary>
    public double? CostTwd { get; set; }

    /// <summary>計算當下的匯率快照。</summary>
    public double? CostExchangeRate { get; set; }

    /// <summary>實際套用的費率設定鍵。</summary>
    public string? CostPriceKey { get; set; }

    /// <summary>是否套用長脈絡費率。</summary>
    public bool CostLongContext { get; set; }

    /// <summary>生效費率組的精簡 JSON。</summary>
    public string? CostRateSnapshot { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public string? RawUsageFile { get; set; }

    /// <summary>呼叫識別碼，對應 AI 對話紀錄；0.9.72 之前的紀錄為 null。</summary>
    public Guid? CallId { get; set; }

    /// <summary>依時長計費的呼叫（不回傳 token 數），合計欄要改顯示秒數而非 token。</summary>
    public bool IsDurationBilled => DurationSeconds.HasValue && TotalCount.HasValue == false;

    /// <summary>算不出費用（找不到費率設定或匯率未設定）。費用欄要顯示「未定價」而不是 0。</summary>
    public bool IsUnpriced => CostUsd.HasValue == false;

    /// <summary>
    /// 費率是前綴比對來的（例如 gpt-4o-2024-08-06 套用了 gpt-4o 的費率）。
    /// 必須在畫面上標示 —— 這是對「套錯兄弟模型費率」唯一的實務防線。
    /// </summary>
    public bool IsPrefixMatched => string.IsNullOrEmpty(CostPriceKey) == false
        && string.Equals(CostPriceKey, Model, StringComparison.OrdinalIgnoreCase) == false;
}
