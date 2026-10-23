using Microsoft.EntityFrameworkCore.ChangeTracking;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 樂觀並行的版本號操作（0.9.93 起，見 <see cref="IConcurrencyStamped"/>）。
///
/// 規則：
/// <list type="bullet">
/// <item><b>使用者編輯存檔</b>的路徑（Blazor 服務的 UpdateAsync、API 的 PUT）呼叫 <see cref="Apply{TEntity}"/>。</item>
/// <item><b>新增</b>的路徑呼叫 <see cref="New"/> 指定新值，不信任客戶端傳來的版本號。</item>
/// <item>其他更新（登入失敗計數、改密碼、忘記密碼、種子資料、RBAC 回填）<b>不換版本號</b> ——
/// 否則管理員在某人登入期間編輯該帳號，會誤報「已被其他人修改」。這些路徑改用「只更新自己負責的欄位」避免互相覆蓋。</item>
/// </list>
/// </summary>
public static class ConcurrencyStampHelper
{
    public const string ConflictMessage = "這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。";

    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 以使用者開啟編輯時拿到的版本號作為比對基準，並換上新版本號。
    ///
    /// ⚠️ OriginalValue 一定要明確設定：「先載入再複製欄位」的寫法（tracked 載入、<c>SetValues</c>）
    /// 會讓 OriginalValue 等於剛從資料庫載入的值，拿它比對永遠相同，衝突就永遠偵測不到。
    /// </summary>
    public static void Apply<TEntity>(EntityEntry<TEntity> entry, string? expectedStamp)
        where TEntity : class, IConcurrencyStamped
    {
        var property = entry.Property(x => x.ConcurrencyStamp);
        property.OriginalValue = expectedStamp ?? string.Empty;
        property.CurrentValue = New();
    }
}
