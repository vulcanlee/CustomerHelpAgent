using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

/// <summary>
/// 線上客服的幕後面板（1.0.4 起）：依序列出 <see cref="AgentTraceEvent"/>（來源色標＋白話一句，由 <see cref="AgentStepTimeline"/> 渲染），
/// 有原始資料的列可展開 <c>&lt;pre&gt;</c> 看 <see cref="AgentTraceEvent.RawJson"/>；底部顯示最近一輪的停止原因白話。
/// 有故障模擬開關啟用時（1.0.5 起），標頭下方顯示「故障模擬中：{清單}」（名稱與順序取 <see cref="DemoFaultLabels"/>），
/// 訂閱 <see cref="DemoControlService.Changed"/>，展示控制台一改就更新。
/// </summary>
public partial class AgentBackstagePanel : IDisposable
{
    private static readonly AgentDataSource[] LegendOrder =
    [
        AgentDataSource.Conversation,
        AgentDataSource.Database,
        AgentDataSource.Policy,
        AgentDataSource.Memory,
        AgentDataSource.Program,
    ];

    private bool disposed;

    [Inject]
    private DemoControlService DemoControlService { get; set; } = default!;

    /// <summary>本頁目前會話的事件（依發生順序）。開新對話時呼叫端會換成空清單。</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<AgentTraceEvent> Events { get; set; } = [];

    /// <summary>
    /// 停止原因下方要註明「照常有效」的待確認卡標題（M5）：呼叫端只在停止原因會讓人誤以為卡片失敗時才給——
    /// 例如同一輪先查到規則衝突、再提出記住偏好，停止原因是「規則衝突」，但記憶卡並沒有失敗。一般情況為 null（不顯示）。
    /// </summary>
    [Parameter]
    public string? PendingCardTitle { get; set; }

    private AgentTraceEvent? LatestStop => Events.LastOrDefault(x => x.Kind == AgentStepKind.Stop);

    /// <summary>啟用中的故障（控制台的顯示順序）；示範模式關閉時一律是空的。</summary>
    private string ActiveFaultText => string.Join("、", DemoFaultLabels.Ordered.Where(DemoControlService.IsFaultActive).Select(DemoFaultLabels.Name));

    protected override void OnInitialized() => DemoControlService.Changed += OnDemoControlChanged;

    public void Dispose()
    {
        disposed = true;
        DemoControlService.Changed -= OnDemoControlChanged;
    }

    /// <summary>控制台在別的連線上觸發：回到本元件的同步內容再重繪。</summary>
    private void OnDemoControlChanged()
    {
        if (disposed)
        {
            return;
        }

        _ = InvokeAsync(StateHasChanged);
    }
}
