using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 還原頁面用的一筆對話（<see cref="AfterSalesAgentRunner.GetHistoryAsync"/>）：
/// <list type="bullet">
/// <item><see cref="AgentStepKind.UserMessage"/>：顧客說的話（<see cref="Text"/>）。</item>
/// <item><see cref="AgentStepKind.TurnResult"/>：一輪的收尾。<see cref="Text"/> 是顧客當時看到的回覆；<see cref="IsProgramReply"/> 為 true
/// 表示那是程式說明（以系統訊息呈現）；另帶本輪的規則卡與停止說明。</item>
/// <item><see cref="AgentStepKind.ConfirmationShown"/>：最後一張未決的確認卡（<see cref="PendingCard"/>），一定是最後一筆。</item>
/// <item><see cref="AgentStepKind.Handoff"/>（多 Agent 模式，M5）：交接。<see cref="Text"/> 是白話「分流 Agent → 品質專員：理由」，
/// <see cref="Speaker"/> 是接手者的顯示名稱（頁面可據以插入「已轉給品質專員」）。</item>
/// </list>
/// <see cref="Speaker"/>：多 Agent 模式下 TurnResult 的發言者（最後一段回覆的 Agent 顯示名稱，程式說明為 null）；單一模式一律 null。
/// <see cref="Segments"/>：多 Agent 模式下 TurnResult 依發言者切開的各段（同一輪兩位以上發言時才有，見 <see cref="AgentTurnResult.Segments"/>）。
/// </summary>
public sealed record AgentHistoryItem(
    AgentStepKind Kind,
    AgentDataSource Source,
    string Text,
    bool IsProgramReply,
    IReadOnlyList<AgentPolicyCard> PolicyCards,
    string? StopExplanation,
    ConfirmationCard? PendingCard,
    DateTime OccurredAt,
    string? Speaker = null,
    IReadOnlyList<AgentReplySegment>? Segments = null);
