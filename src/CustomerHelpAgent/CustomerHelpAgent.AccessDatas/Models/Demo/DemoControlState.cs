using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 展示控制狀態（1.0.5 起）：固定單列（<see cref="Id"/> = 1），由 <c>DemoControlService</c> 讀寫並快取在記憶體。
/// 只在 <c>DemoSettings:Enabled</c> 時有意義；關閉時服務一律視為無故障、即時模式。
/// </summary>
public class DemoControlState
{
    /// <summary>固定為 <see cref="SingletonId"/>，不自動產生。</summary>
    public int Id { get; set; }

    /// <summary>啟用中的故障，JSON 字串陣列（<see cref="DemoFault"/> 的名稱，例：<c>["PolicyConflict"]</c>）。</summary>
    public string FaultsJson { get; set; } = "[]";

    public DemoMode Mode { get; set; }

    /// <summary>回放模式使用的錄製；即時模式為 null。刻意不設外鍵（錄製可被刪除）。</summary>
    public int? ActiveRecordingId { get; set; }

    /// <summary>顧客頁幕後面板的預設開關（使用者在本機的偏好優先）。</summary>
    public bool BackstageDefaultOn { get; set; } = true;

    /// <summary>目前的劇本執行（M6，<see cref="DemoScenarioRun.Id"/>）；流程埋點據此蓋上編號。重置時清空。刻意不設外鍵。</summary>
    public int? ActiveRunId { get; set; }

    public DateTime UpdatedAt { get; set; }

    public const int SingletonId = 1;
}
