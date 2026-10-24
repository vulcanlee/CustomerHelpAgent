namespace CustomerHelpAgent.Share.Demo;

/// <summary>
/// 錄製模型呼叫的來源（<c>AgentRecordedCall.Stream</c>，M4 起）。同一段錄製（以顧客會話的 SessionKey＝申請的 CaseKey 對應）
/// 裡，顧客端 Agent 與 AI 初審的各模型節點各自一條 stream、各自編號，回放時依 stream 分開取用。
/// ⚠️ 值會存進資料庫：不要改名。
/// </summary>
public static class AgentRecordingStreams
{
    /// <summary>顧客端售後 Agent（M3 以前的錄製都是這一條）。</summary>
    public const string Customer = "customer";

    /// <summary>AI 初審：照片初審節點。</summary>
    public const string PreReviewPhoto = "prereview:photo";

    /// <summary>AI 初審：歷史紀錄初審節點。</summary>
    public const string PreReviewHistory = "prereview:history";

    /// <summary>AI 初審：摘要節點。</summary>
    public const string PreReviewSummary = "prereview:summary";
}
