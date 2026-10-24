using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.JSInterop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Export;

/// <summary>
/// 匯出檔的一欄（0.9.107 起）。
/// </summary>
/// <param name="Header">標題列文字。</param>
/// <param name="Value">取值。CSV 一律轉成文字（字串原樣、可格式化的值以目前文化）；Excel 依型別寫入（日期、數字是原生型別，字串一律是文字，不會被當成公式）。</param>
/// <param name="ExcelFormat">Excel 的數字或日期格式（例如 <c>yyyy-mm-dd hh:mm</c>）；null＝預設。</param>
/// <param name="Width">Excel 欄寬（字元數）。固定值：自動欄寬要量字型，伺服器上不可靠。</param>
public sealed record ExportColumn<T>(string Header, Func<T, object?> Value, string? ExcelFormat = null, double Width = 16);

/// <summary>
/// 表格匯出的唯一入口（0.9.107 起）：業務頁的 Excel、診斷頁的 CSV 都走這裡。
/// ⚠️ CSV 一律經 <see cref="TextDownloadPayload.Utf8WithBom"/>（沒有 BOM 時 Excel 開啟整片亂碼，編譯與測試都看不出來）。
/// </summary>
public static class TabularExport
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string CsvContentType = "text/csv";
    public const string DateTimeFormat = "yyyy-mm-dd hh:mm";
    public const string DateFormat = "yyyy-mm-dd";

    /// <summary>CSV 文字：標題列不加引號、每一格以雙引號包住（雙引號加倍），換行與 0.9.106 之前各頁手寫的相同。</summary>
    public static string ToCsvText<T>(IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', columns.Select(x => x.Header)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', columns.Select(x => Quote(ToText(x.Value(row))))));
        }

        return builder.ToString();
    }

    public static byte[] ToCsv<T>(IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows)
        => TextDownloadPayload.Utf8WithBom(ToCsvText(columns, rows));

    /// <summary>單一工作表的 .xlsx：標題列粗體、凍結、自動篩選。</summary>
    public static byte[] ToXlsx<T>(string sheetName, IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows)
    {
        using var workbook = new XLWorkbook();
        AddSheet(workbook, sheetName, columns, rows);
        return Save(workbook);
    }

    /// <summary>
    /// 在既有活頁簿加一張工作表（多工作表匯出用，M6 效益對照）：標題列粗體、凍結、自動篩選。
    /// 標題列永遠在第 1 列並凍結；<paramref name="notes"/> 不為空時，每行說明放在資料列下方（空一列後，淺灰字），這樣長表捲動時標題列仍在、說明不佔畫面。說明只放文字，不放個資。
    /// </summary>
    public static void AddSheet<T>(
        XLWorkbook workbook, string sheetName, IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows, IReadOnlyList<string>? notes = null)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        for (var c = 0; c < columns.Count; c++)
        {
            var header = sheet.Cell(1, c + 1);
            header.SetValue(columns[c].Header);
            header.Style.Font.Bold = true;
            sheet.Column(c + 1).Width = columns[c].Width;
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                SetValue(cell, columns[c].Value(row));
                if (columns[c].ExcelFormat is { } format)
                {
                    cell.Style.NumberFormat.Format = format;
                }
            }

            r++;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(1, r - 1), Math.Max(1, columns.Count)).SetAutoFilter();

        if (notes is { Count: > 0 })
        {
            r++;
            foreach (var text in notes)
            {
                var note = sheet.Cell(r++, 1);
                note.SetValue(text);
                note.Style.Font.FontColor = XLColor.DimGray;
            }
        }
    }

    /// <summary>活頁簿轉位元組。</summary>
    public static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>交給瀏覽器下載（與 PDF 匯出同一條路：<c>appFileDownload.downloadFromStream</c>）。</summary>
    public static async Task DownloadAsync(this IJSRuntime js, string fileName, byte[] content, string contentType)
    {
        using var stream = new MemoryStream(content);
        using var streamReference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, contentType);
    }

    internal static string Quote(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private static string? ToText(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString(),
    };

    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string text:
                cell.SetValue(text);
                break;
            case DateTime dateTime:
                cell.SetValue(dateTime);
                break;
            case bool flag:
                cell.SetValue(flag ? "是" : "否");
                break;
            case int number:
                cell.SetValue(number);
                break;
            case long number:
                cell.SetValue(number);
                break;
            case double number:
                cell.SetValue(number);
                break;
            case decimal number:
                cell.SetValue(number);
                break;
            default:
                cell.SetValue(value.ToString());
                break;
        }
    }
}
