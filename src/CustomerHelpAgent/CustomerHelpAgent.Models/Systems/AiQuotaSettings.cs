using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// AI 用量上限（0.9.109 起，新台幣整數元；0＝不限制）。可在「系統參數」頁的「AI 用量」覆寫，調整後立即生效。
/// 以「Token 用量」記錄的台幣費用加總；未定價的模型（費用為空）以 0 計。期間以伺服器本地時間的今天、本月計算。
/// </summary>
public class AiQuotaSettings
{
    public const string SectionName = "AiQuotaSettings";

    /// <summary>全系統每日上限。</summary>
    [Range(0, 100_000_000)]
    public int GlobalDailyTwd { get; set; }

    /// <summary>全系統每月上限。</summary>
    [Range(0, 100_000_000)]
    public int GlobalMonthlyTwd { get; set; }

    /// <summary>每位使用者每日上限。</summary>
    [Range(0, 100_000_000)]
    public int PerUserDailyTwd { get; set; }

    /// <summary>每位使用者每月上限。</summary>
    [Range(0, 100_000_000)]
    public int PerUserMonthlyTwd { get; set; }
}
