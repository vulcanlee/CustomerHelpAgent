using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// AI 對話紀錄的設定（<c>AiCallLogSettings</c> 區段，0.9.72 起）。
///
/// 放在 Models 而不是 Web/Configuration：讀它的是 Business 層的 <c>AiCallLogService</c>，
/// Business 參照不到 Web。以 <c>ValidateOnStart</c> 驗證範圍，寫壞就啟動失敗。
/// </summary>
public class AiCallLogSettings
{
    public const string SectionName = "AiCallLogSettings";

    /// <summary>
    /// 是否記錄每次 AI 呼叫的完整請求與回應。關閉後不再記錄；
    /// 已存在的紀錄仍依 <see cref="RetentionDays"/> 自動清除。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>保留天數。超過的紀錄連同內容檔由排程作業自動清除（0.9.96 起預設每天 03:00；之前是啟動時與每日）。</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 90;
}
