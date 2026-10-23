namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// LLM 呼叫的計價設定（appsettings.json 的 AiPricingSettings 區段）。
///
/// 放在 CustomerHelpAgent.Models 而不是 CustomerHelpAgent.Web/Configuration（AiSettings 所在處），
/// 是因為費用計算發生在 Business 層的 TokenUsageLogService，而 Business 不相依 Web。
/// 理由與 <see cref="SystemSettings"/> 相同。
///
/// ⚠️ 費率數字是<b>人工從供應商定價頁抄下來的</b>，不會自動更新。
/// 擷取來源與日期請見 docs/operations/日誌與設定檔說明.md，並定期核對實際帳單。
/// </summary>
public sealed class AiPricingSettings
{
    public const string SectionName = "AiPricingSettings";

    /// <summary>
    /// 1 USD 折合多少 TWD。<b>小於等於 0 視為未設定</b>，此時所有呼叫一律記為「未定價」。
    /// 沒有另外的啟用旗標，比照 AiSettings.ApiKey 留空即停用的既有作法。
    /// </summary>
    public double UsdToTwd { get; set; }

    /// <summary>
    /// 模型單價表。鍵為模型名稱，比對不分大小寫。
    ///
    /// Azure OpenAI 請注意：紀錄裡的模型名稱優先取自回應的 model 欄位，但呼叫失敗
    /// （HTTP 錯誤／逾時／傳輸失敗）時根本沒有回應，會退回 AiSettings.Model —— 那是
    /// <b>部署名稱</b>。要讓這些列也能計價，直接用部署名稱再加一筆即可。
    /// </summary>
    public Dictionary<string, AiModelPricing> Models { get; set; } = [];
}

/// <summary>單一模型的計價設定。</summary>
public sealed class AiModelPricing
{
    /// <summary>
    /// 超過此輸入 token 數（<b>嚴格大於</b>）改用 <see cref="LongContextRates"/>。
    /// 以本次 InputCount 判斷，也就是扣掉快取之前的數字。
    ///
    /// OpenAI 官方定價對支援長脈絡分級的模型，以超過 272000 輸入 token 為切換門檻。
    /// </summary>
    public int LongContextThresholdTokens { get; set; }

    /// <summary>標準費率。</summary>
    public AiModelRates Rates { get; set; } = new();

    /// <summary>長脈絡費率。null 代表此模型沒有分級，永遠套用 <see cref="Rates"/>。</summary>
    public AiModelRates? LongContextRates { get; set; }
}

/// <summary>
/// 一組費率。除 <see cref="AudioPerMinute"/> 以外都是「每 1,000,000 單位」的價格（美金）。
///
/// null 的語意<b>刻意分成兩種</b>：
/// <list type="bullet">
///   <item>兩個 cached 費率為 null 時，快取部分<b>退回以對應的 input 費率計算</b>（不打折），
///         而不是免費 —— 寧可高估也不要靜默少算。</item>
///   <item>其餘費率為 null 時，該計費單位不計費。</item>
/// </list>
/// 整組費率全為 null 或 0 時視為未設定，該次呼叫記為「未定價」。
/// </summary>
public sealed class AiModelRates
{
    public double? TextInputPerMillion { get; set; }

    /// <summary>null 時退回 <see cref="TextInputPerMillion"/>。</summary>
    public double? TextCachedInputPerMillion { get; set; }

    public double? TextOutputPerMillion { get; set; }

    public double? ImageInputPerMillion { get; set; }

    /// <summary>null 時退回 <see cref="ImageInputPerMillion"/>。</summary>
    public double? ImageCachedInputPerMillion { get; set; }

    public double? ImageOutputPerMillion { get; set; }

    /// <summary>音訊計費：每分鐘美金。這一項不是「每 1M」。</summary>
    public double? AudioPerMinute { get; set; }

    /// <summary>語音合成計費：每 1,000,000 字元美金。</summary>
    public double? SpeechPerMillionCharacters { get; set; }
}

/// <summary>
/// 一次呼叫的費用計算結果。由 IAiUsageCostCalculator 產生，寫進 TokenUsageLog 成為快照。
///
/// 計算器算不出來時回傳 null（而不是本型別的零值），代表「未定價」——
/// 那與「有費率、但這次用量是 0」是完全不同的兩件事。
/// </summary>
public sealed record AiUsageCost
{
    public double CostUsd { get; init; }

    public double CostTwd { get; init; }

    /// <summary>計算當下的匯率。</summary>
    public double ExchangeRate { get; init; }

    /// <summary>實際命中的設定鍵。與模型名稱不同時代表來自前綴比對。</summary>
    public string PriceKey { get; init; } = string.Empty;

    /// <summary>是否套用長脈絡費率。</summary>
    public bool LongContext { get; init; }

    /// <summary>生效費率組的精簡 JSON（只含非零項），供事後重算對帳。</summary>
    public string RateSnapshot { get; init; } = string.Empty;
}
