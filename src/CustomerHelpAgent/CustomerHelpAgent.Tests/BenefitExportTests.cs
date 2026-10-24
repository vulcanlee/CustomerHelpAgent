using System.Text.RegularExpressions;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Benefit;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 效益對照的 Excel／PDF 匯出（<see cref="BenefitExport"/>，M6）：三張工作表、檔頭說明、實測／假設／推估標示、檔名、不含個資。
/// PDF 內嵌字型是子集合，文字無法抽出，所以 PDF 的內容以產生它的同一份報告模型（<see cref="BenefitExport.BuildReport"/>）斷言，
/// 檔案本身驗證是合法的 PDF（簽章、可解析、至少一頁、標題）。標 <c>[Collection("PdfFont")]</c> 的理由同 <c>AiReportPdfBuilderTests</c>。
/// </summary>
[Collection("PdfFont")]
public sealed class BenefitExportTests
{
    private static readonly DemoAssumptionInput Assumption = new(240, 30, 2, 0.5, 5, 8, 1000);

    private static FlowMetrics Flow(ScenarioFlow flow, double customerSteps, double aiCost, int unpriced = 0) => new()
    {
        Family = "換尺碼",
        Flow = flow,
        RunCount = 2,
        ReplayExcludedCount = 1,
        CustomerSteps = customerSteps,
        StaffSteps = 3,
        EndToEndMinutes = 20,
        EndToEndSampleCount = 2,
        StaffHandlingMinutes = 6,
        StaffHandlingSampleCount = 2,
        AiCostTwd = aiCost,
        ModelCallCount = flow == ScenarioFlow.Traditional ? 0 : 4,
        RequestCount = 1,
        CompletedRunCount = 2,
        CompletedEndToEndMinutes = 20,
        CompletedStaffHandlingMinutes = 6,
        CompletedAiCostTwd = aiCost,
        CompletedModelCallCount = flow == ScenarioFlow.Traditional ? 0 : 8,
        UnpricedCallCount = unpriced,
        CompletedUnpricedCallCount = unpriced,
    };

    private static RunMetrics Run(int id, string code, ScenarioFlow flow, string? exclusion = null, bool replay = false) => new()
    {
        RunId = id,
        ScenarioCode = code,
        Family = "換尺碼",
        Flow = flow,
        Account = "customer1",
        IsReplay = replay,
        ExclusionReason = exclusion,
        IsCompleted = exclusion is null,
        StartedAt = new DateTime(2026, 10, 24, 10, 0, 0),
        EndBoundary = new DateTime(2026, 10, 25),
        LastActivityAt = new DateTime(2026, 10, 24, 10, 30, 0),
        CustomerSteps = 4,
        AiCostTwd = 0.5,
    };

    private static BenefitExportRequest CreateRequest(bool unsaved = false, int unpriced = 0, int unattributed = 0)
    {
        var traditional = Flow(ScenarioFlow.Traditional, 4, 0);
        var single = Flow(ScenarioFlow.SingleAgent, 1, 0.5, unpriced);
        var comparison = new BenefitComparison
        {
            From = new DateTime(2026, 10, 24),
            To = new DateTime(2026, 10, 25),
            Families = [new FamilyComparison { Family = "換尺碼", Flows = [traditional, single] }],
            Runs =
            [
                Run(1, "A-T", ScenarioFlow.Traditional),
                Run(2, "A", ScenarioFlow.SingleAgent),
                Run(3, "A", ScenarioFlow.SingleAgent, "回放（不計）", replay: true),
            ],
            Others = [new ChannelMetrics { Channel = AfterSalesChannel.Agent, RunCount = 3, ReplayExcludedCount = 1, CustomerSteps = 2, AiCostTwd = 0.35 }],
            Cost = new BenefitCostBreakdown { DemoRunsTwd = 1, StaffAssistantTwd = 0.4, StaffAssistantCallCount = 3, UnattributedCallCount = unattributed },
        };

        return new BenefitExportRequest(
            comparison,
            Assumption,
            unsaved,
            "換尺碼",
            ScenarioFlow.SingleAgent,
            BenefitEstimator.Estimate(traditional, single, Assumption),
            "客服 Agent 示範",
            "1.0.7 (2026/10/24)",
            "admin",
            new DateTime(2026, 10, 24, 12, 0, 0));
    }

    private static string SheetText(IXLWorksheet sheet)
        => string.Join("\n", sheet.CellsUsed().Select(x => x.GetFormattedString()));

    [Fact]
    public void Excel_HasThreeSheets_AndAssumptionLabels()
    {
        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(CreateRequest(unsaved: true))));

        Assert.Equal(["對照摘要", "明細", "假設值與推估"], workbook.Worksheets.Select(x => x.Name));

        // 每張工作表的檔頭都有範圍、排除原則、AI 成本口徑與「僅供參考」。
        foreach (var sheet in workbook.Worksheets)
        {
            var text = SheetText(sheet);
            Assert.Contains("資料範圍：2026/10/24 00:00 ～ 2026/10/25 00:00（不含結束時間）", text, StringComparison.Ordinal);
            Assert.Contains("回放與無效執行不計（原因見明細）", text, StringComparison.Ordinal);
            Assert.Contains("每案經 AI 對話紀錄關聯的 Token 用量成本；客服 AI 助手成本未歸入每案", text, StringComparison.Ordinal);
            Assert.Contains("假設值／推估僅供參考", text, StringComparison.Ordinal);
        }

        // 摘要頁：實測標示、相對傳統的差異、缺資料的提示。
        var summarySheet = workbook.Worksheet("對照摘要");
        var summary = SheetText(summarySheet);
        Assert.Contains("換尺碼【實測】", summary, StringComparison.Ordinal);
        Assert.Contains("1（−75%，改善）", summary, StringComparison.Ordinal);
        Assert.Contains("尚無資料", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("【假設】", summary, StringComparison.Ordinal);

        // 假設與推估頁：標示「假設」「推估（依假設值）」，並用琥珀色底與實測區分。
        var assumed = workbook.Worksheet("假設值與推估");
        var assumedText = SheetText(assumed);
        Assert.Contains("假設值【假設】", assumedText, StringComparison.Ordinal);
        Assert.Contains("月度推估【推估（依假設值）】", assumedText, StringComparison.Ordinal);
        Assert.Contains("目前顯示的是尚未儲存的假設值", assumedText, StringComparison.Ordinal);
        var assumedHeading = assumed.CellsUsed().First(x => x.GetFormattedString() == "假設值【假設】");
        var measuredHeading = summarySheet.CellsUsed().First(x => x.GetFormattedString() == "換尺碼【實測】");
        Assert.Equal(XLColor.FromHtml("#FFF3CD"), assumedHeading.Style.Fill.BackgroundColor);
        Assert.NotEqual(assumedHeading.Style.Fill.BackgroundColor, measuredHeading.Style.Fill.BackgroundColor);

        // 明細頁：三筆執行都列出，回放的標不計。
        var detailText = SheetText(workbook.Worksheet("明細"));
        Assert.Contains("回放（不計）", detailText, StringComparison.Ordinal);
        Assert.Contains("計入（已完成）", detailText, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_ContainsComparisonAndAssumptionNote()
    {
        var request = CreateRequest();
        var bytes = BenefitExport.BuildPdf(request);

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 1);
        Assert.Equal(BenefitExport.ReportTitle, pdf.Info.Title);

        // PDF 的內容來源就是這份報告模型。
        var report = BenefitExport.BuildReport(request);
        var notes = string.Join("\n", report.Notes.Select(x => $"{x.Key}：{x.Value}")) + "\n" + report.Disclaimer;
        Assert.Contains("資料範圍：2026/10/24 00:00 ～ 2026/10/25 00:00（不含結束時間）", notes, StringComparison.Ordinal);
        Assert.Contains("回放與無效執行不計（原因見明細）", notes, StringComparison.Ordinal);
        Assert.Contains("客服 AI 助手成本未歸入每案", notes, StringComparison.Ordinal);
        Assert.Contains("假設值／推估僅供參考", notes, StringComparison.Ordinal);

        var family = report.Measured.Single(x => x.Heading == "換尺碼");
        Assert.Equal(BenefitExport.KindMeasured, family.Kind);
        Assert.Equal(["指標", "傳統表單", "單一 Agent", "多 Agent"], family.Header);
        var steps = family.Rows.Single(x => x[0] == "顧客操作步數");
        Assert.Equal(["顧客操作步數", "4", "1（−75%，改善）", "尚無資料"], steps);
        Assert.Equal("尚無資料（請在展示控制台準備劇本 A-M）", family.Rows.Single(x => x[0] == "有效次數")[3]);

        Assert.Equal([BenefitExport.KindAssumed, BenefitExport.KindEstimated], report.Assumed.Select(x => x.Kind));
        Assert.Contains(report.Assumed[1].Rows, x => x[0] == "每月節省客服時數" && x[1].EndsWith("小時", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_HasOthersAndCostTables()
    {
        var report = BenefitExport.BuildReport(CreateRequest());

        var others = report.Measured.Single(x => x.Heading == "示範模式外的案件（依管道）");
        Assert.Equal(["AI 助理", "3", "1", "2", "NT$0.35"], others.Rows.Single());
        var cost = report.Measured.Single(x => x.Heading == "AI 成本對帳");
        Assert.Equal(BenefitExport.KindMeasured, cost.Kind);
        Assert.Contains(cost.Rows, x => x[0] == "劇本執行" && x[1] == "NT$1.00");
        Assert.Contains(cost.Rows, x => x[0] == "客服 AI 助手" && x[1] == "NT$0.40" && x[2].Contains("未歸入每案", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_UnpricedRow_OnlyWhenThereAreUnpricedCalls()
    {
        var none = BenefitExport.BuildReport(CreateRequest()).Measured.Single(x => x.Heading == "換尺碼");
        Assert.DoesNotContain(none.Rows, x => x[0].StartsWith("未定價", StringComparison.Ordinal));

        var some = BenefitExport.BuildReport(CreateRequest(unpriced: 2)).Measured.Single(x => x.Heading == "換尺碼");
        Assert.Equal(["未定價的 AI 呼叫（AI 成本偏低）", "—", "2 次", "—"], some.Rows.Single(x => x[0].StartsWith("未定價", StringComparison.Ordinal)));
    }

    [Fact]
    public void Report_Estimate_InsufficientAndWarningRows()
    {
        var request = CreateRequest(unpriced: 2);
        var insufficient = BenefitExport.BuildReport(request with { Estimate = BenefitEstimator.Estimate(null, Flow(ScenarioFlow.SingleAgent, 1, 0.5), Assumption) });
        var rows = insufficient.Assumed[1].Rows;
        Assert.Contains(rows, x => x[0] == "資料不足" && x[1].Length > 0);
        Assert.DoesNotContain(rows, x => x[0] == "每月節省成本");

        var estimate = request.Estimate!;
        Assert.NotNull(estimate.Warning);
        var warned = BenefitExport.BuildReport(request).Assumed[1].Rows;
        Assert.Contains(warned, x => x[0] == "提醒" && x[1] == estimate.Warning);
    }

    [Fact]
    public void Report_Estimate_UnattributedCallsWarningRow()
    {
        // 與畫面相同：範圍內有對不到案件的 AI 呼叫時，推估表多一列提醒。
        var none = BenefitExport.BuildReport(CreateRequest()).Assumed[1].Rows;
        Assert.DoesNotContain(none, x => x[1].Contains("對應不到案件", StringComparison.Ordinal));

        var some = BenefitExport.BuildReport(CreateRequest(unattributed: 3)).Assumed[1].Rows;
        Assert.Contains(some, x => x[0] == "提醒" && x[1] == "有 3 次 AI 呼叫對應不到案件，AI 成本可能偏低。");
    }

    [Fact]
    public void Assumptions_SavedVersusUnsaved_AndNumericCells()
    {
        var saved = BenefitExport.BuildReport(CreateRequest(unsaved: false)).Assumed[0];
        Assert.DoesNotContain("尚未儲存", saved.Note, StringComparison.Ordinal);
        Assert.Contains("尚未儲存", BenefitExport.BuildReport(CreateRequest(unsaved: true)).Assumed[0].Note, StringComparison.Ordinal);

        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(CreateRequest())));
        var sheet = workbook.Worksheet("假設值與推估");
        Assert.DoesNotContain("尚未儲存", SheetText(sheet), StringComparison.Ordinal);
        var label = sheet.CellsUsed().First(x => x.GetFormattedString() == "每月案件量");
        var value = label.CellRight();
        Assert.Equal(XLDataType.Number, value.DataType);
        Assert.Equal(1000, value.GetDouble());
        Assert.Equal(XLDataType.Number, sheet.CellsUsed().First(x => x.GetFormattedString() == "客服每分鐘成本（NT$）").CellRight().DataType);
    }

    [Fact]
    public void Excel_NumberCells_NoTrailingDecimalPoint()
    {
        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(CreateRequest())));

        // 「#,##0.##」會把整數顯示成「4.」「1,000.」；整數不帶小數點，小數照常顯示。
        foreach (var name in new[] { "明細", "假設值與推估" })
        {
            var numbers = workbook.Worksheet(name).CellsUsed().Where(x => x.DataType == XLDataType.Number).ToList();
            Assert.NotEmpty(numbers);
            Assert.All(numbers, x => Assert.False(x.GetFormattedString().EndsWith('.'), $"{name}!{x.Address}：{x.GetFormattedString()}"));
        }

        var volume = workbook.Worksheet("假設值與推估").CellsUsed().First(x => x.GetFormattedString() == "每月案件量").CellRight();
        Assert.Equal("1,000", volume.GetFormattedString());
        var contacts = workbook.Worksheet("假設值與推估").CellsUsed().First(x => x.GetFormattedString() == "AI 每案聯繫次數").CellRight();
        Assert.Equal("0.5", contacts.GetFormattedString());
    }

    [Fact]
    public void Excel_HeaderFrozen_NotesBelowTable()
    {
        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(CreateRequest())));

        // 明細頁：標題列在第 1 列並凍結，3 筆資料之後空一列才是說明。
        var detail = workbook.Worksheet("明細");
        Assert.Equal(1, detail.SheetView.SplitRow);
        Assert.Equal("種類", detail.Cell(1, 1).GetFormattedString());
        Assert.Equal("實測", detail.Cell(2, 1).GetFormattedString());
        Assert.Equal(string.Empty, detail.Cell(5, 1).GetFormattedString());
        Assert.StartsWith("資料範圍：", detail.Cell(6, 1).GetFormattedString(), StringComparison.Ordinal);

        // 其餘兩張工作表也把說明放在表格下方（第 1 列是第一張表的標題，不是說明）。
        foreach (var name in new[] { "對照摘要", "假設值與推估" })
        {
            var sheet = workbook.Worksheet(name);
            Assert.DoesNotContain("資料範圍：", sheet.Cell(1, 1).GetFormattedString(), StringComparison.Ordinal);
            var note = sheet.CellsUsed().First(x => x.GetFormattedString().StartsWith("資料範圍：", StringComparison.Ordinal));
            var lastTable = sheet.CellsUsed().Where(x => x.Address.ColumnNumber == 1 && x.Address.RowNumber < note.Address.RowNumber).Max(x => x.Address.RowNumber);
            Assert.True(lastTable < note.Address.RowNumber - 1, "說明前要空一列");
        }
    }

    [Fact]
    public void Pdf_HasOneTablePerReportTable()
    {
        var request = CreateRequest();
        var report = BenefitExport.BuildReport(request);

        var document = BenefitExport.BuildPdfDocument(request);

        var tables = document.LastSection.Elements.OfType<MigraDoc.DocumentObjectModel.Tables.Table>().Count();
        Assert.Equal(report.Measured.Count + report.Assumed.Count, tables);
    }

    [Fact]
    public void Report_FamilyStructure_MatchesSharedLayout()
    {
        var request = CreateRequest();
        var sections = BenefitFamilyLayout.BuildSections(request.Comparison);

        var tables = BenefitExport.BuildReport(request).Measured.Where(x => x.Header[0] == "指標").ToList();

        Assert.Equal(sections.Select(x => x.Family), tables.Select(x => x.Heading));
        for (var i = 0; i < sections.Count; i++)
        {
            Assert.Equal(sections[i].Slots.Select(x => BenefitMetricDisplay.FlowLabel(x.Flow)), tables[i].Header.Skip(1));
        }
    }

    [Fact]
    public void Export_DoesNotThrow_WhenThereIsNoData()
    {
        var request = CreateRequest() with
        {
            Comparison = new BenefitComparison { From = new DateTime(2026, 10, 24), To = new DateTime(2026, 10, 25) },
            Estimate = null,
            EstimateFamily = null,
        };

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(BenefitExport.BuildPdf(request), 0, 5));
        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(request)));
        Assert.Equal(3, workbook.Worksheets.Count);
    }

    [Fact]
    public void Export_NoPersonalData()
    {
        var request = CreateRequest();
        using var workbook = new XLWorkbook(new MemoryStream(BenefitExport.BuildXlsx(request)));
        var report = BenefitExport.BuildReport(request);
        var texts = workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()).Select(x => x.GetFormattedString())
            .Concat(report.Measured.Concat(report.Assumed).SelectMany(x => x.Rows.SelectMany(r => r).Concat(x.Header)))
            .ToList();

        // 明細只有示範帳號代號；沒有信箱、電話長度的數字、姓名／地址／手機等欄位。
        Assert.Contains("customer1", texts);
        Assert.DoesNotContain(texts, text => text.Contains('@', StringComparison.Ordinal));
        Assert.DoesNotContain(texts, text => Regex.IsMatch(text, @"\d{8,}"));
        Assert.DoesNotContain(texts, text => Regex.IsMatch(text, "姓名|地址|手機|電話|收件|信箱"));
    }

    [Fact]
    public void FileName_ContainsDateRange()
    {
        var comparison = new BenefitComparison { From = new DateTime(2026, 10, 18), To = new DateTime(2026, 10, 25) };

        Assert.Equal("CustomerHelpAgent.Web-benefit-20261018-20261024.xlsx", BenefitExport.FileName(comparison, "xlsx"));
        Assert.Equal("CustomerHelpAgent.Web-benefit-20261018-20261024.pdf", BenefitExport.FileName(comparison, "pdf"));
    }
}
