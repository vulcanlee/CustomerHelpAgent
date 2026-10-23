using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// Azure OpenAI 與 OpenAI 的位址與認證差異全部收斂在此，且刻意做成純函式
/// —— 這是兩家唯一真正不同的地方，抽出來才測得到（不必真的打 API）。
///
/// <para>Azure 走的是 <b>v1 API</b>：<c>POST {Endpoint}/chat/completions</c>
/// 搭配標頭 <c>api-key: {ApiKey}</c>，部署名稱放在請求 body 的 <c>model</c> 欄位。
/// v1 路徑採隱含版本，<b>不需要 api-version 查詢參數</b>。</para>
/// <para>OpenAI：<c>POST {Endpoint}/v1/chat/completions</c>
/// 搭配標頭 <c>Authorization: Bearer {ApiKey}</c>。</para>
///
/// <para>
/// ⚠️ 刻意<b>不</b>支援 Azure 的傳統路徑（網址含 deployments 與 api-version 的那種）。
/// v1 已是 GA 且微軟建議使用；只支援一條路徑，設定項才能少掉 Deployment 與 ApiVersion
/// 兩個。手上是舊資源的人請到 Azure 入口網站改用 v1 端點。
/// </para>
/// </summary>
public static class AiChatEndpoint
{
    /// <summary>OpenAI 的預設位址（<c>AiSettings:Endpoint</c> 留空時使用）。</summary>
    public const string DefaultOpenAiEndpoint = "https://api.openai.com";

    /// <summary>Azure v1 API 的路徑後綴。入口網站給的端點已經帶著它。</summary>
    public const string AzureV1Suffix = "/openai/v1";

    /// <summary>OpenAI 的路徑後綴。官方文件的 <c>base_url</c> 就是帶著它的形式。</summary>
    public const string OpenAiV1Suffix = "/v1";

    /// <summary>
    /// 供 OpenAI SDK 使用的 BaseUri（以 <c>/</c> 結尾，SDK 再接 <c>chat/completions</c>）。
    /// 後綴補齊規則與 <see cref="Create"/> 相同。
    /// </summary>
    public static Uri CreateBaseUri(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var provider = settings.GetProvider();
        var baseUrl = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? (provider == AiProvider.OpenAI ? DefaultOpenAiEndpoint : string.Empty)
            : settings.Endpoint.Trim().TrimEnd('/');

        return new Uri(
            EnsureSuffix(baseUrl, provider == AiProvider.OpenAI ? OpenAiV1Suffix : AzureV1Suffix) + "/");
    }

    public static AiChatEndpointDescriptor Create(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var uri = new Uri(CreateBaseUri(settings), "chat/completions");
        return settings.GetProvider() == AiProvider.OpenAI
            ? new AiChatEndpointDescriptor(uri, "Authorization", $"Bearer {settings.ApiKey}")
            : new AiChatEndpointDescriptor(uri, "api-key", settings.ApiKey);
    }

    /// <summary>
    /// 補上缺少的版本路徑後綴，讓兩種常見的貼法都能用。
    ///
    /// <para>Azure：入口網站「Azure OpenAI 端點」給的值已經以 <c>/openai/v1</c> 結尾，
    /// 但舊文件與舊習慣常常只給到資源根網址（<c>https://xxx.openai.azure.com</c>）。</para>
    /// <para>OpenAI：官方文件的 <c>base_url</c> 是 <c>https://api.openai.com/v1</c>，
    /// 但也很多人只填到網域。</para>
    ///
    /// 兩種情況少了後綴都會得到 404，而 404 的訊息看不出是路徑不完整，所以這裡補齊。
    /// </summary>
    private static string EnsureSuffix(string baseUrl, string suffix)
        => baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + suffix;

    /// <summary>
    /// 設定完整性檢查。回傳 <c>null</c> 代表可用，否則回傳給使用者看的中文原因。
    /// ⚠️ 訊息只能指出「要設哪個鍵」，絕不可回吐任何金鑰片段（有測試守門）。
    /// </summary>
    public static string? Validate(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // ⚠️ 這裡必須接住 Provider 解析失敗，不能讓例外往外跑。
        // Validate 的職責就是把壞掉的設定翻成使用者看得懂的訊息，而呼叫端
        // AiLogAnalysisService.IsAvailable 是在 LogViewerView.OnInitializedAsync 裡讀的，
        // 外面沒有 try-catch —— Provider 打錯一個字就會炸掉整個日誌檢視頁，
        // 而不只是停用 AI 按鈕。
        AiProvider provider;
        try
        {
            provider = settings.GetProvider();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return "AI 分析尚未設定 API 金鑰，請於 User Secrets 或環境變數設定 AiSettings:ApiKey。";
        }

        var isAzure = provider == AiProvider.AzureOpenAI;

        if (isAzure && string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return "AI 分析尚未設定 AiSettings:Endpoint（Azure 入口網站的「Azure OpenAI 端點」）。";
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return isAzure
                ? "AI 分析尚未設定 AiSettings:Model（Azure OpenAI 的部署名稱）。"
                : "AI 分析尚未設定 AiSettings:Model（OpenAI 模型名稱）。";
        }

        return null;
    }
}
