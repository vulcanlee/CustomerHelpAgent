using MigraDoc.DocumentObjectModel;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// AI 對話紀錄 PDF 的輸入（0.9.72 起）。
///
/// ⚠️ 刻意沒有任何原始 JSON 欄位：PDF 只放中繼資料與完整對話，原始內容請下載 JSON。
/// </summary>
public sealed record AiCallLogPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public AiCallLogAdapterModel Item { get; init; } = new();
    public IReadOnlyList<AiCallLogMessage> Messages { get; init; } = [];

    /// <summary>取得的回應文字；失敗時為空字串。</summary>
    public string ResponseText { get; init; } = string.Empty;

    public TokenUsageLogAdapterModel? Usage { get; init; }
}

/// <summary>
/// 把一筆 AI 對話紀錄排成 PDF（0.9.72 起）。版面、樣式與 Markdown 渲染器共用
/// <see cref="AiMarkdownPdfRenderer"/>，與 <see cref="AiExceptionReportPdfBuilder"/> 一致。
///
/// system／user 訊息是送出的原文，以程式碼段落保留空白；assistant 內容是模型產出，經
/// <see cref="AiMarkdownPdfRenderer.RenderMarkdown"/>（已消毒）排版。
/// </summary>
public static class AiCallLogPdfBuilder
{
    /// <summary>單段內容的上限；超過截斷並註明（日誌分析的提示詞可能是整份日誌）。</summary>
    public const int MaxPdfBlockCharacters = 200_000;

    private const string Title = "AI 對話紀錄";
    private const string Disclaimer = "本文件包含實際送出給 AI 的內容與 AI 的回應，可能含日誌、例外堆疊或使用者帳號，請依內部規範保管。";

    /// <summary>角色的顯示名稱（畫面與 PDF 共用）。</summary>
    public static string RoleLabel(string role) => role switch
    {
        AiChatRoles.System => "系統提示（System）",
        AiChatRoles.User => "使用者（User）",
        AiChatRoles.Assistant => "AI 助理（Assistant）",
        _ => string.IsNullOrWhiteSpace(role) ? "（未標示角色）" : role,
    };

    /// <summary>產生 PDF 位元組。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(AiCallLogPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "CustomerHelpAgent" : request.SystemName;
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: Title,
            subject: request.Item.Operation,
            author: systemName,
            headerText: $"{systemName}｜{Title}｜{request.Item.Operation}",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, Title);
        AiMarkdownPdfRenderer.AddMetadata(section, BuildMetadataLines(request), Disclaimer);
        AddConversation(section, request);

        return AiMarkdownPdfRenderer.Render(document);
    }

    /// <summary>中介資訊行。抽成純函式讓測試直接斷言（方案內沒有 PDF 文字抽取相依）。</summary>
    public static List<KeyValuePair<string, string>> BuildMetadataLines(AiCallLogPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = request.Item;
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
            lines.Add(new KeyValuePair<string, string>("匯出者", request.OperatorAccount));
        }

        lines.Add(new KeyValuePair<string, string>("送出時間", $"{item.OccurredAt:yyyy-MM-dd HH:mm:ss}"));
        lines.Add(new KeyValuePair<string, string>("作業", item.Operation));
        lines.Add(new KeyValuePair<string, string>("發起者", string.IsNullOrWhiteSpace(item.Account) ? "（系統自動）" : item.Account));
        lines.Add(new KeyValuePair<string, string>("供應商／模型", $"{item.Provider}／{item.Model}"));
        lines.Add(new KeyValuePair<string, string>("結果", DescribeOutcome(item)));
        lines.Add(new KeyValuePair<string, string>("耗時", $"{item.ElapsedMilliseconds:N0} ms"));

        if (string.IsNullOrWhiteSpace(item.RelatedInfo) == false)
        {
            lines.Add(new KeyValuePair<string, string>("關聯說明", item.RelatedInfo));
        }

        if (request.Usage is { } usage)
        {
            var cost = usage.IsUnpriced ? "，未定價" : $"，約 NT$ {TokenUsageFormat.CostTwdCell(usage.CostTwd)}";
            lines.Add(new KeyValuePair<string, string>(
                "用量",
                $"輸入 {usage.InputCount?.ToString("N0") ?? "—"}／輸出 {usage.OutputCount?.ToString("N0") ?? "—"}{cost}"));
        }

        lines.Add(new KeyValuePair<string, string>("呼叫識別碼", item.CallId.ToString()));
        return lines;
    }

    /// <summary>結果文字（畫面與 PDF 共用）。</summary>
    public static string DescribeOutcome(AiCallLogAdapterModel item)
    {
        if (item.Success)
        {
            return "成功";
        }

        if (item.IsCanceled)
        {
            return "已取消";
        }

        return string.IsNullOrWhiteSpace(item.FailureReason) ? "失敗" : $"失敗（{item.FailureReason}）";
    }

    private static void AddConversation(Section section, AiCallLogPdfRequest request)
    {
        var number = 0;
        foreach (var message in request.Messages)
        {
            number++;
            AddSectionHeading(section, $"#{number} {RoleLabel(message.Role)}．送出");
            AddContent(section, message.Content, asMarkdown: message.Role == AiChatRoles.Assistant);
        }

        AddSectionHeading(section, "AI 回應（Assistant）．取得");

        if (string.IsNullOrWhiteSpace(request.ResponseText))
        {
            var empty = section.AddParagraph();
            empty.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(empty.Elements, $"未取得內容（{DescribeOutcome(request.Item)}）。");
            return;
        }

        AddContent(section, request.ResponseText, asMarkdown: true);
    }

    /// <summary>
    /// 報告的大章節用 Heading1：模型輸出的章節是 ## 與 ###（Heading2／3），
    /// 用 Heading1 才分得出「這是報告的結構」還是「模型寫的內容」（同 <see cref="AiExceptionReportPdfBuilder"/>）。
    /// </summary>
    private static void AddSectionHeading(Section section, string text)
    {
        var heading = section.AddParagraph(text);
        heading.Style = StyleNames.Heading1;
    }

    private static void AddContent(Section section, string content, bool asMarkdown)
    {
        var truncated = content.Length > MaxPdfBlockCharacters;
        var text = truncated ? content[..MaxPdfBlockCharacters] : content;

        if (asMarkdown)
        {
            AiMarkdownPdfRenderer.RenderMarkdown(section, text);
        }
        else
        {
            AiMarkdownPdfRenderer.AddCodeParagraph(section, text);
        }

        if (truncated)
        {
            var note = section.AddParagraph();
            note.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(
                note.Elements,
                $"（本段共 {content.Length:N0} 字元，PDF 只收錄前 {MaxPdfBlockCharacters:N0} 字元；完整內容請於畫面下載 JSON。）");
        }
    }
}
