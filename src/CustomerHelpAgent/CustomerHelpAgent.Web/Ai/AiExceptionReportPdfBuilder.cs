using MigraDoc.DocumentObjectModel;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>AI 例外分析 PDF 報告的輸入。全部由 AI 例外分析對話窗在按下下載時組好。</summary>
public sealed record AiExceptionReportPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }

    /// <summary>
    /// 例外明細，取自 <see cref="AiExceptionPromptBuilder.BuildDetailLines"/> ——
    /// 報告上的明細就是 AI 實際收到的內容（不含帳號）。
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> DetailLines { get; init; } = [];

    /// <summary>完整堆疊。null 或空白代表堆疊不可得。</summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// 第一則使用者訊息（例外明細）之後的對話：先是分析報告，再來是「追問、回覆」交替。
    /// </summary>
    public IReadOnlyList<AiChatMessage> Exchanges { get; init; } = [];

    public string ModelName { get; init; } = string.Empty;

    /// <summary>整段對話的累計用量。</summary>
    public AiTokenUsage? Usage { get; init; }
}

/// <summary>
/// 把 AI 例外分析（含追問）排成 PDF 報告（0.9.68 起）。
/// 版面、樣式與 Markdown 渲染器共用 <see cref="AiMarkdownPdfRenderer"/>。
/// </summary>
public static class AiExceptionReportPdfBuilder
{
    private const string Title = "例外 AI 分析報告";

    private const string Disclaimer = "本報告由 AI 依下列例外內容生成，內容僅供參考，請以原始紀錄與實際程式碼為準。";

    /// <summary>產生 PDF 位元組。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(AiExceptionReportPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "CustomerHelpAgent" : request.SystemName;
        var exceptionType = request.DetailLines.FirstOrDefault(line => line.Key == "例外類型").Value ?? string.Empty;
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: Title,
            subject: exceptionType,
            author: systemName,
            headerText: $"{systemName}｜{Title}",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, Title);
        AiMarkdownPdfRenderer.AddMetadata(section, BuildMetadataLines(request), Disclaimer);
        AddDetail(section, request);
        AddConversation(section, request);
        AddStackTraceAppendix(section, request);

        return AiMarkdownPdfRenderer.Render(document);
    }

    /// <summary>
    /// 報告的中介資訊行。抽成純函式，讓測試可以直接斷言內容（同 <see cref="AiReportPdfBuilder.BuildMetadataLines"/>）。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildMetadataLines(AiExceptionReportPdfRequest request)
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

        if (string.IsNullOrWhiteSpace(request.ModelName) == false)
        {
            lines.Add(new KeyValuePair<string, string>("使用模型", request.ModelName));
        }

        var followUps = request.Exchanges.Count(message => message.Role == AiChatRoles.User);
        lines.Add(new KeyValuePair<string, string>("追問輪數", followUps.ToString()));

        var usage = AiReportPdfBuilder.BuildUsageText(request.Usage);
        if (string.IsNullOrEmpty(usage) == false)
        {
            lines.Add(new KeyValuePair<string, string>("累計用量", usage));
        }

        return lines;
    }

    private static void AddDetail(Section section, AiExceptionReportPdfRequest request)
    {
        AddSectionHeading(section, "例外明細");

        foreach (var line in request.DetailLines)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.SpaceAfter = Unit.FromPoint(2);
            // 訊息與頁面可能很長且沒有空格，一律補上中文斷行機會。
            CjkLineBreak.AddTo(paragraph.Elements, $"{line.Key}：{line.Value}");
        }
    }

    private static void AddConversation(Section section, AiExceptionReportPdfRequest request)
    {
        var round = 0;
        var isFirstReply = true;

        foreach (var message in request.Exchanges)
        {
            if (message.Role == AiChatRoles.User)
            {
                round++;
                AddSectionHeading(section, $"追問 {round}");

                // 使用者的提問是純文字，不走 Markdown 渲染，照打的原樣呈現。
                var question = section.AddParagraph();
                question.Style = AiMarkdownPdfRenderer.MetaStyleName;
                question.Format.Font.Size = Unit.FromPoint(10.5);
                question.Format.SpaceAfter = Unit.FromPoint(8);
                CjkLineBreak.AddTo(question.Elements, $"問：{message.Content}");
                continue;
            }

            if (isFirstReply)
            {
                AddSectionHeading(section, "分析報告");
                isFirstReply = false;
            }

            AiMarkdownPdfRenderer.RenderMarkdown(section, message.Content);
        }
    }

    private static void AddStackTraceAppendix(Section section, AiExceptionReportPdfRequest request)
    {
        var heading = section.AddParagraph("附錄：完整堆疊");
        heading.Style = StyleNames.Heading1;
        heading.Format.PageBreakBefore = true;

        if (string.IsNullOrWhiteSpace(request.StackTrace))
        {
            section.AddParagraph("堆疊不可得（堆疊檔已被清除或當初寫檔失敗）。");
            return;
        }

        AiMarkdownPdfRenderer.AddCodeParagraph(section, request.StackTrace);
    }

    /// <summary>
    /// 報告的大章節用 Heading1：模型輸出的章節是 ## 與 ###（Heading2／3），
    /// 用 Heading1 才分得出「這是報告的結構」還是「模型寫的內容」。
    /// </summary>
    private static void AddSectionHeading(Section section, string text)
    {
        var heading = section.AddParagraph(text);
        heading.Style = StyleNames.Heading1;
    }
}
