using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 還原頁面用的一筆對話（<see cref="AfterSalesAgentRunner.GetHistoryAsync"/>）：
/// <list type="bullet">
/// <item><see cref="AgentStepKind.UserMessage"/>：顧客說的話（<see cref="Text"/>）。</item>
/// <item><see cref="AgentStepKind.TurnResult"/>：一輪的收尾。<see cref="Text"/> 是顧客當時看到的回覆；<see cref="IsProgramReply"/> 為 true
/// 表示那是程式說明（以系統訊息呈現）；另帶本輪的規則卡與停止說明。</item>
/// <item><see cref="AgentStepKind.ConfirmationShown"/>：最後一張未決的確認卡（<see cref="PendingCard"/>），一定是最後一筆。</item>
/// </list>
/// </summary>
public sealed record AgentHistoryItem(
    AgentStepKind Kind,
    AgentDataSource Source,
    string Text,
    bool IsProgramReply,
    IReadOnlyList<AgentPolicyCard> PolicyCards,
    string? StopExplanation,
    ConfirmationCard? PendingCard,
    DateTime OccurredAt);
