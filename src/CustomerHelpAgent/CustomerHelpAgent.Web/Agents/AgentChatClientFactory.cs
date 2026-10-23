using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Agents;

/// <summary>
/// 建立 Microsoft Agent Framework 使用的 <see cref="IChatClient"/>，沿用 <see cref="AiSettings"/>
/// 與 <see cref="AiChatEndpoint"/> 的端點規則（Azure OpenAI v1 / OpenAI）。
/// <see cref="Create"/> 回傳的 client 已包上 <see cref="AuditingChatClient"/>：每次模型呼叫都查額度、記 Token 用量與 AI 對話紀錄。
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
    internal Func<AiSettings, IChatClient> InnerClientFactory { get; set; } = s => CreateOpenAiChatClient(s);

    public string? Validate() => AiChatEndpoint.Validate(aiOptions.CurrentValue);

    public IChatClient Create(string operation, Guid conversationId)
    {
        var settings = aiOptions.CurrentValue;
        var error = AiChatEndpoint.Validate(settings);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        return new AuditingChatClient(
            InnerClientFactory(settings),
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

    internal static IChatClient CreateOpenAiChatClient(AiSettings s, HttpClient? httpClient = null)
        => new OpenAIClient(
                new ApiKeyCredential(s.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = AiChatEndpoint.CreateBaseUri(s),
                    NetworkTimeout = TimeSpan.FromSeconds(s.TimeoutSeconds > 0 ? s.TimeoutSeconds : 120),
                    Transport = httpClient is null ? null : new HttpClientPipelineTransport(httpClient),
                })
            .GetChatClient(s.Model)
            .AsIChatClient();
}
