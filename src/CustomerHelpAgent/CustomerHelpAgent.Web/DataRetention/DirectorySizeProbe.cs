namespace CustomerHelpAgent.Web.DataRetention;

public enum FolderUsageStatus
{
    /// <summary>設定檔沒有設定這個路徑。</summary>
    NotConfigured,

    /// <summary>資料夾還不存在（還沒寫過任何檔案）。</summary>
    Missing,

    Ok,

    /// <summary>逾時或讀取途中出錯：<see cref="FolderUsage.Bytes"/> 是「至少」這麼多。</summary>
    Partial,
}

public sealed record FolderUsage(FolderUsageStatus Status, long Bytes, int Files)
{
    public static readonly FolderUsage NotConfigured = new(FolderUsageStatus.NotConfigured, 0, 0);
}

/// <summary>
/// 加總資料夾（含子資料夾）裡所有檔案的大小（0.9.133 起，「資料保留與清理」頁）。
/// 略過沒有權限的資料夾與連結（reparse point），不會繞進別的磁碟或無窮迴圈；
/// 被取消或途中出錯時回已加總的部分並標成 <see cref="FolderUsageStatus.Partial"/>，不拋例外。
/// </summary>
public static class DirectorySizeProbe
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static FolderUsage Measure(string? path, CancellationToken cancellationToken)
        => Measure(path, _ => true, cancellationToken);

    /// <param name="include">只算符合條件的檔案（例如日誌目錄只算 .log）。</param>
    public static FolderUsage Measure(string? path, Func<FileInfo, bool> include, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FolderUsage.NotConfigured;
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return new FolderUsage(FolderUsageStatus.Missing, 0, 0);
        }

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", Options))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new FolderUsage(FolderUsageStatus.Partial, bytes, files);
                }

                if (include(file))
                {
                    bytes += file.Length;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列舉途中檔案被清理作業刪掉之類的競態：回已加總的部分。
            return new FolderUsage(FolderUsageStatus.Partial, bytes, files);
        }

        return new FolderUsage(FolderUsageStatus.Ok, bytes, files);
    }
}
