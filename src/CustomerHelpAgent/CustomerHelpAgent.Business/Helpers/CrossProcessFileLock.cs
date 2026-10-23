namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 跨行程的獨佔檔案鎖：以 <see cref="FileShare.None"/> 開啟鎖檔，取得的控制代碼 Dispose 時釋放。
///
/// 為什麼是檔案鎖：IIS 重疊回收、web garden、兩個站台共用資料庫時會有多個行程；named Mutex 只在同一個工作階段有效，
/// 檔案鎖則與行程類型無關，而且行程結束（包括被砍）時作業系統會自動釋放，不會留下「沒人持有卻解不開」的鎖。
/// 鎖檔刻意不刪：刪除會和下一個開檔的行程競爭。停站時可以安全刪除。
///
/// 同一個行程裡的第二個控制代碼一樣會被擋下，所以測試可以用兩個執行個體模擬兩個行程。
/// 用途：資料庫初始化（<c>DatabaseInitializationLock</c>）、排程作業（同一個作業不重疊執行，0.9.96 起）。
/// </summary>
public static class CrossProcessFileLock
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// 試著立即取得鎖：成功回傳控制代碼，**被別的控制代碼佔著**時回 null。
    /// 權限不足、路徑錯誤等其他 IO 錯誤照樣丟出 —— 把它們當成「被佔著」會讓作業永遠默默不執行。
    /// </summary>
    public static IDisposable? TryAcquire(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsHeldByAnotherHandle(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 由 SQLite 連線字串的 DataSource 推出資料庫檔的完整路徑；記憶體資料庫或沒有檔案路徑時回 null（不需要鎖）。
    /// 鎖檔一律放在 EF 實際開啟的那個檔案旁邊，而不是另外從設定組路徑 —— 兩者不一致時，鎖就鎖錯地方了。
    /// </summary>
    public static string? ResolveDatabaseFile(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(dataSource);
    }

    /// <summary>只有「別的控制代碼佔著這個檔案」才值得重試；其餘 IO 錯誤（磁碟、權限、路徑）重試也不會好。</summary>
    public static bool IsHeldByAnotherHandle(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }
}
