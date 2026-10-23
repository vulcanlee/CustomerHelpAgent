using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 已軟刪除資料的保留期限（<c>SoftDeleteSettings</c> 區段，0.9.97 起）。
///
/// 專案、分類、團隊、使用者、角色被刪除後，超過 <see cref="PurgeAfterDays"/> 天就由排程作業「已刪除資料清理」永久刪除
/// （預設每天 03:00，見「排程作業」頁）；在那之前可在各頁的「顯示已刪除」中還原。設成 <c>0</c> 代表不自動永久刪除。
/// </summary>
public class SoftDeleteSettings
{
    public const string SectionName = "SoftDeleteSettings";

    public const int DefaultPurgeAfterDays = 90;

    /// <summary>刪除後超過幾天就永久刪除（依 <c>DeletedAt</c>，本地時間）。0＝不自動永久刪除。</summary>
    [Range(0, 36500)]
    public int PurgeAfterDays { get; set; } = DefaultPurgeAfterDays;
}
