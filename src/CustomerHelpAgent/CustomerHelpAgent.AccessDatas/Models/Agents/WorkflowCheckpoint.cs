namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// MAF Workflow 的 checkpoint（JSON 內容）。<see cref="SessionId"/> 即 <see cref="PreReviewRun.WorkflowSessionId"/>；
/// 刻意不設外鍵，重置示範資料時與初審一起刪除。<see cref="Id"/> 遞增，即「由舊到新」的順序。
/// </summary>
public class WorkflowCheckpoint
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string CheckpointId { get; set; } = string.Empty;
    public string? ParentCheckpointId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
