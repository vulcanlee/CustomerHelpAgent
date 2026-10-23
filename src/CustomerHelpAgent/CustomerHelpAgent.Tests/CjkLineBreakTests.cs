using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 中文斷行規則。
///
/// ⚠️ 背景是一個 MigraDoc 的硬限制：它的斷行機會是一組寫死的字元（空白、減號、
/// 軟連字號、U+200B、U+200C），中文全部不在其中，所以一整段沒有空格的中文會變成
/// 單一個不可分割的「字」並<b>溢出頁面</b>。做法是把文字切成多個 Text 元素，
/// 相鄰兩個之間就能斷行。詳見 <see cref="CjkLineBreak"/>。
///
/// 規則是純函式，這裡把每一條都釘死；PDF 真的有換行由兩支產生器的頁數測試驗證。
/// </summary>
public sealed class CjkLineBreakTests
{
    /// <summary>
    /// ⭐⭐ <b>最重要的一條</b>：切出來的段落接回去必須與原文一字不差。
    ///
    /// 這條不變量就是「不新增任何字元」的保證。第一版改法是插入零寬空格 U+200B，
    /// 看起來更簡單，但內嵌的 Noto Sans TC <b>沒有該字圖</b>（對到 glyph 0 / .notdef，
    /// 寬度整整 1 個 em），實測 13 字的句子從 130pt 膨脹到 250pt ——
    /// 等於每兩字之間插一個看得見的方框。有這條測試就不會再走回那條路。
    /// </summary>
    [Theory]
    [InlineData("註：快取是輸入的折扣子集、推理計入輸出，兩者皆不另計入合計。")]
    [InlineData("Token 用量報表　版本 0.9.52")]
    [InlineData("mixed 中英 text 混排 123")]
    [InlineData("（系統自動）")]
    [InlineData("plain ascii only")]
    public void Split_ShouldNotChangeTheText(string text)
    {
        Assert.Equal(text, string.Concat(CjkLineBreak.Split(text)));
    }

    [Fact]
    public void Split_ShouldBreakBetweenHanCharacters()
    {
        Assert.Equal(["快", "取"], CjkLineBreak.Split("快取"));
    }

    /// <summary>⭐ 行首禁則：句讀不可以被推到下一行的開頭。</summary>
    [Theory]
    [InlineData("計。")]
    [InlineData("計，")]
    [InlineData("計、")]
    [InlineData("計）")]
    [InlineData("計」")]
    [InlineData("計；")]
    public void Split_ShouldNotBreakBeforeClosingPunctuation(string text)
    {
        Assert.Equal([text], CjkLineBreak.Split(text));
    }

    /// <summary>⭐ 行尾禁則：開括號不可以孤零零留在行尾。</summary>
    [Theory]
    [InlineData("（計")]
    [InlineData("「計")]
    [InlineData("【計")]
    public void Split_ShouldNotBreakAfterOpeningPunctuation(string text)
    {
        Assert.Equal([text], CjkLineBreak.Split(text));
    }

    [Fact]
    public void Split_ShouldNotBreakAtAsciiWhitespace()
    {
        // ASCII 空白本來就是 MigraDoc 的斷行機會，不必也不該再切一刀。
        Assert.Equal(["費 用"], CjkLineBreak.Split("費 用"));
    }

    /// <summary>⭐ 不斷行空格是用來把標籤與數字黏在一起的，不可以在它兩側切開。</summary>
    [Fact]
    public void Split_ShouldNotBreakAroundNoBreakSpace()
    {
        var text = $"入{CjkLineBreak.NoBreakSpace}1";

        Assert.Equal([text], CjkLineBreak.Split(text));
    }

    /// <summary>全形空格 MigraDoc 不認，所以它後面要切得開，合計行的群組才斷得了。</summary>
    [Fact]
    public void Split_ShouldBreakAfterIdeographicSpace()
    {
        Assert.Equal(["計\u3000", "輸"], CjkLineBreak.Split("計\u3000輸"));
    }

    [Fact]
    public void Split_ShouldLeaveAsciiWordsIntact()
    {
        // 在英文單字中間切一刀會把單字攔腰折斷。
        Assert.Equal(["Token usage report"], CjkLineBreak.Split("Token usage report"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Split_ShouldReturnNothingForEmptyInput(string? text)
    {
        Assert.Empty(CjkLineBreak.Split(text));
    }

    [Fact]
    public void Split_ShouldReturnSingleChunkForOneCharacter()
    {
        Assert.Equal(["字"], CjkLineBreak.Split("字"));
    }

    /// <summary>沒有任何空格的長中文必須真的被切開，否則 MigraDoc 依舊無處可斷。</summary>
    [Fact]
    public void Split_ShouldGiveLongSpacelessChineseManyChunks()
    {
        var chunks = CjkLineBreak.Split(new string('字', 100));

        Assert.Equal(100, chunks.Count);
    }
}
