using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Benefit;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 效益對照（M6）的月度推估：純函式，以兩種流程「已完成」執行（有客服決策、有端到端與客服處理時間）的實測平均＋假設值計算，
/// 結果一律是「假設／推估」。每案的客服分鐘、端到端與 AI 成本都取同一群已完成的執行，不混用不同母體。
/// <list type="bullet">
/// <item>每案客服分鐘＝已完成的客服處理分鐘平均＋來回聯繫次數 × 每次分鐘（傳統、AI 各用自己的聯繫次數）。</item>
/// <item>每月節省客服分鐘＝（傳統每案 − AI 每案）× 月案件量；時數再 ÷ 60。</item>
/// <item>每月節省成本＝每月節省客服分鐘 × 每分鐘成本 − AI 每案成本 × 月案件量（傳統表單零 AI，不扣）。</item>
/// <item>顧客等待＝已完成的端到端分鐘平均＋排隊等待假設（傳統、AI 各用自己的排隊時間）。</item>
/// </list>
/// 任一邊沒有已完成的執行、AI 已完成的執行對不到任何模型呼叫（AI 對話紀錄未啟用、寫入失敗或已過期）、
/// 或 AI 的模型呼叫全部未定價時回「資料不足」（計算結果全為 0；公式沒有除以變數，不會除以零）。
/// AI 有部分未定價呼叫時照算，但附提醒（AI 成本偏低）。
/// </summary>
public static class BenefitEstimator
{
    public static MonthlyEstimate Estimate(FlowMetrics? traditional, FlowMetrics? ai, DemoAssumptionInput assumption)
    {
        var missing = new List<string>();
        var missingCompleted = false;
        var allUnpriced = false;
        var callsMissing = false;
        if (traditional is not { CompletedRunCount: > 0 })
        {
            missing.Add("傳統沒有已完成（有客服決策）的執行");
            missingCompleted = true;
        }

        if (ai is not { CompletedRunCount: > 0 })
        {
            missing.Add("AI 沒有已完成（有客服決策）的執行");
            missingCompleted = true;
        }
        else if (ai.CompletedModelCallCount == 0)
        {
            // AI 做法一定會呼叫模型；一次都對不到，代表用量列沒能經 AI 對話紀錄對到案件，AI 成本 0 不是真的免費。
            missing.Add("AI 已完成的執行找不到模型呼叫紀錄（AI 對話紀錄未啟用、寫入失敗或已過期），無法估算 AI 成本");
            callsMissing = true;
        }
        else if (ai.CompletedUnpricedCallCount == ai.CompletedModelCallCount)
        {
            missing.Add("AI 的模型呼叫全部未定價，無法估算 AI 成本");
            allUnpriced = true;
        }

        var warning = ai is { CompletedUnpricedCallCount: > 0 }
            ? $"AI 有 {ai.CompletedUnpricedCallCount} 次模型呼叫未定價（沒有費率），AI 成本偏低。"
            : null;

        if (missing.Count > 0)
        {
            return new MonthlyEstimate
            {
                IsSufficient = false,
                InsufficientReason = "資料不足：" + string.Join("；", missing),
                MissingCompletedRuns = missingCompleted,
                AiCallsAllUnpriced = allUnpriced,
                AiCallsMissing = callsMissing,
                Warning = warning,
                TraditionalCompletedRunCount = traditional?.CompletedRunCount ?? 0,
                AiCompletedRunCount = ai?.CompletedRunCount ?? 0,
                MonthlyCaseVolume = assumption.MonthlyCaseVolume,
            };
        }

        // 已完成筆數大於 0 時，三個已完成平均一定有值。
        var traditionalPerCase = traditional!.CompletedStaffHandlingMinutes!.Value + assumption.ContactsPerCaseTraditional * assumption.MinutesPerContact;
        var aiPerCase = ai!.CompletedStaffHandlingMinutes!.Value + assumption.ContactsPerCaseAi * assumption.MinutesPerContact;
        var savedMinutes = (traditionalPerCase - aiPerCase) * assumption.MonthlyCaseVolume;
        var aiCostPerCase = ai.CompletedAiCostTwd!.Value;
        var aiCostPerMonth = aiCostPerCase * assumption.MonthlyCaseVolume;

        return new MonthlyEstimate
        {
            IsSufficient = true,
            Warning = warning,
            TraditionalCompletedRunCount = traditional.CompletedRunCount,
            AiCompletedRunCount = ai.CompletedRunCount,
            TraditionalStaffMinutesPerCase = traditionalPerCase,
            AiStaffMinutesPerCase = aiPerCase,
            SavedStaffMinutesPerMonth = savedMinutes,
            SavedStaffHoursPerMonth = savedMinutes / 60,
            AiCostPerCaseTwd = aiCostPerCase,
            AiCostPerMonthTwd = aiCostPerMonth,
            SavedCostPerMonthTwd = savedMinutes * assumption.StaffCostPerMinuteTwd - aiCostPerMonth,
            TraditionalCustomerWaitMinutes = traditional.CompletedEndToEndMinutes!.Value + assumption.QueueWaitMinutesTraditional,
            AiCustomerWaitMinutes = ai.CompletedEndToEndMinutes!.Value + assumption.QueueWaitMinutesAi,
            MonthlyCaseVolume = assumption.MonthlyCaseVolume,
        };
    }
}
