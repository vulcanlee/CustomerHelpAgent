namespace CustomerHelpAgent.Share.Helpers;

/// <summary>
/// 位元組大小的顯示格式化。
/// </summary>
public static class SizeFormatHelper
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// 以 1024 為底換算並附上單位，例如 668.1 KB。
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < Units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {Units[unitIndex]}";
    }
}
