namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 有編輯畫面、需要樂觀並行控制的實體（0.9.93 起：Project、Category、Team、MyUser、RoleView）。
///
/// <see cref="ConcurrencyStamp"/> 在 <c>BackendDBContext</c> 設為 concurrency token：存檔時 EF 產生
/// <c>UPDATE … WHERE Id = @id AND ConcurrencyStamp = @expected</c>，別人先存過（版本號已換）就影響 0 列並丟出
/// <c>DbUpdateConcurrencyException</c>。SQLite 沒有 rowversion，版本號由程式自己更換：
/// 只在「使用者編輯存檔」的路徑呼叫 <c>ConcurrencyStamp.Apply</c>（<c>CustomerHelpAgent.Business/Helpers/</c>）。
/// </summary>
public interface IConcurrencyStamped
{
    string ConcurrencyStamp { get; set; }
}
