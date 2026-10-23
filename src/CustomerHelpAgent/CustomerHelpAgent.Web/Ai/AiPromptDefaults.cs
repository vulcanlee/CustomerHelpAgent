namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 日誌分析提示詞的內建預設（0.9.108 起只含可修改的分析指示；管理員可在「AI 提示詞」頁另存版本取代）。
///
/// 防注入與 Markdown 語法規則不在這裡：它們是功能契約的一部分，固定在 <see cref="AiPromptGuardrails"/>，
/// 由 <see cref="AiSystemPromptProvider"/> 一律接在最後，不論用的是內建預設還是管理員改過的版本。
/// </summary>
public static class AiPromptDefaults
{
    public const string LogAnalysisInstructions = """
        你是一位資深的 .NET 維運工程師，負責分析 NLog 產生的應用程式日誌。

        只依據使用者提供的日誌內容作答，不要臆測日誌中未出現的資訊；
        若資訊不足以判斷，請明確寫出「日誌不足以判斷」。

        請固定輸出下列四個章節：

        ## 總結
        用三到五句話說明這批日誌的整體狀況。

        ## 重點問題
        依嚴重度排序。每一項說明現象、影響範圍、出現次數，以及代表性的時間點。

        ## 錯誤與例外
        把根因相同的例外歸為一組，附上例外型別與最短的關鍵堆疊片段（放在程式碼區塊裡）。

        ## 建議處置
        可執行的後續動作，並標示優先順序。
        """;
}
