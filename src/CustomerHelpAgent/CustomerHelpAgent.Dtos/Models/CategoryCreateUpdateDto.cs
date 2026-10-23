using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CustomerHelpAgent.Dtos.Models;

/// <summary>
/// 分類資料傳輸物件
/// </summary>
public class CategoryCreateUpdateDto
{
    /// <summary>
    /// 樂觀並行的版本號（0.9.93 起）。PUT 必填：請帶上 GET 取得的值；與資料庫目前的值不同代表別人已先修改，回 409。
    /// POST 會忽略這個欄位。
    /// </summary>
    public string? ConcurrencyStamp { get; set; }

    public CategoryCreateUpdateDto()
    {
    }

    /// <summary>
    /// 分類唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "分類唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 分類名稱
    /// </summary>
    [Required(ErrorMessage = "分類名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 分類描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 適用團隊（多值，以換行分隔字串儲存，可空；未設定表示所有團隊皆可使用）
    /// </summary>
    [JsonPropertyName("teams")]
    public string? Teams { get; set; }

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
