using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// LLM token 用量的記錄端點。
///
/// <b>這是新增 LLM 呼叫點時要用的擴充點</b>：注入本介面，在拿到 API 回應之後呼叫一次
/// <see cref="RecordAsync"/>，該次呼叫就會自動出現在「Token 用量」頁與所有統計頁籤上。
///
/// ⚠️ 記錄點請放在**發出 HTTP 的那一層**，不要放畫面層 —— 原始 usage、實際模型名稱與耗時
/// 在回到畫面之前就已經丟失了。
///
/// 刻意與 <see cref="TokenUsageLogService"/> 的查詢／刪除方法分開：呼叫端只需要「記一筆」，
/// 不該看到「清空全部」這種管理用的方法，測試也只要假造這一個方法。
/// </summary>
public interface ITokenUsageRecorder
{
    /// <summary>
    /// 記錄一次 LLM 呼叫。成功與失敗都要記 ——
    /// 「回應成功但內容為空」那種情況付了錢卻沒拿到東西，最值得被看見。
    ///
    /// 實作保證<b>絕不拋出</b>：記錄用量不可以讓呼叫端的主流程失敗。
    /// </summary>
    Task RecordAsync(TokenUsageEntry entry);
}
