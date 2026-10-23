using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

/// <summary>
/// 線上客服的幕後面板（1.0.4 起）：依序列出 <see cref="AgentTraceEvent"/>（來源色標＋白話一句，由 <see cref="AgentStepTimeline"/> 渲染），
/// 有原始資料的列可展開 <c>&lt;pre&gt;</c> 看 <see cref="AgentTraceEvent.RawJson"/>；底部顯示最近一輪的停止原因白話。
/// </summary>
public partial class AgentBackstagePanel
{
    private static readonly AgentDataSource[] LegendOrder =
    [
        AgentDataSource.Conversation,
        AgentDataSource.Database,
        AgentDataSource.Policy,
        AgentDataSource.Memory,
        AgentDataSource.Program,
    ];

    /// <summary>本頁目前會話的事件（依發生順序）。開新對話時呼叫端會換成空清單。</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<AgentTraceEvent> Events { get; set; } = [];

    private AgentTraceEvent? LatestStop => Events.LastOrDefault(x => x.Kind == AgentStepKind.Stop);
}
