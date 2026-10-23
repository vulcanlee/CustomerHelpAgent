namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// AI 分析設定（日誌檢視頁的「AI 分析」功能）。
///
/// ⚠️ <see cref="ApiKey"/> 在 appsettings.json 一律留空字串當範本，實際值請放
/// User Secrets、環境變數（鍵名 <c>AiSettings__ApiKey</c>）或 appsettings.Development.json，
/// 與 GoogleOAuthSettings 的既有作法一致。
/// </summary>
public class AiSettings
{
    public const string SectionName = "AiSettings";

    /// <summary>
    /// 服務供應商：<c>AzureOpenAI</c> 或 <c>OpenAI</c>。
    /// 刻意存字串再由 <see cref="GetProvider"/> 解析（照 CacheSettings 的既有慣例），
    /// 這樣值打錯時丟的是可控的中文訊息，而不是組態繫結的框架英文例外。
    /// </summary>
    public string Provider { get; set; } = nameof(AiProvider.AzureOpenAI);

    /// <summary>
    /// Azure OpenAI：Azure 入口網站「Azure OpenAI 端點」欄位的值，例如
    /// <c>https://your-resource.openai.azure.com/openai/v1</c>（必填）。
    /// ⚠️ 不要填成 Foundry 的「專案端點」（網址含 <c>/api/projects/</c>），那是給 Agent SDK 用的。
    /// OpenAI：留空即使用 <see cref="Ai.AiChatEndpoint.DefaultOpenAiEndpoint"/>。
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API 金鑰。⚠️ 見類別註解，不要寫進 appsettings.json。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 要呼叫的模型，<b>必填</b>。
    /// <b>Azure OpenAI 請填「部署名稱」</b>（Azure 入口網站 Deployments 那一頁看到的名稱），
    /// 不是模型名稱；OpenAI 則填模型 id（例如 <c>gpt-4o-mini</c>）。
    /// 兩者都是送進請求 body 的 <c>model</c> 欄位，所以共用同一個設定。
    ///
    /// ⚠️ 刻意沒有預設值。預設供應商是 Azure，而任何模型名稱拿去當部署名稱送出去都只會
    /// 換來 404；留空才能在按下按鈕之前就用 Tooltip 告訴使用者少填了什麼。
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>送出給 AI 的日誌筆數上限。超過時取最新的 N 筆。</summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>
    /// AI 例外分析的追問輪數上限（0.9.68 起；不含第一次的分析報告）。0 或負數代表不開放追問。
    ///
    /// 每一輪追問都會帶上整段前文，輸入量與費用隨輪數累加 —— 這個上限就是單次對話的費用天花板。
    /// </summary>
    public int MaxFollowUpRounds { get; set; } = 10;

    /// <summary>
    /// HTTP 逾時秒數。
    ///
    /// 預設 10 分鐘，遠高於一般 API：推論模型對上百筆日誌可能想很久，
    /// 而「要等多久」也是部署環境之間差異最大、最可能需要現場調整的一項，
    /// 所以它是少數仍寫在 appsettings.json 裡的欄位。
    ///
    /// ⚠️ 已知取捨：等待期間 AI 分析按鈕停用且沒有取消機制，上游若卡住，
    /// 使用者最久會被困滿這個秒數。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 回應長度上限（送出的 <c>max_completion_tokens</c>）。
    /// <c>null</c>（預設）代表整個欄位都不送，由模型自己決定。
    ///
    /// ⚠️ 預設不送是刻意的：這個額度<b>同時涵蓋推論模型的思考 token</b>，
    /// 設太小會在模型產出任何可見文字之前就耗盡 —— 拿到空回應，而輸入與思考的費用照付。
    /// 微軟建議為推論與輸出至少保留 25000。不送就能相容所有模型，要控成本的人再自己填。
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// 取樣溫度。<c>null</c>（預設）代表整個欄位都不送，由模型自己決定。
    ///
    /// ⚠️ 預設不送是刻意的：推論模型（o 系列、gpt-5 家族）只接受預設值，送任何數字都會
    /// 被回 400 <c>unsupported_value</c>。不送就能相容所有模型，要調的人再自己填。
    ///
    /// ⚠️ 要停用請設 <c>null</c>，**不要**設空字串：空字串會讓組態繫結在第一次讀取
    /// 設定時（也就是使用者按下按鈕時）才丟例外，而不是啟動時。
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>把 <see cref="Provider"/> 字串解析成列舉。空字串視為 Azure OpenAI。</summary>
    /// <exception cref="InvalidOperationException">值不是支援的供應商名稱。</exception>
    public AiProvider GetProvider()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            return AiProvider.AzureOpenAI;
        }

        if (Enum.TryParse<AiProvider>(Provider, ignoreCase: true, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"不支援的 AI provider：{Provider}");
    }
}

public enum AiProvider
{
    AzureOpenAI,
    OpenAI
}
