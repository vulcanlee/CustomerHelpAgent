using MigraDoc.DocumentObjectModel;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 讓中文在 MigraDoc 裡換得了行。
///
/// ⚠️ <b>為什麼需要這支</b>：MigraDoc 6.2.4 的 <c>PdfFlattenVisitor</c> 把段落切成排版單位時，
/// 斷行機會是一組<b>寫死且封閉</b>的字元 —— 空白、減號、軟連字號、U+200B、U+200C。
/// 中文落在該 switch 的 <c>default</c> 分支被一路累積，所以「一整段沒有空格的中文」
/// 會變成<b>單一個不可分割的字</b>；<c>ParagraphRenderer.HandleNonFittingLine()</c>
/// 遇到比整行還寬的「字」時，會把行寬設成整個字的寬度，也就是<b>刻意讓它溢出頁面</b>。
/// MigraDoc 沒有任何 <c>WordWrap</c>／<c>BreakStrategy</c> 可調，也沒有 UAX #14 斷行。
///
/// <para>
/// <b>做法：把文字切成多個 <c>Text</c> 元素，而不是插入零寬空格。</b>
/// MigraDoc 把每一個 <c>Text</c> 當成一個「字」，相鄰兩個之間就能斷行 —— 實測
/// 3000 字的中文以單次 <c>AddText</c> 排出 1 頁（一條衝出頁面的長線），
/// 逐段 <c>AddText</c> 則正確排成 2 頁。
/// </para>
///
/// ⚠️ <b>不要改用插入 U+200B 的寫法。</b>看起來更簡單，但本專案內嵌的 Noto Sans TC
/// <b>沒有 U+200B 的字圖</b>（cmap 查得到的是 glyph 0 / .notdef，而該字圖有 5 條輪廓、
/// 寬度整整 1 個 em）。實測在 10pt 下一個 U+200B 量到 <b>10pt</b>，
/// 13 個字的句子會從 130pt 膨脹到 250pt —— 等於每兩個中文字之間插一個看得見的方框。
/// 切成多個元素則<b>完全不新增任何字元</b>：沒有寬度、沒有方框，
/// 從 PDF 複製出來的文字也和原文一字不差。
///
/// ⚠️ 任何要進 PDF 的中文都必須經過這裡。新增 PDF 產生器時別忘了。
/// </summary>
internal static class CjkLineBreak
{
    /// <summary>
    /// 不斷行空格。用來把「標籤」與「數字」黏在一起（見 TokenUsageReportPdfBuilder 的合計行）——
    /// MigraDoc 只把 ASCII <c>' '</c> 當斷行機會，換成這個就黏住了。
    ///
    /// 這個字元<b>有字圖</b>（Noto Sans TC 的 cmap 把它對到 glyph 1，與一般空格同一個），
    /// 所以不會變成方框。
    /// </summary>
    internal const char NoBreakSpace = '\u00A0';

    /// <summary>
    /// 不可置於行首的字元 ⇒ 不可在它<b>前面</b>斷行（中文排版的行首禁則）。
    /// 少了這個，句子會斷成「…兩者皆不另計入合計」換行「。費用為…」，行首掛一個句號。
    ///
    /// 全形空格（U+3000）也列在這裡：斷在它前面會讓下一行以一個看得見的全形縮排開頭。
    /// </summary>
    private const string NoBreakBefore = "。、，．；：！？）〉》」』】〕｝…‥・ー々\u3000,.;:!?)]}%";

    /// <summary>
    /// 不可置於行尾的字元 ⇒ 不可在它<b>後面</b>斷行。
    /// 開括號落在行尾、內容卻跑到下一行，讀起來會斷掉。
    /// </summary>
    private const string NoBreakAfter = "（〈《「『【〔｛([{$";

    /// <summary>
    /// 把文字加進段落，並在合法的中文斷行點切成多個 <see cref="Text"/> 元素。
    /// 這是呼叫端唯一該用的入口。
    /// </summary>
    public static void AddTo(ParagraphElements elements, string? text)
    {
        ArgumentNullException.ThrowIfNull(elements);

        foreach (var chunk in Split(text))
        {
            elements.AddText(chunk);
        }
    }

    /// <summary>
    /// 依中文斷行規則把文字切段；相鄰兩段之間就是一個斷行機會。
    ///
    /// ⚠️ 切出來的所有段落接回去<b>必須與原文一字不差</b>（由測試守門）——
    /// 這個不變量就是「不新增任何字元」的保證。
    /// </summary>
    internal static List<string> Split(string? text)
    {
        var chunks = new List<string>();

        if (string.IsNullOrEmpty(text))
        {
            return chunks;
        }

        var start = 0;

        for (var index = 0; index < text.Length - 1; index++)
        {
            if (AllowsBreakBetween(text[index], text[index + 1]) == false)
            {
                continue;
            }

            chunks.Add(text[start..(index + 1)]);
            start = index + 1;
        }

        chunks.Add(text[start..]);
        return chunks;
    }

    private static bool AllowsBreakBetween(char previous, char next)
    {
        // ASCII 空白本來就是 MigraDoc 認得的斷行機會，不必也不該再切一刀。
        if (IsAsciiWhiteSpace(previous) || IsAsciiWhiteSpace(next))
        {
            return false;
        }

        // ⚠️ 不斷行空格是刻意用來把「標籤」與「數字」黏在一起的。
        // 在它兩側製造斷行機會，等於親手把那個設計破壞掉。
        if (previous == NoBreakSpace || next == NoBreakSpace)
        {
            return false;
        }

        if (NoBreakAfter.Contains(previous) || NoBreakBefore.Contains(next))
        {
            return false;
        }

        // 兩邊都是西文時交回 MigraDoc 原本的空格斷行，不要插手 ——
        // 在英文單字中間切一刀會把單字攔腰折斷。
        return IsCjk(previous) || IsCjk(next);
    }

    private static bool IsAsciiWhiteSpace(char value)
        => value is ' ' or '\t' or '\r' or '\n';

    /// <summary>
    /// 中日韓字元。範圍刻意涵蓋全形空格與全形標點 —— 它們同樣不是 MigraDoc 的斷行機會。
    /// </summary>
    private static bool IsCjk(char value)
        => value is (>= '\u2E80' and <= '\u9FFF')   // 部首補充 ～ CJK 統一漢字（含全形標點 U+3000–U+303F）
            or (>= '\uF900' and <= '\uFAFF')        // CJK 相容漢字
            or (>= '\uFF01' and <= '\uFF60');       // 全形 ASCII 對應
}
