namespace CustomerHelpAgent.Business.Repositories;

/// <summary>
/// Repository 修改的結果（0.9.131 起）。<see cref="Diff"/> 是給稽核詳細內容檔的修改前後差異，
/// 由 Controller 經 <c>WriteAuditAsync(content: …)</c> 寫入；沒有任何欄位改變時為 null。
/// </summary>
public sealed record UpdateOutcome(bool Updated, string? Diff = null)
{
    public static UpdateOutcome NotFound { get; } = new(false);
}
