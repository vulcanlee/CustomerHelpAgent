using MigraDoc.DocumentObjectModel;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>PDF 報告的輸入。全部由日誌檢視頁在按下匯出時組好。</summary>
public sealed record AiReportPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public DateTime QueryStartTime { get; init; }
    public DateTime QueryEndTime { get; init; }

    /// <summary>最低等級。空字串代表不限。</summary>
    public string MinimumLevel { get; init; } = string.Empty;

    /// <summary>關鍵字。空字串代表沒有指定。</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>模型回傳的原始 Markdown。</summary>
    public string Markdown { get; init; } = string.Empty;

    public AiPromptBuildResult Prompt { get; init; } = new();
    public AiTokenUsage? Usage { get; init; }
    public string ModelName { get; init; } = string.Empty;
}

/// <summary>
/// 把 AI 日誌分析結果排成 PDF 報告。
///
/// 版面、樣式與 Markdown 渲染器在 <see cref="AiMarkdownPdfRenderer"/>（0.9.68 起與 AI 例外分析共用），
/// 本類別只負責日誌分析專屬的標題、中介資訊與附錄。
/// </summary>
public static class AiReportPdfBuilder
{
    /// <summary>模型只會用 ## 與 ###，仍夾住上限以防輸出超出預期。</summary>
    private const int MaxHeadingLevel = 3;

    private const string Disclaimer = "本報告由 AI 依上述日誌生成，內容僅供參考，請以原始日誌為準。";

    /// <summary>產生 PDF 位元組。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(AiReportPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "CustomerHelpAgent" : request.SystemName;
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: "日誌 AI 分析報告",
            subject: $"{request.QueryStartTime:yyyy-MM-dd HH:mm} ～ {request.QueryEndTime:yyyy-MM-dd HH:mm}",
            author: systemName,
            headerText: $"{systemName}｜日誌 AI 分析報告",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, "日誌 AI 分析報告");
        AiMarkdownPdfRenderer.AddMetadata(section, BuildMetadataLines(request), Disclaimer);
        AiMarkdownPdfRenderer.RenderMarkdown(section, request.Markdown);
        AddAppendix(section, request);

        return AiMarkdownPdfRenderer.Render(document);
    }

    /// <summary>
    /// 報告的中介資訊行。抽成純函式，讓測試可以直接斷言內容，
    /// 不必去解析產出的 PDF（方案內沒有輕量的 PDF 文字抽取相依）。
    /// 「有值才加」的規則也集中在這裡。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildMetadataLines(AiReportPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = new List<KeyValuePair<string, string>>();

        if (string.IsNullOrWhiteSpace(request.SystemName) == false)
        {
            var system = string.IsNullOrWhiteSpace(request.SystemVersion)
                ? request.SystemName
                : $"{request.SystemName}　版本 {request.SystemVersion}";
            lines.Add(new KeyValuePair<string, string>("系統", system));
        }

        lines.Add(new KeyValuePair<string, string>("產生時間", $"{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}"));

        if (string.IsNullOrWhiteSpace(request.OperatorAccount) == false)
        {
            lines.Add(new KeyValuePair<string, string>("操作者", request.OperatorAccount));
        }

        lines.Add(new KeyValuePair<string, string>(
            "查詢區間",
            $"{request.QueryStartTime:yyyy-MM-dd HH:mm:ss} ～ {request.QueryEndTime:yyyy-MM-dd HH:mm:ss}"));

        lines.Add(new KeyValuePair<string, string>(
            "最低等級",
            string.IsNullOrWhiteSpace(request.MinimumLevel) ? "不限" : request.MinimumLevel));

        if (string.IsNullOrWhiteSpace(request.Keyword) == false)
        {
            lines.Add(new KeyValuePair<string, string>("關鍵字", request.Keyword));
        }

        lines.Add(new KeyValuePair<string, string>("送出範圍", BuildScopeText(request.Prompt)));

        if (string.IsNullOrWhiteSpace(request.ModelName) == false)
        {
            lines.Add(new KeyValuePair<string, string>("使用模型", request.ModelName));
        }

        var usage = BuildUsageText(request.Usage);
        if (string.IsNullOrEmpty(usage) == false)
        {
            lines.Add(new KeyValuePair<string, string>("用量", usage));
        }

        return lines;
    }

    private static string BuildScopeText(AiPromptBuildResult prompt)
    {
        var text = $"送出 {prompt.IncludedEntryCount} 筆／查詢 {prompt.TotalEntryCount} 筆";
        if (prompt.DroppedByEntryLimit)
        {
            text += "（已依筆數上限取最新資料）";
        }

        return text;
    }

    /// <summary>只列出 API 真的有回的欄位。全缺時回空字串，呼叫端就不會加這一行。</summary>
    internal static string BuildUsageText(AiTokenUsage? usage)
    {
        if (usage is null || usage.HasAny == false)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        Append(parts, "輸入", usage.InputCount);
        Append(parts, "輸出", usage.OutputCount);
        Append(parts, "合計", usage.TotalCount);
        Append(parts, "快取輸入", usage.CachedInputCount);
        Append(parts, "推論", usage.ReasoningCount);
        return string.Join("　", parts);

        static void Append(List<string> parts, string label, int? value)
        {
            if (value.HasValue)
            {
                parts.Add($"{label} {value.Value:N0}");
            }
        }
    }

    private static void AddAppendix(Section section, AiReportPdfRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt.UserMessage))
        {
            return;
        }

        var heading = section.AddParagraph("附錄：實際送出的原始日誌");
        heading.Style = StyleNames.Heading2;
        heading.Format.PageBreakBefore = true;

        AiMarkdownPdfRenderer.AddCodeParagraph(section, request.Prompt.UserMessage);
    }
}
