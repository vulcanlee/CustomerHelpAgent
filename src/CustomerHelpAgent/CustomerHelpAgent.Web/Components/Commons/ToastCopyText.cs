using System.Text;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 右下角通知「複製」按鈕放進剪貼簿的文字：使用者回報問題時一次貼出標題、內文、追蹤碼、時間與頁面，
/// 管理員拿追蹤碼就能在 /logs 找到同一筆紀錄。
///
/// 追蹤碼獨立一行，所以傳進來的必須是**原始內文**（UnexpectedError 畫面上的「（錯誤追蹤碼：…）」後綴不要帶進來），
/// 否則同一個碼會出現兩次。
///
/// <paramref name="detail"/>（0.9.130 起）是完整例外（<c>ex.ToString()</c>）或 AI 失敗的 HTTP 狀態與上游代碼，
/// 只在管理員或 <c>ExceptionDetailPolicy</c> 允許時由 <see cref="ToastCopyButton"/> 傳進來，接在最後。
/// </summary>
internal static class ToastCopyText
{
    public static string Build(string title, string description, string? traceCode, DateTime occurredAt, string? pageUrl, string? detail = null)
    {
        var text = new StringBuilder()
            .AppendLine(title)
            .AppendLine(description);

        if (!string.IsNullOrEmpty(traceCode))
        {
            text.AppendLine($"錯誤追蹤碼：{traceCode}");
        }

        text.AppendLine($"時間：{occurredAt:yyyy/MM/dd HH:mm:ss}");

        if (!string.IsNullOrEmpty(pageUrl))
        {
            text.AppendLine($"頁面：{pageUrl}");
        }

        if (!string.IsNullOrEmpty(detail))
        {
            text.AppendLine()
                .AppendLine("----- 錯誤詳細資料開始 -----")
                .AppendLine(detail.TrimEnd())
                .AppendLine("----- 錯誤詳細資料結束 -----");
        }

        return text.ToString().TrimEnd();
    }
}
