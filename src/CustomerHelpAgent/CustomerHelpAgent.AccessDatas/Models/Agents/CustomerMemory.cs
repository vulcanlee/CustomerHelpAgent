using System.ComponentModel.DataAnnotations;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 顧客的長期記憶（M5）：經顧客確認後才寫入的偏好（鞋碼、衣服尺碼、顏色、溝通偏好），一種偏好只存一筆（新值覆蓋舊值）。
/// <see cref="CustomerUserId"/> 與 <see cref="SourceSessionKey"/> 刻意不設外鍵，只作比對與回查（與其他 Agent 資料表一致）；
/// 帳號被永久清除或示範資料重置時由程式一併刪除。
/// </summary>
public class CustomerMemory
{
    public int Id { get; set; }
    public int CustomerUserId { get; set; }
    public MemoryKind Kind { get; set; }

    [MaxLength(60)]
    public string Value { get; set; } = string.Empty;

    /// <summary>這筆記憶是在哪一段對話中建立的（可為空）。</summary>
    public Guid? SourceSessionKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
