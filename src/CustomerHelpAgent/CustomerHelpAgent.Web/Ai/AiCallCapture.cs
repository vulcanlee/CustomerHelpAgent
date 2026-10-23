using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 一次 AI 呼叫的「AI 對話紀錄」擷取狀態（0.9.72 起）。
///
/// 用法：送出前建立並呼叫 <see cref="MarkSent"/>；各分支填入回應與結果；
/// 在呼叫點的<b>單一 <c>finally</c></b> 裡呼叫 <see cref="RecordSafelyAsync"/>。
/// 這樣連非預期例外與使用者取消都會留下紀錄，而「設定不完整、根本沒送出」不會。
///
/// ⚠️ 只擷取請求 body 與回應，<b>絕不碰 HTTP 標頭</b> —— API 金鑰只在單次請求的標頭上。
/// </summary>
internal sealed class AiCallCapture
{
    private bool? success;
    private string? failureReason;

    public AiCallCapture(
        string operation,
        string provider,
        string requestedModel,
        string? account,
        int? userId,
        Uri endpoint,
        string requestBody,
        string? relatedInfo,
        Guid? conversationId)
    {
        Operation = operation;
        Provider = provider;
        RequestedModel = requestedModel;
        Account = string.IsNullOrWhiteSpace(account) ? null : account;
        UserId = userId == 0 ? null : userId;
        Endpoint = DescribeEndpoint(endpoint);
        RequestBody = requestBody;
        RelatedInfo = relatedInfo;
        ConversationId = conversationId;
    }

    public Guid CallId { get; } = Guid.NewGuid();

    public DateTime OccurredAt { get; } = DateTime.Now;

    public string Operation { get; }

    public string Provider { get; }

    public string RequestedModel { get; }

    public string? Account { get; }

    public int? UserId { get; }

    public string Endpoint { get; }

    public string RequestBody { get; }

    public string? RelatedInfo { get; }

    public Guid? ConversationId { get; }

    /// <summary>請求是否已送出。只有送出過的呼叫才記錄。</summary>
    public bool Sent { get; private set; }

    public int? HttpStatus { get; set; }

    public string? ResponseBody { get; set; }

    public string ResponseText { get; set; } = string.Empty;

    public string? FinishReason { get; set; }

    public string? ResponseModel { get; set; }

    public string? ExceptionType { get; set; }

    /// <summary>緊接在 <c>SendAsync</c> 之前呼叫。</summary>
    public void MarkSent() => Sent = true;

    /// <summary>設定這次呼叫的結果。從未設定時視為非預期失敗（Unexpected）。</summary>
    public void SetOutcome(bool isSuccess, string? reason)
    {
        success = isSuccess;
        failureReason = isSuccess ? null : reason;
    }

    public AiCallLogEntry ToEntry(TimeSpan elapsed) => new()
    {
        CallId = CallId,
        OccurredAt = OccurredAt,
        Operation = Operation,
        Provider = Provider,
        RequestedModel = RequestedModel,
        Model = string.IsNullOrWhiteSpace(ResponseModel) ? RequestedModel : ResponseModel,
        Account = Account,
        UserId = UserId,
        Endpoint = Endpoint,
        RelatedInfo = RelatedInfo,
        ConversationId = ConversationId,
        RequestBody = RequestBody,
        HttpStatus = HttpStatus,
        ResponseBody = ResponseBody,
        ResponseText = ResponseText,
        FinishReason = string.IsNullOrEmpty(FinishReason) ? null : FinishReason,
        Success = success ?? false,
        FailureReason = success is null ? AiAnalysisFailureReason.Unexpected.ToString() : failureReason,
        ExceptionType = ExceptionType,
        ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
    };

    /// <summary>
    /// 記錄這次呼叫（只在已送出時）。
    ///
    /// ⚠️ 這支會在呼叫點的 <c>finally</c> 裡執行，而 <c>finally</c> 丟出的例外會蓋掉原本的回傳值，
    /// 所以即使 recorder 已保證不丟，這裡仍再包一層。
    /// </summary>
    public static async Task RecordSafelyAsync(
        IAiCallLogRecorder recorder,
        AiCallCapture capture,
        TimeSpan elapsed,
        ILogger logger)
    {
        if (capture.Sent == false || recorder.IsEnabled == false)
        {
            return;
        }

        try
        {
            await recorder.RecordAsync(capture.ToEntry(elapsed));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record AI call log. Operation={Operation}", capture.Operation);
        }
    }

    /// <summary>只保留 scheme、主機、連接埠與路徑 —— 絕不帶查詢字串或使用者資訊。</summary>
    private static string DescribeEndpoint(Uri endpoint)
        => endpoint.IsDefaultPort
            ? $"{endpoint.Scheme}://{endpoint.Host}{endpoint.AbsolutePath}"
            : $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}{endpoint.AbsolutePath}";
}
