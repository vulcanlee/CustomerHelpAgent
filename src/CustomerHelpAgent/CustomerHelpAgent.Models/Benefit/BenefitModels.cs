using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Models.Benefit;

/// <summary>
/// 效益對照（M6）的整體結果，由 <c>BenefitMetricsService.GetComparisonAsync(from, to)</c> 產生。
/// 時間一律是本地時間；範圍含 <see cref="From"/>、不含 <see cref="To"/>。全部數字只來自重置後仍保留的紀錄（實測值）。
/// </summary>
public sealed class BenefitComparison
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>依情境家族分區的並排結果（家族順序同展示控制台的劇本順序）。</summary>
    public IReadOnlyList<FamilyComparison> Families { get; init; } = [];

    /// <summary>「其他／未分類」：示範模式外（沒有劇本執行編號）的案件，依管道彙總。</summary>
    public IReadOnlyList<ChannelMetrics> Others { get; init; } = [];

    /// <summary>每次劇本執行的明細（依開始時間由舊到新，含不列入平均的執行，原因見 <see cref="RunMetrics.ExclusionReason"/>）。</summary>
    public IReadOnlyList<RunMetrics> Runs { get; init; } = [];

    /// <summary>AI 成本對帳：與「Token 用量」頁同一範圍、同一作業的總和對得起來。</summary>
    public BenefitCostBreakdown Cost { get; init; } = new();
}

/// <summary>一個情境家族（例：換尺碼）的並排結果；每種有執行紀錄的流程一筆。</summary>
public sealed class FamilyComparison
{
    public string Family { get; init; } = string.Empty;

    /// <summary>依流程（傳統 → 單一 Agent → 多 Agent）排序；沒有任何執行的流程不會出現。</summary>
    public IReadOnlyList<FlowMetrics> Flows { get; init; } = [];
}

/// <summary>
/// 一組執行（或案件）的平均值。只平均「納入統計」的筆數（<see cref="RunCount"/>）：回放一律排除（<see cref="ReplayExcludedCount"/>），
/// 無效的（沒有顧客操作；劇本執行另要求有建立申請）也排除（<see cref="ExcludedRunCount"/>）。
/// 可能沒有值的指標（端到端、客服處理）只平均有值的筆數，筆數另列（<see cref="EndToEndSampleCount"/>、<see cref="StaffHandlingSampleCount"/>）。
/// <see cref="RunCount"/> 為 0 時所有平均都是 null。
/// 「已完成」（Completed 開頭）是納入統計且端到端、客服處理都有值（已有客服決策）的同一群筆數，月度推估的每案數字只用這一群。
/// </summary>
public abstract class BenefitAverages
{
    /// <summary>納入平均的筆數（已排除回放與無效）。</summary>
    public int RunCount { get; set; }

    /// <summary>因回放而排除的筆數。</summary>
    public int ReplayExcludedCount { get; set; }

    /// <summary>因無效（沒有顧客操作、或劇本執行沒有建立申請）而排除的筆數；回放的不重複計入。</summary>
    public int ExcludedRunCount { get; set; }

    public double? CustomerSteps { get; set; }
    public double? StaffSteps { get; set; }

    public double? EndToEndMinutes { get; set; }

    /// <summary>有端到端時間（已有客服決策）的筆數；未完成的不列入平均。</summary>
    public int EndToEndSampleCount { get; set; }

    public double? StaffHandlingMinutes { get; set; }

    /// <summary>有客服處理時間的筆數。</summary>
    public int StaffHandlingSampleCount { get; set; }

    public double? AiCostTwd { get; set; }
    public double? ModelCallCount { get; set; }
    public double? StopToAskCount { get; set; }
    public double? AwaitingPhotoCount { get; set; }
    public double? RequestCount { get; set; }

    /// <summary>納入統計筆數的未定價呼叫合計（不是平均）；大於 0 時 AI 成本偏低。</summary>
    public int UnpricedCallCount { get; set; }

    /// <summary>已完成（納入統計、且有端到端與客服處理時間）的筆數。</summary>
    public int CompletedRunCount { get; set; }

    public double? CompletedEndToEndMinutes { get; set; }
    public double? CompletedStaffHandlingMinutes { get; set; }
    public double? CompletedAiCostTwd { get; set; }

    /// <summary>已完成筆數的模型呼叫合計（不是平均）。</summary>
    public int CompletedModelCallCount { get; set; }

    /// <summary>已完成筆數的未定價呼叫合計（不是平均）。</summary>
    public int CompletedUnpricedCallCount { get; set; }

    /// <summary>這一組全部筆數（含排除的）的 AI 成本合計（新台幣），供對帳用；不是平均。</summary>
    public double TotalAiCostTwd { get; set; }
}

/// <summary>某個家族、某種流程所有有效劇本執行的平均。</summary>
public sealed class FlowMetrics : BenefitAverages
{
    public string Family { get; init; } = string.Empty;
    public ScenarioFlow Flow { get; init; }
}

/// <summary>「其他／未分類」某個管道所有有效案件（CaseKey）的平均；<see cref="BenefitAverages.RunCount"/> 在這裡是案件數。</summary>
public sealed class ChannelMetrics : BenefitAverages
{
    public AfterSalesChannel Channel { get; init; }
}

/// <summary>一次劇本執行的實測值。不含顧客個資，只有示範帳號代號（<see cref="Account"/>）。</summary>
public sealed class RunMetrics
{
    public int RunId { get; init; }
    public string ScenarioCode { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public ScenarioFlow Flow { get; init; }

    /// <summary>示範帳號代號（例：customer1），不是顧客個資。</summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>回放：建立時是回放模式，或任一案件的執行軌跡是回放。整筆不列入平均。</summary>
    public bool IsReplay { get; init; }

    /// <summary>
    /// 不列入平均的原因（明細照列）：「回放（不計）」「沒有顧客操作，不計」「未建立申請，不計」；列入平均時為 null。
    /// </summary>
    public string? ExclusionReason { get; init; }

    /// <summary>已完成：列入平均，且端到端與客服處理時間都有值（已有客服決策）。月度推估只用這些執行。</summary>
    public bool IsCompleted { get; init; }

    /// <summary>執行的計算區間起點（＝準備劇本的時間）。</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>計算區間終點（不含）：下一次劇本執行的開始時間與查詢範圍終點兩者較早者。區間外的紀錄不算進這次執行。</summary>
    public DateTime EndBoundary { get; init; }

    /// <summary>區間內最後一筆活動（埋點、AI 呼叫或 Agent 執行）的時間；沒有任何活動為 null。</summary>
    public DateTime? LastActivityAt { get; init; }

    /// <summary>案件（CaseKey）數，含只瀏覽訂單的頁面。</summary>
    public int CaseCount { get; init; }

    /// <summary>
    /// 顧客操作步數：顧客點擊類埋點筆數（不含 AgentRequestCreated 與自動記錄的 OpenForm，裁定 R11），同一張表連續的填寫細節算 1 步。
    /// </summary>
    public int CustomerSteps { get; init; }
    public int StaffSteps { get; init; }

    /// <summary>
    /// 從第一筆顧客操作（不含自動記錄的「開啟表單」，裁定 R10）到其後第一筆客服決策的分鐘數；沒有決策（未完成）為 null。
    /// </summary>
    public double? EndToEndMinutes { get; init; }

    /// <summary>每筆申請「第一個決策之前的第一次客服開啟（OpenRequest）→ 第一個決策」分鐘數的合計；沒有成對紀錄為 null。</summary>
    public double? StaffHandlingMinutes { get; init; }

    public double AiCostTwd { get; init; }

    /// <summary>未定價（費用為 null）的呼叫數；這些呼叫不在 <see cref="AiCostTwd"/> 裡。</summary>
    public int UnpricedCallCount { get; init; }

    public int ModelCallCount { get; init; }
    public int StopToAskCount { get; init; }
    public int AwaitingPhotoCount { get; init; }

    /// <summary>申請筆數：顧客埋點 Submit、UrgeShipment、AgentRequestCreated 的筆數（不讀申請表，重置後仍在）。</summary>
    public int RequestCount { get; init; }
}

/// <summary>
/// AI 成本對帳（新台幣）：查詢範圍內「AI 售後客服 Agent」「AI 售後多 Agent」「AI 售後初審 Workflow」三種作業的已定價費用，
/// 一定等於 <see cref="DemoRunsTwd"/> ＋ <see cref="OtherCasesTwd"/> ＋ <see cref="UnattributedTwd"/>；
/// 「客服 AI 助手」無法對應到案件，另列 <see cref="StaffAssistantTwd"/>（未歸入每案）。
/// </summary>
public sealed class BenefitCostBreakdown
{
    /// <summary>全部劇本執行（含回放）的成本合計。</summary>
    public double DemoRunsTwd { get; init; }

    /// <summary>「其他／未分類」全部案件（含回放）的成本合計。</summary>
    public double OtherCasesTwd { get; init; }

    /// <summary>三種作業中對應不到任何執行或案件的成本（例如 AI 對話紀錄已過期、或屬於範圍前開始的執行）。</summary>
    public double UnattributedTwd { get; init; }

    public int UnattributedCallCount { get; init; }

    /// <summary>「客服 AI 助手」的成本（未歸入每案）。</summary>
    public double StaffAssistantTwd { get; init; }

    public int StaffAssistantCallCount { get; init; }
}

/// <summary>
/// 月度推估（假設／推估，不是實測），由 <c>BenefitEstimator.Estimate</c> 產生。實測輸入一律取兩邊「已完成」執行的平均。
/// <see cref="IsSufficient"/> 為 false 時（資料不足）所有計算結果都是 0（<see cref="MonthlyCaseVolume"/> 與已完成筆數仍照實回傳），
/// <see cref="InsufficientReason"/> 說明缺什麼。
/// </summary>
public sealed class MonthlyEstimate
{
    public bool IsSufficient { get; init; }
    public string? InsufficientReason { get; init; }

    /// <summary>資料不足的原因之一：任一邊沒有已完成的執行（處理方式：兩種做法各跑一次並由客服做出決定）。</summary>
    public bool MissingCompletedRuns { get; init; }

    /// <summary>資料不足的原因之一：AI 已完成執行的模型呼叫全部未定價（處理方式：補上模型費率）。</summary>
    public bool AiCallsAllUnpriced { get; init; }

    /// <summary>
    /// 資料不足的原因之一：AI 已完成的執行對不到任何模型呼叫（AI 對話紀錄未啟用、寫入失敗或已過期，用量列對不到案件）；
    /// AI 成本 0 不代表免費（處理方式：確認 AI 對話紀錄已啟用，在保存期限內重新跑一次 AI 做法並審核）。
    /// </summary>
    public bool AiCallsMissing { get; init; }

    /// <summary>提醒（例如 AI 有未定價的呼叫，AI 成本偏低）；沒有則為 null。資料不足時也可能有值。</summary>
    public string? Warning { get; init; }

    /// <summary>傳統、AI 兩邊用來推估的已完成執行數。</summary>
    public int TraditionalCompletedRunCount { get; init; }

    public int AiCompletedRunCount { get; init; }

    /// <summary>傳統每案客服分鐘＝已完成執行的客服處理分鐘平均＋傳統來回聯繫次數 × 每次分鐘。</summary>
    public double TraditionalStaffMinutesPerCase { get; init; }

    /// <summary>AI 每案客服分鐘＝已完成執行的客服處理分鐘平均＋AI 來回聯繫次數 × 每次分鐘。</summary>
    public double AiStaffMinutesPerCase { get; init; }

    /// <summary>每月節省客服分鐘＝（傳統每案 − AI 每案）× 月案件量；可能為負（AI 較慢）。</summary>
    public double SavedStaffMinutesPerMonth { get; init; }

    /// <summary>每月節省客服時數＝每月節省客服分鐘 ÷ 60。</summary>
    public double SavedStaffHoursPerMonth { get; init; }

    /// <summary>AI 每案成本（已完成執行的實測平均，新台幣）。</summary>
    public double AiCostPerCaseTwd { get; init; }

    /// <summary>每月 AI 成本＝AI 每案成本 × 月案件量。</summary>
    public double AiCostPerMonthTwd { get; init; }

    /// <summary>每月節省成本＝每月節省客服分鐘 × 每分鐘成本 − 每月 AI 成本；可能為負。</summary>
    public double SavedCostPerMonthTwd { get; init; }

    /// <summary>傳統顧客等待＝已完成執行的端到端分鐘平均＋傳統排隊等待假設。</summary>
    public double TraditionalCustomerWaitMinutes { get; init; }

    /// <summary>AI 顧客等待＝已完成執行的端到端分鐘平均＋AI 排隊等待假設。</summary>
    public double AiCustomerWaitMinutes { get; init; }

    public int MonthlyCaseVolume { get; init; }
}
