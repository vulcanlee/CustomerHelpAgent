using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Share.Shop;

/// <summary>
/// 申請管道的判斷（M5）：單一 Agent（<see cref="AfterSalesChannel.Agent"/>）與多 Agent（<see cref="AfterSalesChannel.AgentHandoff"/>）
/// 都是 AI 管道，一樣跑 M4 初審、顯示審核頁的 AI 面板與客服 AI 助手。
/// ⚠️ 判斷「是不是 AI 管道」一律用 <see cref="IsAi"/>，不要直接與 <c>AfterSalesChannel.Agent</c> 比較相等（會漏掉多 Agent 的申請；
/// <c>AfterSalesChannelTests</c> 守門）；EF 查詢無法轉譯擴充方法，改用 <c>AiChannels.Contains(x.Channel)</c>。
/// </summary>
public static class AfterSalesChannelExtensions
{
    /// <summary>所有 AI 管道（EF 查詢用，轉譯成 <c>IN</c>）。</summary>
    public static IReadOnlyList<AfterSalesChannel> AiChannels { get; } = [AfterSalesChannel.Agent, AfterSalesChannel.AgentHandoff];

    /// <summary>是否為 AI 管道（單一或多 Agent）。</summary>
    public static bool IsAi(this AfterSalesChannel channel)
        => channel is AfterSalesChannel.Agent or AfterSalesChannel.AgentHandoff;

    /// <summary>Agent 會話模式對應的申請管道：多 Agent → 「AI 多 Agent」，其餘 → 「AI 助理」。</summary>
    public static AfterSalesChannel ForAgentMode(AgentMode mode)
        => mode == AgentMode.Handoff ? AfterSalesChannel.AgentHandoff : AfterSalesChannel.Agent;
}
