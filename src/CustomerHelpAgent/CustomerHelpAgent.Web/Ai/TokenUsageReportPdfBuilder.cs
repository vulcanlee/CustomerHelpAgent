using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 報表要輸出哪些區塊。<see cref="All"/> 之外的每一個值都對應畫面上的一個頁籤 ——
/// 「匯出目前頁籤」就是把目前選中的頁籤鍵轉成這裡的一個值。
///
/// ⚠️ 標題、條件區與合計<b>不受本列舉影響，一律輸出</b>：
/// 一份看不出是在什麼條件下產生的統計沒有意義。
/// </summary>
public enum TokenUsageReportScope
{
    All,
    Trend,
    Daily,
    Account,
    Operation,
    Model,
    CallKind,
    Detail,
}

/// <summary>Token 用量 PDF 報表的輸入。全部由 Token 用量頁在按下匯出時組好。</summary>
public sealed record TokenUsageReportRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }

    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string Account { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string CallKind { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    public TokenUsageSummary Summary { get; init; } = new();
    public IReadOnlyList<TokenUsageGroupRow> ByAccount { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByOperation { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByModel { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByCallKind { get; init; } = [];
    public IReadOnlyList<TokenUsageLogAdapterModel> Details { get; init; } = [];

    /// <summary>
    /// 每日趨勢。<b>直接來自畫面（已補零、已套上限），PDF 不重新查資料庫</b> ——
    /// 這樣報表的格數與日期範圍與畫面上看到的必然一致。
    /// </summary>
    public IReadOnlyList<TokenUsageDailyRow> Daily { get; init; } = [];

    /// <summary>要輸出哪些區塊。預設整份。</summary>
    public TokenUsageReportScope Scope { get; init; } = TokenUsageReportScope.All;
}

/// <summary>
/// 把 Token 用量統計排成 PDF 報表。
///
/// ⚠️ 與 <see cref="AiReportPdfBuilder"/> 共用同一套內嵌中文字型機制
/// （<see cref="EmbeddedFontResolver"/>）。內嵌字型只有 Regular 一個字重
/// （PDFsharp 沒有粗體模擬），所以一律不設 <c>Font.Bold</c>，層級改用字級與顏色表達。
/// </summary>
public static class TokenUsageReportPdfBuilder
{
    /// <summary>明細最多列印的筆數。PDF 是拿來看趨勢的，逐筆對帳請改用 CSV。</summary>
    private const int MaxDetailRows = 200;

    /// <summary>每日趨勢長條的分段數。MigraDoc 沒有巢狀表格，長條只能由固定格數的網底拼出來。</summary>
    private const int BarSegments = 30;

    /// <summary>長條最後一格的欄索引（第 0 欄是日期）。</summary>
    private const int BarColumnEnd = BarSegments;

    private static readonly Color TextColor = Color.FromRgb(0x33, 0x41, 0x55);
    private static readonly Color HeadingColor = Color.FromRgb(0x1E, 0x29, 0x3B);
    private static readonly Color MutedColor = Color.FromRgb(0x64, 0x74, 0x8B);
    private static readonly Color BorderColor = Color.FromRgb(0xE2, 0xE8, 0xF0);
    private static readonly Color ShadingColor = Color.FromRgb(0xF1, 0xF5, 0xF9);

    /// <summary>每日趨勢長條的填色。報表自帶一組色，不與網頁端的 theme.css 色票共用。</summary>
    private static readonly Color BarColor = Color.FromRgb(0x94, 0xA3, 0xB8);

    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(TokenUsageReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        EmbeddedFontResolver.EnsureRegistered();
        if (EmbeddedFontResolver.IsFontAvailable() == false)
        {
            throw new InvalidOperationException("Embedded PDF font resource is missing.");
        }

        var document = CreateDocument(request);
        var section = document.LastSection;

        // 標題、條件區與合計一律輸出，不受 Scope 影響 —— 見 TokenUsageReportScope 的說明。
        AddTitle(section, request);
        AddMetadata(section, request);
        AddSummary(section, request.Summary);

        // 區塊順序與畫面的頁籤順序一致：趨勢圖、每日趨勢、四張分組表、明細。
        if (Include(TokenUsageReportScope.Trend))
        {
            AddTrendChart(section, request.Daily);
        }

        if (Include(TokenUsageReportScope.Daily))
        {
            AddDailyTable(section, request.Daily);
        }

        if (Include(TokenUsageReportScope.Account))
        {
            AddGroupTable(section, "依使用者", "使用者", request.ByAccount);
        }

        if (Include(TokenUsageReportScope.Operation))
        {
            AddGroupTable(section, "依作業", "作業", request.ByOperation);
        }

        if (Include(TokenUsageReportScope.Model))
        {
            AddGroupTable(section, "依模型", "模型", request.ByModel);
        }

        if (Include(TokenUsageReportScope.CallKind))
        {
            AddGroupTable(section, "依型別", "型別", request.ByCallKind);
        }

        if (Include(TokenUsageReportScope.Detail))
        {
            AddDetailTable(section, request.Details);
        }

        bool Include(TokenUsageReportScope one)
            => request.Scope is TokenUsageReportScope.All || request.Scope == one;

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static Document CreateDocument(TokenUsageReportRequest request)
    {
        var document = new Document();
        document.Info.Title = "Token 用量報表";
        document.Info.Author = string.IsNullOrWhiteSpace(request.SystemName) ? "CustomerHelpAgent" : request.SystemName;

        // ⚠️ 字型一定要設在 Normal 樣式上，其他樣式才會繼承到內嵌的中文字型。
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = Unit.FromPoint(10);
        normal.Font.Color = TextColor;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.25;

        var heading = document.Styles[StyleNames.Heading2]!;
        heading.Font.Size = Unit.FromPoint(13);
        heading.Font.Color = HeadingColor;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);

        return document;
    }

    /// <summary>
    /// 加一段自由文字，並在中文的合法斷行點切成多個元素。
    /// 中文沒有空格，不切就會排成一條衝出頁面的長線（見 <see cref="CjkLineBreak"/>）。
    /// </summary>
    private static Paragraph AddBreakableParagraph(Section section, string text)
    {
        var paragraph = section.AddParagraph();
        CjkLineBreak.AddTo(paragraph.Elements, text);
        return paragraph;
    }

    /// <summary>單頁籤報表的標題後綴。拿在手上要看得出這份只涵蓋哪一個頁籤。</summary>
    internal static string DescribeScope(TokenUsageReportScope scope) => scope switch
    {
        TokenUsageReportScope.Trend => "趨勢圖",
        TokenUsageReportScope.Daily => "每日趨勢",
        TokenUsageReportScope.Account => "依使用者",
        TokenUsageReportScope.Operation => "依作業",
        TokenUsageReportScope.Model => "依模型",
        TokenUsageReportScope.CallKind => "依型別",
        TokenUsageReportScope.Detail => "明細",
        _ => "全部頁籤",
    };

    private static void AddTitle(Section section, TokenUsageReportRequest request)
    {
        var title = section.AddParagraph(request.Scope is TokenUsageReportScope.All
            ? "Token 用量報表"
            : $"Token 用量報表 — {DescribeScope(request.Scope)}");
        title.Format.Font.Size = Unit.FromPoint(18);
        title.Format.Font.Color = HeadingColor;
        title.Format.SpaceAfter = Unit.FromPoint(4);

        var subtitle = AddBreakableParagraph(section,
            $"{request.SystemName}　版本 {request.SystemVersion}");
        subtitle.Format.Font.Size = Unit.FromPoint(9.5);
        subtitle.Format.Font.Color = MutedColor;
        subtitle.Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void AddMetadata(Section section, TokenUsageReportRequest request)
    {
        var lines = new List<string>
        {
            $"產生時間：{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}",
            $"操作者：{request.OperatorAccount}",
            $"查詢區間：{FormatDate(request.StartDate)} ～ {FormatDate(request.EndDate)}",
        };

        var filters = new List<string>();
        AddFilter(filters, "帳號", request.Account);
        AddFilter(filters, "作業", request.Operation);
        AddFilter(filters, "型別", request.CallKind);
        AddFilter(filters, "模型", request.Model);
        lines.Add($"篩選條件：{(filters.Count == 0 ? "（不限）" : string.Join("、", filters))}");

        foreach (var line in lines)
        {
            // 「篩選條件」會帶使用者輸入的長作業名／模型名，同樣需要斷行機會。
            var paragraph = AddBreakableParagraph(section, line);
            paragraph.Format.Font.Size = Unit.FromPoint(9.5);
            paragraph.Format.Font.Color = MutedColor;
            paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        }

        static void AddFilter(List<string> target, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value) == false)
            {
                target.Add($"{label} {value}");
            }
        }

        static string FormatDate(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? "（不限）";
    }

    private static void AddSummary(Section section, TokenUsageSummary summary)
    {
        section.AddParagraph("合計", StyleNames.Heading2);

        // ⚠️ 標籤與數字之間用不斷行空格（U+00A0）而不是一般空格：MigraDoc 只把 ASCII ' '
        // 當斷行機會，用一般空格會讓「輸入(送出)」留在行尾、「17.87K」被推到下一行。
        // 群組之間的全形空格則由 CjkLineBreak 補上斷行機會。
        // 標籤與數字之間夾不斷行空格，讓兩者不分家；群組之間的全形空格則由
        // CjkLineBreak 取得斷行機會。常數走 CjkLineBreak，不在這裡寫字面字元。
        var glue = CjkLineBreak.NoBreakSpace;
        var paragraph = AddBreakableParagraph(section,
            $"輸入(送出){glue}{TokenUsageFormat.Compact(summary.InputCount)}　"
            + $"輸出(接收){glue}{TokenUsageFormat.Compact(summary.OutputCount)}　"
            + $"其中快取{glue}{TokenUsageFormat.Compact(summary.CachedInputCount)}　"
            + $"其中推理{glue}{TokenUsageFormat.Compact(summary.ReasoningCount)}　"
            + $"合計{glue}{TokenUsageFormat.Compact(summary.TotalCount)}　"
            + $"呼叫次數{glue}{summary.CallCount:N0}　"
            + $"花費{glue}NT${glue}{TokenUsageFormat.CostTwdTotal(summary.CostTwd)}"
            + $"（US${glue}{TokenUsageFormat.CostUsdTotal(summary.CostUsd)}）");
        paragraph.Format.Font.Size = Unit.FromPoint(11);

        // ⚠️ 這一段是 97 個中文字、零個空格。不補斷行機會的話它會排成 30.79cm 寬，
        // 而 A4 橫式可用寬只有 26.5cm、整張紙也才 29.7cm —— 最後幾個字會直接掉出紙外。
        var note = AddBreakableParagraph(section,
            "註：快取是輸入的折扣子集、推理計入輸出，兩者皆不另計入合計。"
            + "費用為呼叫當下以設定單價與匯率算好的快照，事後調整設定不影響已記錄的帳；"
            + "美金與台幣各自為逐列加總，區間橫跨匯率調整時兩者彼此推不出來。");
        note.Format.Font.Size = Unit.FromPoint(9);
        note.Format.Font.Color = MutedColor;

        if (summary.UnpricedCount > 0)
        {
            var unpriced = AddBreakableParagraph(section,
                $"　其中 {summary.UnpricedCount:N0} 筆未定價（找不到該模型的費率設定）未計入上列金額。");
            unpriced.Format.Font.Size = Unit.FromPoint(9);
            unpriced.Format.Font.Color = MutedColor;
        }
    }

    /// <summary>
    /// 每日趨勢：表格加上費用的網底長條。
    ///
    /// ⚠️ 長條<b>不能</b>用「巢狀表格＋依比例算欄寬」來做 —— MigraDoc 不支援巢狀表格。
    /// 這裡改用固定分段：<see cref="BarSegments"/> 個等寬窄欄，依比例把前 k 格上網底。
    /// 解析度是 1/30，對報表夠用，而且不必冒險。
    ///
    /// ⚠️ 只畫費用一條。token 合計仍以數字欄位呈現 —— 兩者單位不同（錢 vs token），
    /// 共用一個尺規只會誤導。
    /// </summary>
    private static void AddDailyTable(Section section, IReadOnlyList<TokenUsageDailyRow> rows)
    {
        section.AddParagraph("每日趨勢", StyleNames.Heading2);

        if (rows.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        // 欄寬：2.6 + 30×0.4 + 2.2 + 2.4 + 2.6 + 2.4 = 24.2 cm，A4 橫式可用寬約 26.5 cm。
        var table = CreateTable(section);
        table.AddColumn(Unit.FromCentimeter(2.6));
        for (var index = 0; index < BarSegments; index++)
        {
            table.AddColumn(Unit.FromCentimeter(0.4));
        }

        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(2.4));
        table.AddColumn(Unit.FromCentimeter(2.6));
        table.AddColumn(Unit.FromCentimeter(2.4));

        var header = table.AddRow();
        header.Shading.Color = ShadingColor;
        header.HeadingFormat = true;
        SetCell(header, 0, "日期", left: true);
        SetCell(header, 1, "費用趨勢（相對於區間內最高的一天）", left: true);
        // 長條那 30 格在表頭合併成一格，否則標題會被切成 30 段。
        header.Cells[1].MergeRight = BarSegments - 1;
        SetCell(header, BarColumnEnd + 1, "次數");
        SetCell(header, BarColumnEnd + 2, "合計");
        SetCell(header, BarColumnEnd + 3, "費用(TWD)");
        SetCell(header, BarColumnEnd + 4, "費用(USD)");

        for (var index = 0; index <= BarColumnEnd + 4; index++)
        {
            header.Cells[index].Format.Font.Color = HeadingColor;
        }

        // ⚠️ max 可能是 0（整段期間都沒花錢，或全部未定價），一律先擋掉再除。
        var max = rows.Max(x => x.CostTwd);

        foreach (var row in rows)
        {
            var cells = table.AddRow();
            SetCell(cells, 0, row.Date.ToString("MM/dd (ddd)"), left: true);

            var filled = max <= 0 ? 0 : (int)Math.Ceiling(row.CostTwd / max * BarSegments);
            for (var index = 0; index < BarSegments; index++)
            {
                if (index < filled)
                {
                    cells.Cells[index + 1].Shading.Color = BarColor;
                }
            }

            SetCell(cells, BarColumnEnd + 1, row.CallCount.ToString("N0"));
            SetCell(cells, BarColumnEnd + 2, TokenUsageFormat.Compact(row.TotalCount));
            SetCell(cells, BarColumnEnd + 3, TokenUsageFormat.CostTwdTotal(row.CostTwd));
            SetCell(cells, BarColumnEnd + 4, TokenUsageFormat.CostUsdTotal(row.CostUsd));
        }

        var unpriced = rows.Sum(x => x.UnpricedCount);
        if (unpriced > 0)
        {
            var note = AddBreakableParagraph(section,
                $"　其中 {unpriced:N0} 筆未定價未計入金額，因此那些日子的長條會短於實際用量。");
            note.Format.Font.Size = Unit.FromPoint(9);
            note.Format.Font.Color = MutedColor;
        }
    }

    /// <summary>
    /// 趨勢圖：上下兩張折線圖（費用、合計 token），點與刻度和畫面共用 <see cref="TokenUsageTrendChart"/>。
    ///
    /// ⚠️ MigraDoc 沒有圖表，也沒辦法在排版流程中直接拿 XGraphics 畫圖。
    /// 這裡先用 PDFsharp 把圖畫進一份單頁的 PDF，再以 <c>base64:</c> 圖片來源嵌回報表 ——
    /// 嵌進去的是向量（Form XObject），放大不會糊，也不必落地暫存檔。
    /// </summary>
    private static void AddTrendChart(Section section, IReadOnlyList<TokenUsageDailyRow> rows)
    {
        // 圖很高，放不下時會整張換頁；標題與說明要跟著圖走，不能孤零零留在上一頁底部。
        section.AddParagraph("趨勢圖", StyleNames.Heading2).Format.KeepWithNext = true;

        var points = TokenUsageTrendChart.Bucket(rows);
        if (points.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        var caption = AddBreakableParagraph(section,
            $"{TokenUsageTrendChart.DescribeGrain(TokenUsageTrendChart.GrainFor(rows.Count))}，共 {points.Count} 點。"
            + "上圖為費用（NT$），下圖為合計 token，兩者各自一個 Y 軸。");
        caption.Format.Font.Size = Unit.FromPoint(9);
        caption.Format.Font.Color = MutedColor;
        caption.Format.KeepWithNext = true;

        var image = section.AddImage("base64:" + Convert.ToBase64String(RenderTrendChart(points)));
        image.Width = Unit.FromCentimeter(TrendImageWidthCm);
        image.LockAspectRatio = true;

        if (points.Any(x => x.UnpricedCount > 0))
        {
            var note = AddBreakableParagraph(section,
                "　區間內有未定價的呼叫，未計入費用，因此那些點的費用會低於實際用量。");
            note.Format.Font.Size = Unit.FromPoint(9);
            note.Format.Font.Color = MutedColor;
        }
    }

    private const double TrendImageWidthCm = 25;
    private const double TrendChartHeightCm = 5;

    /// <summary>把兩張折線圖畫成一份單頁 PDF 的位元組。</summary>
    internal static byte[] RenderTrendChart(IReadOnlyList<TokenUsageTrendPoint> points)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromCentimeter(TrendImageWidthCm);
        page.Height = XUnit.FromCentimeter(TrendChartHeightCm * 2 + 0.6);

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var chartHeight = XUnit.FromCentimeter(TrendChartHeightCm).Point;
            var width = page.Width.Point;
            var gap = XUnit.FromCentimeter(0.6).Point;

            DrawTrendLine(gfx, points, "費用（NT$）", x => x.CostTwd, TokenUsageFormat.CostTwdTotal,
                TrendCostColor, 0, width, chartHeight);
            DrawTrendLine(gfx, points, "合計 token", x => x.TotalCount, v => TokenUsageFormat.Compact((long)v),
                TrendCountColor, chartHeight + gap, width, chartHeight);
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static readonly XColor TrendCostColor = XColor.FromArgb(0x33, 0x41, 0x55);
    private static readonly XColor TrendCountColor = XColor.FromArgb(0x94, 0xA3, 0xB8);

    private static void DrawTrendLine(
        XGraphics gfx,
        IReadOnlyList<TokenUsageTrendPoint> points,
        string title,
        Func<TokenUsageTrendPoint, double> value,
        Func<double, string> formatTick,
        XColor lineColor,
        double top,
        double width,
        double height)
    {
        var font = new XFont(EmbeddedFontResolver.FamilyName, 7.5);
        var titleFont = new XFont(EmbeddedFontResolver.FamilyName, 9);
        var muted = new XSolidBrush(XColor.FromArgb(0x64, 0x74, 0x8B));
        var gridPen = new XPen(XColor.FromArgb(0xE2, 0xE8, 0xF0), 0.5);

        // 版面：上方 22pt 標題（要讓出最上面那條刻度的字高）、下方 14pt 日期、左邊 52pt 刻度，
        // 右邊留 20pt —— 最後一個日期標籤置中在最後一點上，不留白會被切掉半個字。
        const double left = 52;
        var plotTop = top + 22;
        var plotHeight = height - 22 - 14;
        var plotWidth = width - left - 20;

        gfx.DrawString(title, titleFont, new XSolidBrush(XColor.FromArgb(0x1E, 0x29, 0x3B)),
            new XPoint(0, top + 10));

        var values = points.Select(value).ToList();
        var axis = TokenUsageTrendChart.Axis(values.Max());

        foreach (var tick in axis.Ticks())
        {
            var y = TokenUsageTrendChart.Y(tick, axis.Max, plotTop, plotHeight);
            gfx.DrawLine(gridPen, left, y, left + plotWidth, y);
            gfx.DrawString(formatTick(tick), font, muted,
                new XRect(0, y - 5, left - 4, 10), XStringFormats.CenterRight);
        }

        var labelStep = TokenUsageTrendChart.LabelStep(points.Count);
        for (var index = 0; index < points.Count; index += labelStep)
        {
            var x = TokenUsageTrendChart.X(index, points.Count, left, plotWidth);
            gfx.DrawString(TokenUsageTrendChart.AxisLabel(points[index]), font, muted,
                new XRect(x - 20, plotTop + plotHeight + 2, 40, 10), XStringFormats.TopCenter);
        }

        var coordinates = values
            .Select((v, index) => new XPoint(
                TokenUsageTrendChart.X(index, values.Count, left, plotWidth),
                TokenUsageTrendChart.Y(v, axis.Max, plotTop, plotHeight)))
            .ToArray();

        if (coordinates.Length > 1)
        {
            gfx.DrawLines(new XPen(lineColor, 1.4) { LineJoin = XLineJoin.Round }, coordinates);
        }

        // 點太多時（按週也可能二十幾點）仍然畫點：少了點，零值的日子會和線段中段分不出來。
        var brush = new XSolidBrush(lineColor);
        foreach (var point in coordinates)
        {
            gfx.DrawEllipse(brush, point.X - 1.6, point.Y - 1.6, 3.2, 3.2);
        }
    }

    private static void AddGroupTable(
        Section section, string heading, string keyTitle, IReadOnlyList<TokenUsageGroupRow> rows)
    {
        section.AddParagraph(heading, StyleNames.Heading2);

        if (rows.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        // 欄寬：7 + 7×2.6 = 25.2 cm，A4 橫式可用寬約 26.5 cm（29.7 減左右各 1.6）。
        var table = CreateTable(section);
        table.AddColumn(Unit.FromCentimeter(7));
        for (var index = 0; index < 7; index++)
        {
            table.AddColumn(Unit.FromCentimeter(2.6));
        }

        AddHeaderRow(table, [keyTitle, "輸入", "輸出", "快取", "推理", "合計", "費用(TWD)", "次數"]);

        foreach (var row in rows)
        {
            var cells = table.AddRow();
            SetCell(cells, 0, row.Key, left: true);
            SetCell(cells, 1, TokenUsageFormat.Compact(row.InputCount));
            SetCell(cells, 2, TokenUsageFormat.Compact(row.OutputCount));
            SetCell(cells, 3, TokenUsageFormat.Compact(row.CachedInputCount));
            SetCell(cells, 4, TokenUsageFormat.Compact(row.ReasoningCount));
            SetCell(cells, 5, TokenUsageFormat.Compact(row.TotalCount));
            SetCell(cells, 6, TokenUsageFormat.CostTwdTotal(row.CostTwd));
            SetCell(cells, 7, row.CallCount.ToString("N0"));
        }
    }

    private static void AddDetailTable(Section section, IReadOnlyList<TokenUsageLogAdapterModel> details)
    {
        section.AddParagraph("明細", StyleNames.Heading2);

        if (details.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        var table = CreateTable(section);
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(2.4));
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(3.6));
        for (var index = 0; index < 4; index++)
        {
            table.AddColumn(Unit.FromCentimeter(2.0));
        }

        // 費用欄。欄寬合計 3.4+2.4+3.4+2.2+3.6+4×2.0+2.2 = 25.2 cm，A4 橫式放得下。
        table.AddColumn(Unit.FromCentimeter(2.2));

        AddHeaderRow(table, ["時間", "使用者", "作業", "型別", "模型", "輸入", "輸出", "合計", "費用(TWD)", "結果"]);

        foreach (var item in details.Take(MaxDetailRows))
        {
            var cells = table.AddRow();
            SetCell(cells, 0, item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss"), left: true);
            SetCell(cells, 1, item.Account ?? "（系統自動）", left: true);
            SetCell(cells, 2, item.Operation, left: true);
            SetCell(cells, 3, item.CallKind, left: true);
            SetCell(cells, 4, item.Model, left: true);
            SetCell(cells, 5, TokenUsageFormat.Cell(item.InputCount));
            SetCell(cells, 6, TokenUsageFormat.Cell(item.OutputCount));
            SetCell(cells, 7, item.IsDurationBilled
                ? $"時長 {item.DurationSeconds:N0} 秒"
                : TokenUsageFormat.Cell(item.TotalCount));
            SetCell(cells, 8, item.IsUnpriced ? "未定價" : TokenUsageFormat.CostTwdCell(item.CostTwd));
            SetCell(cells, 9, item.Success ? "成功" : item.FailureReason ?? "失敗");
        }

        if (details.Count > MaxDetailRows)
        {
            var note = AddBreakableParagraph(section,
                $"明細僅列出前 {MaxDetailRows} 筆（共 {details.Count:N0} 筆）。完整資料請改用 CSV 匯出。");
            note.Format.Font.Size = Unit.FromPoint(9);
            note.Format.Font.Color = MutedColor;
        }
    }

    private static Table CreateTable(Section section)
    {
        var table = section.AddTable();
        table.Borders.Color = BorderColor;
        table.Borders.Width = 0.5;
        table.Format.Font.Size = Unit.FromPoint(8.5);
        table.Format.SpaceAfter = 0;
        table.TopPadding = Unit.FromPoint(2);
        table.BottomPadding = Unit.FromPoint(2);
        table.LeftPadding = Unit.FromPoint(3);
        table.RightPadding = Unit.FromPoint(3);
        return table;
    }

    private static void AddHeaderRow(Table table, string[] titles)
    {
        var header = table.AddRow();
        header.Shading.Color = ShadingColor;
        header.HeadingFormat = true;

        for (var index = 0; index < titles.Length; index++)
        {
            SetCell(header, index, titles[index], left: index == 0);
            header.Cells[index].Format.Font.Color = HeadingColor;
        }
    }

    /// <summary>
    /// 所有表格儲存格的<b>單一收斂點</b>，因此中文斷行機會統一在這裡補。
    /// 長作業名／模型名／「（系統自動）」在窄欄裡沒有斷行機會就會溢出欄寬。
    /// </summary>
    private static void SetCell(Row row, int index, string text, bool left = false)
    {
        var cell = row.Cells[index];
        CjkLineBreak.AddTo(cell.AddParagraph().Elements, text);
        cell.Format.Alignment = left ? ParagraphAlignment.Left : ParagraphAlignment.Right;
        cell.VerticalAlignment = VerticalAlignment.Center;
    }
}
