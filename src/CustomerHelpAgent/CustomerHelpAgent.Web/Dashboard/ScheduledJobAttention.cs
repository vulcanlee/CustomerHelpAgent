using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Web.Dashboard;

/// <summary>一個需要管理員注意的排程作業。</summary>
public sealed record ScheduledJobAttentionItem(string DisplayName, string Reason);

/// <summary>
/// 首頁「排程作業」小工具要列出的（0.9.106 起）：上次執行失敗或中斷、逾期未執行、已停用。
/// 與「排程作業」頁、系統健康監控讀同一份總覽（<see cref="ScheduledJobOverviewService"/>）。
/// </summary>
public static class ScheduledJobAttention
{
    public const string FailedReason = "上次執行失敗";
    public const string InterruptedReason = "上次執行被中斷";
    public const string OverdueReason = "逾期未執行";
    public const string DisabledReason = "已停用";

    public static IReadOnlyList<ScheduledJobAttentionItem> From(ScheduledJobOverview overview)
    {
        var result = new List<ScheduledJobAttentionItem>();
        foreach (var item in overview.Items)
        {
            if (!item.IsEnabled)
            {
                result.Add(new(item.DisplayName, DisabledReason));
                continue;
            }

            if (item.LastFinishedRun?.Status == JobRunStatuses.Failed)
            {
                result.Add(new(item.DisplayName, FailedReason));
            }
            else if (item.LastFinishedRun?.Status == JobRunStatuses.Interrupted)
            {
                result.Add(new(item.DisplayName, InterruptedReason));
            }

            if (item.OverdueSlotUtc is not null)
            {
                result.Add(new(item.DisplayName, OverdueReason));
            }
        }

        return result;
    }
}
