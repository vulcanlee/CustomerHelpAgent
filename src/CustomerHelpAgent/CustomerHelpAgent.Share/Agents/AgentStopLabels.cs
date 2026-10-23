namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// 停止原因的白話說明（顧客的幕後面板與後台「Agent 執行紀錄」共用同一份）。
/// 步數與連續失敗上限只在呼叫端知道時才寫進文字（後台列表不帶，顯示「步數上限」「連續失敗」）。
/// </summary>
public static class AgentStopLabels
{
    public static string Text(AgentStopReason reason, int? maxSteps = null, int? maxConsecutiveToolFailures = null) => reason switch
    {
        AgentStopReason.Answered => "本輪結束：Agent 已回覆。",
        AgentStopReason.AwaitingUserReply => "本輪停下：Agent 向你追問，等待你回覆。",
        AgentStopReason.AwaitingConfirmation => "本輪停下：等待你確認申請內容；按下確認前不會建立申請。",
        AgentStopReason.AwaitingPhotoUpload => "本輪停下：等待你上傳照片，上傳完成才會繼續。",
        AgentStopReason.StepLimitReached => maxSteps is { } steps
            ? $"程式停止本輪：已達步數上限（{steps} 步）。"
            : "程式停止本輪：已達步數上限。",
        AgentStopReason.ConsecutiveToolFailures => maxConsecutiveToolFailures is { } failures
            ? $"程式停止本輪：工具連續失敗（上限 {failures} 次）。"
            : "程式停止本輪：工具連續失敗。",
        AgentStopReason.Forbidden => "程式立即停止：登入身分核對未通過。",
        AgentStopReason.ServiceUnavailable => "程式停止本輪：服務暫時無法使用。",
        AgentStopReason.Timeout => "程式停止本輪：工具逾時。",
        AgentStopReason.PolicyConflict => "本輪結束：規則衝突，已封鎖建立申請。",
        AgentStopReason.QuotaExceeded => "本輪中止：已達 AI 用量上限。",
        AgentStopReason.ModelError => "本輪中止：模型呼叫失敗。",
        AgentStopReason.Canceled => "本輪已取消。",
        _ => reason.ToString(),
    };
}
