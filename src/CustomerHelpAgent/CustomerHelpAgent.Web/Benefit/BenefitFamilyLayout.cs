using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Web.Benefit;

/// <summary>一個家族的一種流程：<see cref="Metrics"/> 為 null 代表範圍內沒有這種流程的執行；<see cref="ScenarioCodes"/> 是可以準備的劇本。</summary>
public sealed record FlowSlot(ScenarioFlow Flow, FlowMetrics? Metrics, string ScenarioCodes);

public sealed record FamilySection(string Family, IReadOnlyList<FlowSlot> Slots)
{
    public FlowMetrics? Traditional => Slots.FirstOrDefault(x => x.Flow == ScenarioFlow.Traditional)?.Metrics;
}

/// <summary>
/// 效益對照頁與匯出共用的版面規則（M6）：要列哪些家族與流程、缺資料提示、筆數說明、平均值與執行狀態文字。
/// 頁面與 Excel／PDF 都走這裡，兩邊的結構與用字不會各漂各的。純函式。
/// </summary>
public static class BenefitFamilyLayout
{
    /// <summary>
    /// 要列出的家族：劇本清單中有兩種以上做法的家族一律列出（沒資料也列，提示要準備哪個劇本），
    /// 另外再加上範圍內有執行紀錄的其他家族。家族順序同展示控制台的劇本順序；流程依傳統 → 單一 → 多 Agent。
    /// </summary>
    public static IReadOnlyList<FamilySection> BuildSections(BenefitComparison data)
    {
        var scenarioFamilies = DemoScenarios.All.GroupBy(x => x.Family).ToList();
        var families = scenarioFamilies
            .Where(x => x.Select(s => s.Flow).Distinct().Count() > 1)
            .Select(x => x.Key)
            .Concat(data.Families.Select(x => x.Family))
            .Distinct()
            .ToList();

        var result = new List<FamilySection>();
        foreach (var family in families)
        {
            var measured = data.Families.FirstOrDefault(x => x.Family == family)?.Flows ?? [];
            var scenarios = DemoScenarios.All.Where(x => x.Family == family).ToList();
            var flows = scenarios.Select(x => x.Flow).Concat(measured.Select(x => x.Flow)).Distinct().Order().ToList();
            result.Add(new FamilySection(
                family,
                [.. flows.Select(flow => new FlowSlot(
                    flow,
                    measured.FirstOrDefault(x => x.Flow == flow),
                    string.Join("、", scenarios.Where(x => x.Flow == flow).Select(x => x.Code))))]));
        }

        return result;
    }

    public static string MissingHint(FlowSlot slot)
        => string.IsNullOrEmpty(slot.ScenarioCodes)
            ? "尚無資料"
            : $"尚無資料（請在展示控制台準備劇本 {slot.ScenarioCodes}）";

    /// <summary>卡片上的筆數說明：有效幾次、排除幾次（回放與無效分開寫）。</summary>
    public static string SampleText(FlowMetrics metrics)
    {
        var excluded = new List<string>();
        if (metrics.ReplayExcludedCount > 0)
        {
            excluded.Add($"回放 {metrics.ReplayExcludedCount}");
        }

        if (metrics.ExcludedRunCount > 0)
        {
            excluded.Add($"無效 {metrics.ExcludedRunCount}");
        }

        return excluded.Count == 0
            ? $"有效 {metrics.RunCount} 次"
            : $"有效 {metrics.RunCount} 次（不計：{string.Join("、", excluded)}）";
    }

    /// <summary>端到端、客服處理只平均有值的執行：有值筆數比有效次數少時回傳有值筆數，否則 null。</summary>
    public static int? PartialSampleCount(BenefitMetric metric, BenefitAverages metrics)
    {
        var samples = metric.SampleCount?.Invoke(metrics) ?? metrics.RunCount;
        return samples < metrics.RunCount ? samples : null;
    }

    /// <summary>平均值；沒有流程或沒有有效筆數時為 null。</summary>
    public static double? Average(BenefitMetric metric, BenefitAverages? metrics)
        => metrics is { RunCount: > 0 } ? metric.Average(metrics) : null;

    /// <summary>明細的「計入平均」狀態文字。</summary>
    public static string RunStatusText(RunMetrics run)
        => run.ExclusionReason ?? (run.IsCompleted ? "計入（已完成）" : "計入（未完成，不進推估）");
}
