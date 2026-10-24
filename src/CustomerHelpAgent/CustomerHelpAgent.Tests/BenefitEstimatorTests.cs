using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Tests;

/// <summary>效益對照的月度推估（<see cref="BenefitEstimator"/>，純函式）：公式、母體（只用已完成的執行）、未定價與「資料不足」。</summary>
public sealed class BenefitEstimatorTests
{
    private static readonly DemoAssumptionInput Assumption = new(
        QueueWaitMinutesTraditional: 240,
        QueueWaitMinutesAi: 30,
        ContactsPerCaseTraditional: 2,
        ContactsPerCaseAi: 0.5,
        MinutesPerContact: 5,
        StaffCostPerMinuteTwd: 8,
        MonthlyCaseVolume: 1000);

    /// <summary>
    /// 已完成的平均放在 Completed 欄位；一般平均刻意給不同的值（含未審的執行），推估若誤用一般平均就會算錯。
    /// </summary>
    private static FlowMetrics Flow(ScenarioFlow flow, int completed = 2, double staffMinutes = 6, double endToEnd = 20, double aiCost = 0,
        int modelCalls = 0, int unpriced = 0)
        => new()
        {
            Family = "換尺碼",
            Flow = flow,
            RunCount = completed + 1,
            StaffHandlingMinutes = staffMinutes + 100,
            EndToEndMinutes = endToEnd + 100,
            AiCostTwd = aiCost + 100,
            CompletedRunCount = completed,
            CompletedStaffHandlingMinutes = completed == 0 ? null : staffMinutes,
            CompletedEndToEndMinutes = completed == 0 ? null : endToEnd,
            CompletedAiCostTwd = completed == 0 ? null : aiCost,
            CompletedModelCallCount = modelCalls,
            CompletedUnpricedCallCount = unpriced,
            UnpricedCallCount = unpriced,
        };

    [Fact]
    public void Estimator_FormulaMatchesSpec()
    {
        var traditional = Flow(ScenarioFlow.Traditional, staffMinutes: 6, endToEnd: 20, aiCost: 0);
        var ai = Flow(ScenarioFlow.SingleAgent, completed: 3, staffMinutes: 2, endToEnd: 8, aiCost: 1.5, modelCalls: 12);

        var estimate = BenefitEstimator.Estimate(traditional, ai, Assumption);

        Assert.True(estimate.IsSufficient);
        Assert.Null(estimate.InsufficientReason);
        Assert.Null(estimate.Warning);
        Assert.Equal(2, estimate.TraditionalCompletedRunCount);
        Assert.Equal(3, estimate.AiCompletedRunCount);
        // 每案客服分鐘＝已完成的實測＋聯繫次數 × 每次分鐘：傳統 6＋2×5＝16；AI 2＋0.5×5＝4.5。
        Assert.Equal(16, estimate.TraditionalStaffMinutesPerCase, 9);
        Assert.Equal(4.5, estimate.AiStaffMinutesPerCase, 9);
        // （16 − 4.5）× 1000＝11500 分鐘＝191.666… 小時。
        Assert.Equal(11500, estimate.SavedStaffMinutesPerMonth, 9);
        Assert.Equal(11500 / 60.0, estimate.SavedStaffHoursPerMonth, 9);
        // 11500 × 8 − 1.5 × 1000＝92000 − 1500＝90500。
        Assert.Equal(1.5, estimate.AiCostPerCaseTwd, 9);
        Assert.Equal(1500, estimate.AiCostPerMonthTwd, 9);
        Assert.Equal(90500, estimate.SavedCostPerMonthTwd, 9);
        // 顧客等待＝已完成的端到端＋排隊假設：20＋240；8＋30。
        Assert.Equal(260, estimate.TraditionalCustomerWaitMinutes, 9);
        Assert.Equal(38, estimate.AiCustomerWaitMinutes, 9);
        Assert.Equal(1000, estimate.MonthlyCaseVolume);
    }

    [Fact]
    public void Estimator_AiSlower_GivesNegativeSavings()
    {
        var estimate = BenefitEstimator.Estimate(
            Flow(ScenarioFlow.Traditional, staffMinutes: 1, aiCost: 0),
            Flow(ScenarioFlow.MultiAgent, staffMinutes: 9, aiCost: 2, modelCalls: 4),
            Assumption with { ContactsPerCaseTraditional = 0, ContactsPerCaseAi = 0 });

        Assert.True(estimate.IsSufficient);
        Assert.Equal(-8000, estimate.SavedStaffMinutesPerMonth, 9);
        Assert.Equal(-8000 * 8 - 2000, estimate.SavedCostPerMonthTwd, 9);
    }

    [Fact]
    public void Estimator_SomeAiCallsUnpriced_WarnsButEstimates()
    {
        var estimate = BenefitEstimator.Estimate(
            Flow(ScenarioFlow.Traditional),
            Flow(ScenarioFlow.SingleAgent, aiCost: 1, modelCalls: 5, unpriced: 2),
            Assumption);

        Assert.True(estimate.IsSufficient);
        Assert.Contains("2 次模型呼叫未定價", estimate.Warning);
        Assert.Equal(1, estimate.AiCostPerCaseTwd, 9);
    }

    [Fact]
    public void Estimator_AiCompletedRunsWithoutModelCalls_Insufficient()
    {
        // AI 已完成的執行對不到任何模型呼叫（AI 對話紀錄未啟用、寫入失敗或已過期）：AI 成本 0 不代表免費，不能照算。
        var estimate = BenefitEstimator.Estimate(
            Flow(ScenarioFlow.Traditional),
            Flow(ScenarioFlow.SingleAgent, aiCost: 0, modelCalls: 0),
            Assumption);

        Assert.False(estimate.IsSufficient);
        Assert.Equal(
            "資料不足：AI 已完成的執行找不到模型呼叫紀錄（AI 對話紀錄未啟用、寫入失敗或已過期），無法估算 AI 成本",
            estimate.InsufficientReason);
        Assert.True(estimate.AiCallsMissing);
        Assert.False(estimate.MissingCompletedRuns);
        Assert.False(estimate.AiCallsAllUnpriced);
        Assert.Equal(0, estimate.SavedCostPerMonthTwd);
        Assert.Equal(0, estimate.AiCostPerCaseTwd);
    }

    public static TheoryData<string> InsufficientCases => ["traditional-null", "ai-null", "traditional-none-completed", "ai-none-completed", "ai-all-unpriced"];

    [Theory]
    [MemberData(nameof(InsufficientCases))]
    public void Estimator_InsufficientData_NoDivideByZero(string scenario)
    {
        FlowMetrics? traditional = Flow(ScenarioFlow.Traditional);
        FlowMetrics? ai = Flow(ScenarioFlow.SingleAgent, aiCost: 1, modelCalls: 3);
        switch (scenario)
        {
            case "traditional-null": traditional = null; break;
            case "ai-null": ai = null; break;
            // 有執行、但都還沒有客服決策：一般平均有值，已完成的沒有。
            case "traditional-none-completed": traditional = Flow(ScenarioFlow.Traditional, completed: 0); break;
            case "ai-none-completed": ai = Flow(ScenarioFlow.SingleAgent, completed: 0); break;
            // 全部未定價：AI 成本 0 不代表免費。
            case "ai-all-unpriced": ai = Flow(ScenarioFlow.SingleAgent, aiCost: 0, modelCalls: 3, unpriced: 3); break;
        }

        var estimate = BenefitEstimator.Estimate(traditional, ai, Assumption);

        Assert.False(estimate.IsSufficient);
        Assert.StartsWith("資料不足：", estimate.InsufficientReason);
        if (scenario == "ai-all-unpriced")
        {
            Assert.Contains("全部未定價", estimate.InsufficientReason);
            Assert.NotNull(estimate.Warning);
        }

        // 畫面依原因給不同的處理建議：未定價是補費率，其餘是補跑並審核。
        Assert.Equal(scenario == "ai-all-unpriced", estimate.AiCallsAllUnpriced);
        Assert.Equal(scenario != "ai-all-unpriced", estimate.MissingCompletedRuns);

        double[] numbers =
        [
            estimate.TraditionalStaffMinutesPerCase, estimate.AiStaffMinutesPerCase, estimate.SavedStaffMinutesPerMonth,
            estimate.SavedStaffHoursPerMonth, estimate.AiCostPerCaseTwd, estimate.AiCostPerMonthTwd, estimate.SavedCostPerMonthTwd,
            estimate.TraditionalCustomerWaitMinutes, estimate.AiCustomerWaitMinutes,
        ];
        Assert.All(numbers, x => Assert.Equal(0, x));
        Assert.Equal(1000, estimate.MonthlyCaseVolume);
    }
}
