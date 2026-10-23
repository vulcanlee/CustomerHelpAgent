namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 一輪的模型呼叫總數已達上限（<see cref="TracingChatClient"/> 的 <c>turnBudget</c>，＝ <c>MaxSteps + 1</c>），
/// 下一次呼叫在送出前就被擋下。Runner 把它判定為 <c>StepLimitReached</c>。
/// </summary>
internal sealed class AgentTurnBudgetExceededException(int budget)
    : Exception($"The after-sales agent turn reached its model-call budget ({budget}).");
