using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>錄製中的一個顧客動作（送出一句話、確認／取消、上傳照片），回放時用來提示下一步。</summary>
public class AgentRecordedTurn
{
    public int Id { get; set; }
    public int AgentRecordingId { get; set; }
    public AgentRecording? AgentRecording { get; set; }

    /// <summary>同一段錄製內的順序（由 1 起遞增）。</summary>
    public int Sequence { get; set; }

    public AgentRecordedTurnKind Kind { get; set; }

    /// <summary>顧客送出的文字（上傳照片時為代送的「（已上傳 N 張照片）」；確認／取消為 null）。</summary>
    public string? CustomerText { get; set; }

    public DateTime CreatedAt { get; set; }
}
