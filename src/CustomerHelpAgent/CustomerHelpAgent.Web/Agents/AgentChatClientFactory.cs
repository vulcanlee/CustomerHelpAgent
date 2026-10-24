using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Agents;

/// <summary>
/// 建立 Microsoft Agent Framework 使用的 <see cref="IChatClient"/>，沿用 <see cref="AiSettings"/>
/// 與 <see cref="AiChatEndpoint"/> 的端點規則（Azure OpenAI v1 / OpenAI）。
/// <see cref="Create"/> 回傳的 client 已包上 <see cref="AuditingChatClient"/>：每次模型呼叫都查額度、記 Token 用量與 AI 對話紀錄。
///
/// <para>API 形式（<see cref="AgentApiStyle"/>）與推理強度在這裡轉成各自的請求欄位（<see cref="ChatOptions.RawRepresentationFactory"/>）：
/// Responses 一律 <c>store=false</c>（不用服務端儲存的對話，回應也就不帶對話 Id，MAF 會話歷史照舊留在 <c>AgentSession</c>），
/// 推理強度送 <c>reasoning.effort</c>；Chat Completions 送 <c>reasoning_effort</c>。空字串＝不送。</para>
/// </summary>
public sealed class AgentChatClientFactory
{
    private readonly IOptionsMonitor<AiSettings> aiOptions;
    private readonly IOptionsMonitor<SlowOperationSettings> slowOptions;
    private readonly ITokenUsageRecorder tokenUsageRecorder;
    private readonly IAiCallLogRecorder aiCallLogRecorder;
    private readonly IAiQuotaService quotaService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILoggerFactory loggerFactory;

    public AgentChatClientFactory(
        IOptionsMonitor<AiSettings> aiOptions,
        IOptionsMonitor<SlowOperationSettings> slowOptions,
        ITokenUsageRecorder tokenUsageRecorder,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILoggerFactory loggerFactory)
    {
        this.aiOptions = aiOptions;
        this.slowOptions = slowOptions;
        this.tokenUsageRecorder = tokenUsageRecorder;
        this.aiCallLogRecorder = aiCallLogRecorder;
        this.quotaService = quotaService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>測試可換掉，避免真的連線。</summary>
    internal Func<AiSettings, AgentApiStyle, IChatClient> InnerClientFactory { get; set; } = (s, apiStyle) => CreateOpenAiChatClient(s, apiStyle);

    public string? Validate() => AiChatEndpoint.Validate(aiOptions.CurrentValue);

    /// <summary>設定的模型（<c>AiSettings:Model</c>，Azure 為部署名稱）；錄製在回應沒有帶模型名稱時用它。</summary>
    public string ConfiguredModel => aiOptions.CurrentValue.Model;

    /// <param name="model">
    /// 改用另一個模型（Azure 為部署名稱，例如 AI 初審照片節點的 <c>AfterSalesAgentSettings.VisionModelOverride</c>，由呼叫端解析好再傳入）；
    /// null＝沿用 <c>AiSettings:Model</c>。端點、金鑰、逾時都相同，Token 用量與 AI 對話紀錄記的是實際使用的模型。
    /// </param>
    public IChatClient Create(
        string operation,
        Guid conversationId,
        AgentApiStyle apiStyle = AgentApiStyle.ChatCompletions,
        string? reasoningEffort = null,
        string? model = null)
    {
        var settings = aiOptions.CurrentValue;
        var error = AiChatEndpoint.Validate(settings);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        if (model is not null)
        {
            settings = settings.WithModel(model);
        }

        var inner = InnerClientFactory(settings, apiStyle);
        if (CreateRawOptionsFactory(apiStyle, reasoningEffort) is { } rawOptions)
        {
            // 呼叫端已自行指定 RawRepresentationFactory 時以呼叫端為準，沒有才補上。
            inner = inner.AsBuilder().ConfigureOptions(o => o.RawRepresentationFactory ??= rawOptions).Build();
        }

        return new AuditingChatClient(
            inner,
            operation,
            conversationId,
            settings,
            slowOptions,
            tokenUsageRecorder,
            aiCallLogRecorder,
            quotaService,
            auditLogService,
            currentUserService,
            loggerFactory.CreateLogger<AuditingChatClient>());
    }

    // OPENAI001：OpenAI SDK 2.14 把 Responses API 與推理強度型別標為評估版。只在這兩個方法內豁免；
    // 解除條件：SDK 移除這些型別的 [Experimental] 標記後拿掉 pragma。
#pragma warning disable OPENAI001
    internal static IChatClient CreateOpenAiChatClient(AiSettings s, AgentApiStyle apiStyle, HttpClient? httpClient = null)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(s.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = AiChatEndpoint.CreateBaseUri(s),
                NetworkTimeout = TimeSpan.FromSeconds(s.TimeoutSeconds > 0 ? s.TimeoutSeconds : 120),
                Transport = httpClient is null ? null : new HttpClientPipelineTransport(httpClient),
            });
        return apiStyle == AgentApiStyle.Responses
            ? client.GetResponsesClient().AsIChatClient(s.Model)
            : client.GetChatClient(s.Model).AsIChatClient();
    }

    /// <summary>依 API 形式產生原始請求選項；Chat Completions 又沒有推理強度時不需要（回 <c>null</c>）。</summary>
    private static Func<IChatClient, object?>? CreateRawOptionsFactory(AgentApiStyle apiStyle, string? reasoningEffort)
    {
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim().ToLowerInvariant();
        if (apiStyle == AgentApiStyle.Responses)
        {
            return _ => new CreateResponseOptions
            {
                StoredOutputEnabled = false,
                ReasoningOptions = effort is null ? null : new ResponseReasoningOptions { ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort) },
            };
        }

        return effort is null ? null : _ => new ChatCompletionOptions { ReasoningEffortLevel = new ChatReasoningEffortLevel(effort) };
    }
#pragma warning restore OPENAI001
}
