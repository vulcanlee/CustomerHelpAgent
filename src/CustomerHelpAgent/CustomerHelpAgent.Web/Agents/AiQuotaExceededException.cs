namespace CustomerHelpAgent.Web.Agents;

/// <summary>
/// Agent 的模型呼叫被 AI 用量上限擋下。訊息即 <c>AiQuotaService.BlockedMessage</c>，可直接給使用者看。
/// </summary>
public sealed class AiQuotaExceededException(string message) : Exception(message);
