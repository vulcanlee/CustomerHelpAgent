using System.ComponentModel.DataAnnotations;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 劇本執行（M6）：展示者每按一次「準備此劇本」就建立一筆，之後的流程埋點（<see cref="ProcessMetric.DemoRunId"/>）都蓋上這個編號，
/// 效益對照頁才能依劇本分組。重置示範資料不會刪除這些紀錄。刻意不設外鍵（埋點只存編號）。
/// </summary>
public class DemoScenarioRun
{
    public int Id { get; set; }

    [MaxLength(16)]
    public string ScenarioCode { get; set; } = string.Empty;

    /// <summary>情境家族（例：換尺碼、一句兩事）。</summary>
    [MaxLength(32)]
    public string Family { get; set; } = string.Empty;

    public ScenarioFlow Flow { get; set; }

    /// <summary>示範帳號代號（不是顧客個資）。</summary>
    [MaxLength(64)]
    public string Account { get; set; } = string.Empty;

    /// <summary>建立當下是否為回放模式；回放的執行不納入效益統計。</summary>
    public bool IsReplay { get; set; }

    public DateTime StartedAt { get; set; }
}
