using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 把資料庫的唯一索引違反轉成使用者看得懂的訊息。
///
/// 為什麼需要：唯一性前置檢查（BeforeAddCheckAsync）與實際寫入（AddAsync）各自開一個
/// DbContext、不在同一個交易裡，兩個並發請求可以同時通過檢查。唯一索引是最後一道防線，
/// 但服務層的 catch (Exception) 會把它轉成泛用的「新增失敗」，使用者看不出到底哪裡有問題。
/// </summary>
public static class UniqueConstraintHelper
{
    /// <summary>SQLite 的 SQLITE_CONSTRAINT 錯誤碼。</summary>
    private const int SqliteConstraintErrorCode = 19;

    /// <summary>
    /// 唯一索引違反時的訊息對應。鍵為 SQLite 例外訊息中的「資料表.欄位」，
    /// 例如 "UNIQUE constraint failed: Team.Code"。
    /// </summary>
    private static readonly (string Target, string Message)[] KnownConstraints =
    [
        ("Category.Name", "分類名稱已存在，無法儲存。"),
        ("Team.Name", "團隊名稱已存在，無法儲存。"),
        ("Team.Code", "團隊代號已存在，無法儲存。"),
    ];

    /// <summary>
    /// 判斷例外是否為已知的唯一索引違反，是的話輸出對應的中文訊息。
    /// </summary>
    public static bool TryGetFriendlyMessage(Exception exception, out string message)
    {
        message = string.Empty;

        if (exception is not DbUpdateException { InnerException: SqliteException sqliteException })
        {
            return false;
        }

        if (sqliteException.SqliteErrorCode != SqliteConstraintErrorCode)
        {
            return false;
        }

        foreach (var (target, knownMessage) in KnownConstraints)
        {
            if (sqliteException.Message.Contains(target, StringComparison.Ordinal))
            {
                message = knownMessage;
                return true;
            }
        }

        return false;
    }
}
