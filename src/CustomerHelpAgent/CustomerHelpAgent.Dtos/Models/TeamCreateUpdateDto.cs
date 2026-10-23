using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CustomerHelpAgent.Dtos.Models;

/// <summary>
/// 團隊資料傳輸物件
/// </summary>
public class TeamCreateUpdateDto
{
    /// <summary>
    /// 樂觀並行的版本號（0.9.93 起）。PUT 必填：請帶上 GET 取得的值；與資料庫目前的值不同代表別人已先修改，回 409。
    /// POST 會忽略這個欄位。
    /// </summary>
    public string? ConcurrencyStamp { get; set; }

    public TeamCreateUpdateDto()
    {
    }

    /// <summary>
    /// 團隊唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "團隊唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 團隊名稱
    /// </summary>
    [Required(ErrorMessage = "團隊名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 團隊代號 (選填，有填則需唯一)
    /// </summary>
    [StringLength(50, ErrorMessage = "代號長度不可超過 50 字元")]
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// 團隊描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 上層部門的 Id（0.9.105 起，null＝最上層）。不可是自己、自己的下屬或已刪除的部門，否則回 400。
    /// </summary>
    [JsonPropertyName("parentId")]
    public int? ParentId { get; set; }

    /// <summary>
    /// 是否啟用
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 建立時間
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// 更新時間
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
