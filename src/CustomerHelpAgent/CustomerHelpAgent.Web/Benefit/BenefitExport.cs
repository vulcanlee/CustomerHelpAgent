using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Benefit;

/// <summary>
/// 效益對照匯出的輸入（M6）：就是畫面當下看到的東西 —— 目前範圍的 <see cref="Comparison"/>、
/// 畫面上的假設值（可能是尚未儲存的草稿，<see cref="AssumptionUnsaved"/>）與目前選的月度推估。
/// 不含任何顧客個資：明細只有示範帳號代號。
/// </summary>
public sealed record BenefitExportRequest(
    BenefitComparison Comparison,
    DemoAssumptionInput Assumption,
    bool AssumptionUnsaved,
    string? EstimateFamily,
    ScenarioFlow EstimateFlow,
    MonthlyEstimate? Estimate,
    string SystemName,
    string SystemVersion,
    string OperatorAccount,
    DateTime GeneratedAt);

/// <summary>報告中的一張表。<see cref="Kind"/> 為「實測」「假設」或「推估（依假設值）」，檔案裡一律寫出來。</summary>
public sealed record BenefitReportTable(string Heading, string Kind, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note = null)
{
    public bool IsAssumed => Kind != BenefitExport.KindMeasured;
}

/// <summary>Excel 與 PDF 共用的報告內容（先組成文字再各自排版，兩種檔案說同一件事，測試也能直接斷言）。</summary>
public sealed record BenefitReport(
    string Title,
    IReadOnlyList<KeyValuePair<string, string>> Notes,
    string Disclaimer,
    IReadOnlyList<BenefitReportTable> Measured,
    IReadOnlyList<BenefitReportTable> Assumed);

/// <summary>
/// 效益對照的 Excel／PDF 匯出（M6）。Excel 三張工作表：對照摘要、明細、假設值與推估；PDF 是對照摘要加假設值與推估。
/// 兩種檔案都在檔頭寫明資料範圍、回放與無效不計、AI 成本口徑與「假設值／推估僅供參考」，
/// 並以「實測」「假設」「推估」標示每張表（假設與推估另用琥珀色底）。
/// </summary>
public static class BenefitExport
{
    public const string KindMeasured = "實測";
    public const string KindAssumed = "假設";
    public const string KindEstimated = "推估（依假設值）";

    public const string SummarySheet = "對照摘要";
    public const string DetailSheet = "明細";
    public const string AssumptionSheet = "假設值與推估";

    public const string ReportTitle = "效益對照報告";
    public const string Disclaimer = "假設值／推估僅供參考，不是實際發生的數字。";

    private const string NoData = "尚無資料";
    private const string Dash = "—";

    private static readonly XLColor AssumedFill = XLColor.FromHtml("#FFF3CD");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F1F5F9");

    #region 檔名

    /// <summary>檔名含日期範圍（起始日與結束日，皆為整天）：<c>CustomerHelpAgent.Web-benefit-20261024-20261024.xlsx</c>。</summary>
    public static string FileName(BenefitComparison comparison, string extension)
    {
        var end = comparison.To.AddTicks(-1).Date;
        if (end < comparison.From.Date)
        {
            end = comparison.From.Date;
        }

        return $"CustomerHelpAgent.Web-benefit-{comparison.From:yyyyMMdd}-{end:yyyyMMdd}.{extension}";
    }

    #endregion

    #region 報告內容

    public static BenefitReport BuildReport(BenefitExportRequest request)
    {
        var comparison = request.Comparison;
        IReadOnlyList<KeyValuePair<string, string>> notes =
        [
            new("資料範圍", $"{comparison.From:yyyy/MM/dd HH:mm} ～ {comparison.To:yyyy/MM/dd HH:mm}（不含結束時間）"),
            new("計入原則", "回放與無效執行不計（原因見明細）"),
            new("AI 成本口徑", "AI 成本＝每案經 AI 對話紀錄關聯的 Token 用量成本；客服 AI 助手成本未歸入每案"),
            new("標示", "「實測」來自系統紀錄；「假設」是人工設定的假設值；「推估」是依假設值算出的月度數字"),
        ];

        var measured = new List<BenefitReportTable>();
        measured.AddRange(BenefitFamilyLayout.BuildSections(comparison).Select(FamilyTable));
        measured.Add(OthersTable(comparison));
        measured.Add(CostTable(comparison.Cost));

        return new BenefitReport(
            ReportTitle,
            notes,
            Disclaimer,
            measured,
            [AssumptionTable(request), EstimateTable(request)]);
    }

    private static BenefitReportTable FamilyTable(FamilySection section)
    {
        var flows = section.Slots.Select(x => x.Flow).ToList();
        var traditional = section.Traditional;
        var rows = new List<IReadOnlyList<string>>();

        var sampleRow = new List<string> { "有效次數" };
        sampleRow.AddRange(section.Slots.Select(slot => slot.Metrics is { } metrics ? BenefitFamilyLayout.SampleText(metrics) : BenefitFamilyLayout.MissingHint(slot)));
        rows.Add(sampleRow);

        foreach (var metric in BenefitMetricDisplay.Metrics)
        {
            var row = new List<string> { metric.Label };
            var baseline = BenefitFamilyLayout.Average(metric, traditional);
            row.AddRange(section.Slots.Select(slot => CellText(metric, slot.Metrics, slot.Flow == ScenarioFlow.Traditional ? null : baseline)));
            rows.Add(row);
        }

        if (section.Slots.Any(x => x.Metrics is { UnpricedCallCount: > 0 }))
        {
            var unpriced = new List<string> { "未定價的 AI 呼叫（AI 成本偏低）" };
            unpriced.AddRange(section.Slots.Select(slot => slot.Metrics is { UnpricedCallCount: > 0 } m ? $"{m.UnpricedCallCount} 次" : Dash));
            rows.Add(unpriced);
        }

        return new BenefitReportTable(
            section.Family,
            KindMeasured,
            ["指標", .. flows.Select(BenefitMetricDisplay.FlowLabel)],
            rows,
            "單一 Agent、多 Agent 欄括號內為相對傳統表單的差異。");
    }

    private static string CellText(BenefitMetric metric, FlowMetrics? metrics, double? baseline)
    {
        if (metrics is null)
        {
            return NoData;
        }

        var average = BenefitFamilyLayout.Average(metric, metrics);
        if (average is null)
        {
            return Dash;
        }

        var text = metric.Format(average);
        if (baseline is not null && BenefitMetricDisplay.Delta(baseline, average, metric.Direction) is { } delta)
        {
            text += $"（{delta.Text}）";
        }

        if (BenefitFamilyLayout.PartialSampleCount(metric, metrics) is { } samples)
        {
            text += $"［{samples} 次有值］";
        }

        return text;
    }

    private static BenefitReportTable OthersTable(BenefitComparison comparison)
        => new(
            "示範模式外的案件（依管道）",
            KindMeasured,
            ["管道", "案件數", "不計", "顧客操作步數（平均）", "AI 成本（平均每案）"],
            [.. comparison.Others.Select(x => (IReadOnlyList<string>)
            [
                AfterSalesLabels.ChannelText(x.Channel),
                x.RunCount.ToString(),
                (x.ReplayExcludedCount + x.ExcludedRunCount).ToString(),
                BenefitMetricDisplay.Format(x.CustomerSteps, false),
                BenefitMetricDisplay.Format(x.AiCostTwd, true),
            ])],
            comparison.Others.Count == 0
                ? "範圍內沒有示範模式外（沒有劇本執行編號）的案件。"
                : "沒有經過「準備此劇本」的案件，依顧客使用的管道彙總；只有實測，不進月度推估。");

    private static BenefitReportTable CostTable(BenefitCostBreakdown cost)
        => new(
            "AI 成本對帳",
            KindMeasured,
            ["項目", "金額（新台幣）", "說明"],
            [
                ["劇本執行", BenefitMetricDisplay.Format(cost.DemoRunsTwd, true), "全部劇本執行（含回放，回放成本為 0）"],
                ["其他案件", BenefitMetricDisplay.Format(cost.OtherCasesTwd, true), "示範模式外的案件"],
                ["對應不到", BenefitMetricDisplay.Format(cost.UnattributedTwd, true), $"{cost.UnattributedCallCount} 次呼叫對應不到執行或案件"],
                ["客服 AI 助手", BenefitMetricDisplay.Format(cost.StaffAssistantTwd, true), $"{cost.StaffAssistantCallCount} 次呼叫；未歸入每案，不在任何對照數字裡"],
            ],
            "前三項相加＝「Token 用量」頁同一範圍三種售後作業（AI 售後客服 Agent、AI 售後多 Agent、AI 售後初審 Workflow）的合計。");

    private static BenefitReportTable AssumptionTable(BenefitExportRequest request)
    {
        var a = request.Assumption;
        return new BenefitReportTable(
            "假設值",
            KindAssumed,
            ["項目", "數值"],
            [
                ["傳統排隊等待（分鐘）", Number(a.QueueWaitMinutesTraditional)],
                ["AI 排隊等待（分鐘）", Number(a.QueueWaitMinutesAi)],
                ["傳統每案聯繫次數", Number(a.ContactsPerCaseTraditional)],
                ["AI 每案聯繫次數", Number(a.ContactsPerCaseAi)],
                ["每次聯繫（分鐘）", Number(a.MinutesPerContact)],
                ["客服每分鐘成本（NT$）", Number(a.StaffCostPerMinuteTwd)],
                ["每月案件量", Number(a.MonthlyCaseVolume)],
            ],
            "這些不是實測，是人工設定的假設，只用在月度推估。"
                + (request.AssumptionUnsaved ? "目前顯示的是尚未儲存的假設值。" : string.Empty));
    }

    private static string Number(double value) => value.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static BenefitReportTable EstimateTable(BenefitExportRequest request)
    {
        var estimate = request.Estimate;
        var rows = new List<IReadOnlyList<string>>();
        string? note = null;

        if (estimate is null || request.EstimateFamily is null)
        {
            rows.Add(["月度推估", "沒有可以推估的情境（需要有傳統表單做法的情境）", string.Empty]);
        }
        else
        {
            rows.Add(["對比", $"{request.EstimateFamily}：傳統表單 對比 {BenefitMetricDisplay.FlowLabel(request.EstimateFlow)}", string.Empty]);
            if (estimate.IsSufficient == false)
            {
                rows.Add(["資料不足", estimate.InsufficientReason ?? NoData, string.Empty]);
            }
            else
            {
                rows.Add([
                    "每月節省客服時數",
                    BenefitMetricDisplay.FormatMonthly(estimate.SavedStaffHoursPerMonth) + " 小時",
                    $"（傳統每案 {BenefitMetricDisplay.FormatMonthly(estimate.TraditionalStaffMinutesPerCase)} 分 − AI 每案 {BenefitMetricDisplay.FormatMonthly(estimate.AiStaffMinutesPerCase)} 分）× {BenefitMetricDisplay.FormatMonthly(estimate.MonthlyCaseVolume)} 件 ÷ 60"]);
                rows.Add([
                    "每月節省成本",
                    BenefitMetricDisplay.FormatMonthlyTwd(estimate.SavedCostPerMonthTwd),
                    "節省的客服分鐘 × 每分鐘成本 − 每月 AI 成本" + (estimate.SavedCostPerMonthTwd < 0 ? "；負數代表 AI 做法反而比較貴" : string.Empty)]);
                rows.Add([
                    "每月 AI 成本",
                    BenefitMetricDisplay.FormatMonthlyTwd(estimate.AiCostPerMonthTwd),
                    $"AI 每案 {BenefitMetricDisplay.Format(estimate.AiCostPerCaseTwd, true)}（已完成執行的實測平均）× {BenefitMetricDisplay.FormatMonthly(estimate.MonthlyCaseVolume)} 件"]);
                rows.Add([
                    "顧客等待（每案，傳統 → AI）",
                    $"{BenefitMetricDisplay.FormatMonthly(estimate.TraditionalCustomerWaitMinutes)} → {BenefitMetricDisplay.FormatMonthly(estimate.AiCustomerWaitMinutes)} 分",
                    "端到端實測平均＋排隊等待假設"]);
            }

            if (estimate.Warning is not null)
            {
                rows.Add(["提醒", estimate.Warning, string.Empty]);
            }

            if (BenefitMetricDisplay.UnattributedWarning(request.Comparison.Cost) is { } unattributed)
            {
                rows.Add(["提醒", unattributed, string.Empty]);
            }

            note = $"依傳統 {estimate.TraditionalCompletedRunCount} 次、AI {estimate.AiCompletedRunCount} 次「已完成」（客服已開啟並做出決定）執行的實測平均，加上假設值計算；客服 AI 助手的成本未歸入每案，不在推估裡。";
        }

        return new BenefitReportTable("月度推估", KindEstimated, ["項目", "數值", "算法"], rows, note);
    }

    #endregion

    #region Excel

    public static byte[] BuildXlsx(BenefitExportRequest request)
    {
        var report = BuildReport(request);
        var notes = ExcelNotes(report, request.GeneratedAt, request.SystemName, request.SystemVersion);

        using var workbook = new XLWorkbook();
        WriteTables(workbook.Worksheets.Add(SummarySheet), notes, report.Measured);

        TabularExport.AddSheet(
            workbook,
            DetailSheet,
            DetailColumns,
            request.Comparison.Runs,
            [.. notes, $"本頁種類：{KindMeasured}。不計的執行照列，原因見「計入平均」欄。"]);

        WriteTables(workbook.Worksheets.Add(AssumptionSheet), notes, report.Assumed);
        return TabularExport.Save(workbook);
    }

    private static List<string> ExcelNotes(BenefitReport report, DateTime generatedAt, string systemName, string systemVersion)
    {
        var notes = report.Notes.Select(x => $"{x.Key}：{x.Value}").ToList();
        notes.Add(report.Disclaimer);
        var system = string.IsNullOrWhiteSpace(systemName) ? "CustomerHelpAgent" : systemName;
        notes.Add($"產生時間：{generatedAt:yyyy/MM/dd HH:mm}　系統：{system} {systemVersion}".TrimEnd());
        return notes;
    }

    private static readonly IReadOnlyList<ExportColumn<RunMetrics>> DetailColumns =
    [
        new("種類", _ => KindMeasured, Width: 8),
        new("編號", x => x.RunId, Width: 8),
        new("劇本代碼", x => x.ScenarioCode, Width: 10),
        new("劇本", x => DemoScenarios.All.FirstOrDefault(s => s.Code == x.ScenarioCode)?.Title ?? x.ScenarioCode, Width: 26),
        new("情境", x => x.Family, Width: 12),
        new("做法", x => BenefitMetricDisplay.FlowLabel(x.Flow), Width: 12),
        new("示範帳號", x => x.Account, Width: 14),
        new("計入平均", BenefitFamilyLayout.RunStatusText, Width: 26),
        new("開始", x => x.StartedAt, TabularExport.DateTimeFormat),
        new("區間終點", x => x.EndBoundary, TabularExport.DateTimeFormat),
        new("最後活動", x => x.LastActivityAt, TabularExport.DateTimeFormat),
        .. BenefitMetricDisplay.Metrics.Select(metric =>
            new ExportColumn<RunMetrics>(metric.Label, x => metric.RunValue(x), DetailNumberFormat(metric), 14)),
    ];

    /// <summary>
    /// 明細的數字格式：Excel 的「#,##0.#」會把整數顯示成「4.」，所以依指標固定小數位數——
    /// 金額 2 位、分鐘 1 位、次數類（單次值一定是整數）不帶小數。
    /// </summary>
    private static string DetailNumberFormat(BenefitMetric metric)
        => metric.IsCurrency ? "#,##0.00" : metric.IsMinutes ? "#,##0.0" : "#,##0";

    /// <summary>把幾張報告表直向排進一張工作表：每張表「標題＋種類」「欄名」「資料列」「附註」。假設與推估用琥珀色底。</summary>
    private static void WriteTables(IXLWorksheet sheet, IReadOnlyList<string> notes, IReadOnlyList<BenefitReportTable> tables)
    {
        var row = 1;
        var columnCount = tables.Max(x => x.Header.Count);
        foreach (var table in tables)
        {
            var fill = table.IsAssumed ? AssumedFill : null;
            var titleRow = row++;
            var title = sheet.Cell(titleRow, 1);
            title.SetValue($"{table.Heading}【{table.Kind}】");
            title.Style.Font.Bold = true;
            if (fill is not null)
            {
                sheet.Range(titleRow, 1, titleRow, table.Header.Count).Style.Fill.BackgroundColor = fill;
            }

            for (var c = 0; c < table.Header.Count; c++)
            {
                var header = sheet.Cell(row, c + 1);
                header.SetValue(table.Header[c]);
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = fill ?? HeaderFill;
            }

            row++;
            foreach (var data in table.Rows)
            {
                for (var c = 0; c < data.Count; c++)
                {
                    var cell = sheet.Cell(row, c + 1);
                    if (table.Kind == KindAssumed && c == 1
                        && double.TryParse(data[c], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        // 假設值寫成數字，客戶拿到檔案可以直接改來試算。
                        cell.SetValue(number);
                        cell.Style.NumberFormat.Format = number == Math.Floor(number) ? "#,##0" : "#,##0.##";
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                    }
                    else
                    {
                        cell.SetValue(data[c]);
                    }

                    cell.Style.Alignment.WrapText = true;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                    if (fill is not null)
                    {
                        cell.Style.Fill.BackgroundColor = fill;
                    }
                }

                row++;
            }

            if (table.Note is not null)
            {
                var noteCell = sheet.Cell(row++, 1);
                noteCell.SetValue(table.Note);
                noteCell.Style.Font.FontColor = XLColor.DimGray;
            }

            row++;
        }

        // 檔頭說明放在表格下方（每張表後已空一列），與明細頁一致：開檔先看到資料。
        foreach (var note in notes)
        {
            var cell = sheet.Cell(row++, 1);
            cell.SetValue(note);
            cell.Style.Font.FontColor = XLColor.DimGray;
        }

        for (var c = 1; c <= columnCount; c++)
        {
            sheet.Column(c).Width = 30;
        }
    }

    #endregion

    #region PDF

    private static readonly Color AssumedColor = Color.FromRgb(0xFF, 0xF3, 0xCD);

    /// <summary>
    /// PDF：沿用 <see cref="AiMarkdownPdfRenderer"/> 的文件骨架（字型、頁首頁尾、標題、檔頭說明）。
    /// 它的 Markdown 渲染器沒有表格，所以表格直接用 MigraDoc 的 Table 排（與 Token 用量 PDF 同做法），不經 Markdown。
    /// </summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] BuildPdf(BenefitExportRequest request)
        => AiMarkdownPdfRenderer.Render(BuildPdfDocument(request));

    /// <summary>PDF 的 MigraDoc 文件（渲染前）：測試可以數表格，確認每張報告表都有排進去。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    internal static Document BuildPdfDocument(BenefitExportRequest request)
    {
        var report = BuildReport(request);
        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "CustomerHelpAgent" : request.SystemName;
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: report.Title,
            subject: "效益對照",
            author: systemName,
            headerText: $"{systemName}｜{report.Title}",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, report.Title);
        AiMarkdownPdfRenderer.AddMetadata(section, report.Notes, report.Disclaimer);

        foreach (var table in report.Measured.Concat(report.Assumed))
        {
            AddTable(section, table);
        }

        var tail = section.AddParagraph();
        tail.Style = AiMarkdownPdfRenderer.MetaStyleName;
        tail.Format.SpaceBefore = Unit.FromPoint(8);
        CjkLineBreak.AddTo(tail.Elements, "每次執行的明細請見同範圍的 Excel 匯出。");

        return document;
    }

    private static void AddTable(Section section, BenefitReportTable table)
    {
        section.AddParagraph($"{table.Heading}（{table.Kind}）", StyleNames.Heading2);

        var pdfTable = section.AddTable();
        pdfTable.Borders.Color = AiMarkdownPdfRenderer.BorderColor;
        pdfTable.Borders.Width = 0.5;
        pdfTable.Format.Font.Size = Unit.FromPoint(8.5);
        pdfTable.Format.SpaceAfter = 0;
        pdfTable.TopPadding = Unit.FromPoint(2);
        pdfTable.BottomPadding = Unit.FromPoint(2);
        pdfTable.LeftPadding = Unit.FromPoint(3);
        pdfTable.RightPadding = Unit.FromPoint(3);

        // A4 直式可用寬 17 cm：第一欄固定，其餘平分。
        const double total = 17;
        var first = table.Header.Count <= 2 ? 6.5 : 4.6;
        pdfTable.AddColumn(Unit.FromCentimeter(first));
        for (var index = 1; index < table.Header.Count; index++)
        {
            pdfTable.AddColumn(Unit.FromCentimeter((total - first) / (table.Header.Count - 1)));
        }

        var header = pdfTable.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = table.IsAssumed ? AssumedColor : AiMarkdownPdfRenderer.ShadingColor;
        for (var c = 0; c < table.Header.Count; c++)
        {
            SetCell(header, c, table.Header[c]);
            header.Cells[c].Format.Font.Color = AiMarkdownPdfRenderer.HeadingColor;
        }

        foreach (var data in table.Rows)
        {
            var row = pdfTable.AddRow();
            if (table.IsAssumed)
            {
                row.Shading.Color = AssumedColor;
            }

            for (var c = 0; c < table.Header.Count; c++)
            {
                SetCell(row, c, c < data.Count ? data[c] : string.Empty);
            }
        }

        if (table.Note is not null)
        {
            var note = section.AddParagraph();
            note.Style = AiMarkdownPdfRenderer.MetaStyleName;
            note.Format.SpaceBefore = Unit.FromPoint(3);
            CjkLineBreak.AddTo(note.Elements, table.Note);
        }
    }

    private static void SetCell(Row row, int index, string text)
    {
        var cell = row.Cells[index];
        CjkLineBreak.AddTo(cell.AddParagraph().Elements, text);
        cell.Format.Alignment = ParagraphAlignment.Left;
        cell.VerticalAlignment = VerticalAlignment.Center;
    }

    #endregion
}
