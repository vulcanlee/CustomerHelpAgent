using Microsoft.EntityFrameworkCore.ChangeTracking;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 軟刪除的狀態變更（0.9.94 起，見 <see cref="ISoftDeletable"/>）。
///
/// 刪除與還原都會換新版本號：正在編輯同一筆的人存檔時會收到「已被其他人修改或刪除」，
/// 而不是把修改寫進一筆已刪除的資料；「永久刪除」與「同時被還原」的競態也會丟出衝突，而不是把剛還原的資料刪掉。
/// </summary>
public static class SoftDeleteHelper
{
    public static void MarkDeleted<TEntity>(TEntity entity, string? actorAccount)
        where TEntity : class, ISoftDeletable, IConcurrencyStamped
    {
        entity.IsDeleted = true;
        entity.DeletedAt = DateTime.Now;
        entity.DeletedBy = actorAccount;
        entity.ConcurrencyStamp = ConcurrencyStampHelper.New();
    }

    public static void Restore<TEntity>(TEntity entity)
        where TEntity : class, ISoftDeletable, IConcurrencyStamped
    {
        entity.IsDeleted = false;
        entity.DeletedAt = null;
        entity.DeletedBy = null;
        entity.ConcurrencyStamp = ConcurrencyStampHelper.New();
    }

    /// <summary>
    /// 一般的編輯存檔不得改動軟刪除欄位。
    ///
    /// ⚠️ 整筆覆蓋的寫法（attach + <c>EntityState.Modified</c>、API 的 <c>SetValues</c>）會把三個欄位寫回
    /// 畫面模型或 DTO 的預設值（<c>IsDeleted = false</c>）—— 等於把已刪除的資料「救活」。
    /// 每個呼叫 <see cref="ConcurrencyStampHelper.Apply{TEntity}"/> 的地方都要一併呼叫這個方法。
    /// </summary>
    public static void ProtectFlags<TEntity>(EntityEntry<TEntity> entry)
        where TEntity : class, ISoftDeletable
    {
        entry.Property(x => x.IsDeleted).IsModified = false;
        entry.Property(x => x.DeletedAt).IsModified = false;
        entry.Property(x => x.DeletedBy).IsModified = false;
    }
}
