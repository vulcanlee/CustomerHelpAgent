using System.Globalization;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Web.Benefit;

/// <summary>差異的好壞方向：越少越好的指標才標「改善／變差」，其餘只寫百分比。</summary>
public enum BenefitDirection
{
    LowerIsBetter,
    Neutral,
}

/// <summary>
/// 效益對照頁的一個實測指標：標籤、白話定義（滑鼠提示與操作說明用，與 <c>BenefitMetricsService</c> 的計算一致）、
/// 平均值與單次值的取法、顯示格式。只平均有值筆數的指標（端到端、客服處理）另給 <see cref="SampleCount"/>；
/// <see cref="IsMinutes"/> 標示分鐘數（單次值有小數，其餘次數類的單次值都是整數）。
/// </summary>
public sealed record BenefitMetric(
    string Label,
    string Definition,
    BenefitDirection Direction,
    Func<BenefitAverages, double?> Average,
    Func<RunMetrics, double?> RunValue,
    bool IsCurrency,
    bool Charted,
    Func<BenefitAverages, int>? SampleCount = null,
    bool IsMinutes = false)
{
    public string Format(double? value) => BenefitMetricDisplay.Format(value, IsCurrency);
}

/// <summary>差異百分比與它的語氣（better／worse／neutral，對應 CSS）；<see cref="Text"/> 一定含文字，不只靠顏色。</summary>
public readonly record struct BenefitDelta(double Percent, string Text, string Tone);

/// <summary>效益對照頁的顯示規則（M6）：指標清單、流程名稱、數字格式與相對傳統的差異。純函式。</summary>
public static class BenefitMetricDisplay
{
    public static IReadOnlyList<BenefitMetric> Metrics { get; } =
    [
        new("顧客操作步數",
            "顧客在畫面上的操作次數（選商品、選類型、填寫、上傳照片、送出、傳訊息、按確認等）。同一張表連續修改欄位算 1 步；打開表單或線上客服頁不算（兩邊都不算，才公平），Agent 建立申請也不算顧客的步數。",
            BenefitDirection.LowerIsBetter, x => x.CustomerSteps, x => x.CustomerSteps, false, true),
        new("客服操作步數",
            "客服在後台的操作次數（開啟申請、查看訂單或規則、核准、退回、要求補件）。",
            BenefitDirection.LowerIsBetter, x => x.StaffSteps, x => x.StaffSteps, false, true),
        new("端到端（分鐘）",
            "從顧客第一個操作，到客服第一次做出決定（核准、退回或要求補件）的分鐘數。傳統表單不從打開表單算起（那是頁面自動記錄的），從第一次選商品、選類型或填寫算起。還沒有客服決定的執行不列入平均。",
            BenefitDirection.LowerIsBetter, x => x.EndToEndMinutes, x => x.EndToEndMinutes, false, true, x => x.EndToEndSampleCount, IsMinutes: true),
        new("客服處理（分鐘）",
            "每筆申請從客服第一次開啟到做出決定的分鐘數；一次執行有多筆申請時相加，代表客服的總工作量。",
            BenefitDirection.LowerIsBetter, x => x.StaffHandlingMinutes, x => x.StaffHandlingMinutes, false, true, x => x.StaffHandlingSampleCount, IsMinutes: true),
        new("AI 成本（每次）",
            "這次執行所有 AI 呼叫（顧客 Agent、多 Agent、AI 初審）的費用合計，與「Token 用量」頁同一算法；未定價的呼叫不含在內。傳統表單不呼叫 AI。",
            BenefitDirection.Neutral, x => x.AiCostTwd, x => x.AiCostTwd, true, true),
        new("模型呼叫次數",
            "呼叫語言模型的次數，含失敗與未定價的呼叫。",
            BenefitDirection.Neutral, x => x.ModelCallCount, x => x.ModelCallCount, false, false),
        new("Agent 停下詢問",
            "Agent 停下來問顧客（例如「黑 40 還是白 41？」）的次數；先問清楚再辦，不是錯誤。",
            BenefitDirection.Neutral, x => x.StopToAskCount, x => x.StopToAskCount, false, false),
        new("等待上傳照片",
            "Agent 停下來等顧客上傳照片的次數。",
            BenefitDirection.Neutral, x => x.AwaitingPhotoCount, x => x.AwaitingPhotoCount, false, false),
        new("申請筆數",
            "建立的售後申請筆數（表單送出、物流催件、Agent 確認後建立的申請）。",
            BenefitDirection.Neutral, x => x.RequestCount, x => x.RequestCount, false, false),
    ];

    public static string FlowLabel(ScenarioFlow flow) => flow switch
    {
        ScenarioFlow.Traditional => "傳統表單",
        ScenarioFlow.SingleAgent => "單一 Agent",
        ScenarioFlow.MultiAgent => "多 Agent",
        _ => flow.ToString(),
    };

    /// <summary>CSS 用的流程代號（長條顏色、卡片標記）。</summary>
    public static string FlowKey(ScenarioFlow flow) => flow switch
    {
        ScenarioFlow.Traditional => "traditional",
        ScenarioFlow.SingleAgent => "single",
        ScenarioFlow.MultiAgent => "multi",
        _ => "other",
    };

    /// <summary>實測值：次數與分鐘最多 1 位小數（整數不帶小數），金額 NT$ 2 位小數；沒有值寫「—」。</summary>
    public static string Format(double? value, bool isCurrency)
    {
        if (value is not { } number || double.IsFinite(number) == false)
        {
            return "—";
        }

        return isCurrency
            ? "NT$" + number.ToString("#,0.00", CultureInfo.InvariantCulture)
            : number.ToString("#,0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 月度推估的金額：NT$ 整數加千分位；負數寫在 NT$ 前面（−NT$1,234）。
    /// 先四捨五入再決定正負號，−0.4 不會寫成「−NT$0」。
    /// </summary>
    public static string FormatMonthlyTwd(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (rounded < 0 ? "−" : string.Empty) + "NT$" + Math.Abs(rounded).ToString("#,0", CultureInfo.InvariantCulture);
    }

    /// <summary>月度推估的時數或分鐘：千分位、1 位小數；負數用減號（−）。同樣先四捨五入再決定正負號。</summary>
    public static string FormatMonthly(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return (rounded < 0 ? "−" : string.Empty) + Math.Abs(rounded).ToString("#,0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 範圍內有對不到案件的 AI 呼叫時的提醒（畫面與匯出的月度推估共用）：它們不在任何一次執行的 AI 成本裡，
    /// 推估用的 AI 每案成本可能偏低。沒有時回 null。
    /// </summary>
    public static string? UnattributedWarning(BenefitCostBreakdown cost)
        => cost.UnattributedCallCount > 0 ? $"有 {cost.UnattributedCallCount} 次 AI 呼叫對應不到案件，AI 成本可能偏低。" : null;

    /// <summary>
    /// 相對傳統的差異：（值 − 傳統）÷ 傳統，四捨五入到整數百分比。傳統沒有值或是 0（例如傳統不呼叫 AI）時無法計算，回 null。
    /// 越少越好的指標標「改善」或「變差」，其餘只寫百分比；相同時寫「持平」。
    /// </summary>
    public static BenefitDelta? Delta(double? baseline, double? value, BenefitDirection direction)
    {
        if (baseline is not { } basis || basis <= 0 || value is not { } current || double.IsFinite(current) == false)
        {
            return null;
        }

        var percent = Math.Round((current - basis) / basis * 100, MidpointRounding.AwayFromZero);
        if (percent == 0)
        {
            return new BenefitDelta(0, "持平", "neutral");
        }

        var number = (percent < 0 ? "−" : "+") + Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture) + "%";
        if (direction == BenefitDirection.Neutral)
        {
            return new BenefitDelta(percent, number, "neutral");
        }

        return percent < 0
            ? new BenefitDelta(percent, number + "，改善", "better")
            : new BenefitDelta(percent, number + "，變差", "worse");
    }
}
