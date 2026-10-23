using System.Text;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 把查詢結果組成要送給模型的使用者訊息。純函式，無 I/O、無 DI。
/// </summary>
public static class AiLogPromptBuilder
{
    /// <summary>每筆日誌之間的分隔行。</summary>
    public const string EntrySeparator = "---";

    /// <summary>
    /// 組出使用者訊息。只有一道處理：從時間正序的來源取最新
    /// <paramref name="maxEntries"/> 筆，內容<b>原封不動</b>送出。
    ///
    /// <para>
    /// 0.9.7 起<b>完全不做字元層級的截斷</b>。原本有兩道（每筆截到固定長度、總量預算），
    /// 兩道都移除了：它們會把真正有用的堆疊切掉尾巴，而堆疊尾巴常常才是根因所在。
    /// 有多少字就送多少字。
    /// </para>
    /// <para>
    /// ⚠️ 代價是<b>提示詞大小只剩筆數這一道界線</b>。日誌檢視會把續行併成同一筆，
    /// 所以理論上一筆失控的堆疊就可能有數 MB。真的撞到模型的內容視窗上限時，
    /// 會收到 400 <c>context_length_exceeded</c>，而
    /// <see cref="AiLogAnalysisService"/> 會把它翻成「請縮小時間區間或減少查詢筆數」。
    /// 這是刻意的取捨：寧可偶爾撞牆並明確告知，也不要每次都默默切掉內容。
    /// </para>
    /// </summary>
    public static AiPromptBuildResult Build(IReadOnlyList<LogEntry>? entriesAscending, int maxEntries)
    {
        var total = entriesAscending?.Count ?? 0;
        if (entriesAscending is null || total == 0)
        {
            return new AiPromptBuildResult();
        }

        // 設定值可能被人改成 0 或負數。這裡夾住而不是丟例外，避免整個功能因設定手誤全掛。
        var entryLimit = Math.Max(1, maxEntries);

        var skip = Math.Max(0, total - entryLimit);
        var droppedByEntryLimit = skip > 0;

        // 統一換行為 LF，讓字元數與模型實際看到的內容一致。內容本身完整保留。
        var blocks = entriesAscending
            .Skip(skip)
            .Select(entry => (entry.Raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'))
            .ToList();

        // ⚠️ 一律用 '\n' 而不是 AppendLine：AppendLine 會用 Environment.NewLine，在 Windows 上
        // 是 CRLF，會讓訊息混用兩種行尾。固定 LF 讓輸出跨平台一致。
        var builder = new StringBuilder();
        builder.Append("以下是 ").Append(blocks.Count).Append(" 筆應用程式日誌（時間正序，最舊在前），每筆以 ")
            .Append(EntrySeparator).Append(" 分隔。\n");
        if (droppedByEntryLimit)
        {
            builder.Append("（原始查詢共 ").Append(total)
                .Append(" 筆，受筆數上限僅送出最新 ").Append(blocks.Count).Append(" 筆。）\n");
        }

        foreach (var block in blocks)
        {
            builder.Append(EntrySeparator).Append('\n').Append(block).Append('\n');
        }

        var message = builder.ToString();
        return new AiPromptBuildResult
        {
            UserMessage = message,
            TotalEntryCount = total,
            IncludedEntryCount = blocks.Count,
            DroppedByEntryLimit = droppedByEntryLimit,
            CharacterCount = message.Length,
        };
    }
}
