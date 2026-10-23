using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 依 appsettings 的單價設定，估算一次 LLM 呼叫的費用。
///
/// 由 TokenUsageLogService.RecordAsync 在寫入前呼叫一次，算出來的金額連同當時生效的
/// 單價與匯率一起存進資料列 —— 是<b>快照</b>，日後調整設定不會改變已經記錄的帳。
/// </summary>
public interface IAiUsageCostCalculator
{
    /// <summary>
    /// 估算費用。算不出來時回傳 <c>null</c>（代表「未定價」）：找不到該模型的費率設定、
    /// 匯率未設定、或選中的費率組整組是空的。
    ///
    /// ⚠️ 回傳 <c>null</c> 與回傳「金額 0」是兩件不同的事：後者代表有費率、只是這次用量是 0。
    /// </summary>
    AiUsageCost? Calculate(TokenUsageEntry entry);
}
