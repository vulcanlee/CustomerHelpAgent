using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Agents.StaffAssistant;

/// <summary>
/// 一段客服 AI 助手對話（只存在記憶體：審核窗內的面板持有它，關窗即丟棄，不寫資料庫、不寫執行軌跡）。
/// 綁定的申請與客服在 <see cref="StaffAssistantRunner.StartSession"/> 決定；<see cref="ConversationId"/> 每次開啟各自產生，
/// 與顧客會話（CaseKey）分開，Token 用量與 AI 對話紀錄以它串起同一段對話。
/// </summary>
public sealed class StaffAssistantSession
{
    internal StaffAssistantSession(int requestId, int staffUserId)
    {
        RequestId = requestId;
        StaffUserId = staffUserId;
    }

    public int RequestId { get; }

    public Guid ConversationId { get; } = Guid.NewGuid();

    internal int StaffUserId { get; }

    /// <summary>MAF 的對話歷史（第一次提問時建立，之後每一輪沿用）。</summary>
    internal AgentSession? AgentSession { get; set; }
}

/// <summary>
/// 一輪回覆。<see cref="IsProgramMessage"/> 為 true 時 <see cref="Text"/> 是程式的固定說明（停用、額度、錯誤），不是模型文字；
/// <see cref="ToolTags"/> 是這一輪查過的工具（「查了：規則 v1」），顯示在回覆上方。
/// </summary>
public sealed record StaffAssistantReply(string Text, bool IsProgramMessage, IReadOnlyList<string> ToolTags);

/// <summary>
/// 客服 AI 助手的執行底座（scoped，跑在客服自己的 Blazor circuit：<c>CurrentUser</c> 就是客服，額度與用量記在他身上）。
///
/// 組裝：<see cref="AgentChatClientFactory.Create"/>（作業 <see cref="TokenUsageOperations.AfterSalesStaffAssistant"/>、
/// 對話識別碼＝<see cref="StaffAssistantSession.ConversationId"/>，含 <see cref="AuditingChatClient"/>）→ <c>UseFunctionInvocation</c>
/// （<see cref="MaxSteps"/> 次往返）→ <see cref="ChatClientAgent"/>（6 個只讀工具，<see cref="StaffAssistantTools"/>）→ 函式中介層（回報工具標籤）。
/// 系統指示取 <see cref="PromptTemplateKeys.AfterSalesStaffAssistant"/>（作用中版本或 <see cref="StaffAssistantPromptDefaults"/>）＋固定規則，每輪重新讀取。
///
/// ⚠️ 刻意<b>不</b>包 <see cref="TracingChatClient"/> 也不錄製：軌跡寫在顧客的 SessionKey 底下會出現在顧客還原的對話裡；
/// 錄製串流屬於顧客會話。回放模式一律停用（客服提問每次不同，沒有可重播的錄製）。
/// </summary>
public sealed class StaffAssistantRunner
{
    /// <summary>一輪最多幾次「模型回應 → 執行工具」往返。</summary>
    public const int MaxSteps = 6;

    public const string ReplayDisabledText = "回放模式不提供客服 AI 助手（需即時模型）";
    public const string AiNotConfiguredText = "AI 服務尚未設定，請洽管理員。";
    public const string ModelErrorText = "AI 服務暫時發生問題，這次沒有回覆，請稍後再試。";
    public const string EmptyReplyText = "這次沒有得到回覆，請換個問法再試一次。";

    private readonly AgentChatClientFactory chatClientFactory;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly AfterSalesRequestService requests;
    private readonly AfterSalesPolicyService policies;
    private readonly ShopOrderService orders;
    private readonly PreReviewRunService preReviews;
    private readonly HandoverSummaryBuilder handover;
    private readonly IPermissionChecker permissionChecker;
    private readonly CurrentUserService currentUserService;
    private readonly DemoControlService demoControl;
    private readonly IOptionsMonitor<AfterSalesAgentSettings> settings;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<StaffAssistantRunner> logger;

    public StaffAssistantRunner(
        AgentChatClientFactory chatClientFactory,
        IAiSystemPromptProvider promptProvider,
        AfterSalesRequestService requests,
        AfterSalesPolicyService policies,
        ShopOrderService orders,
        PreReviewRunService preReviews,
        HandoverSummaryBuilder handover,
        IPermissionChecker permissionChecker,
        CurrentUserService currentUserService,
        DemoControlService demoControl,
        IOptionsMonitor<AfterSalesAgentSettings> settings,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        this.chatClientFactory = chatClientFactory;
        this.promptProvider = promptProvider;
        this.requests = requests;
        this.policies = policies;
        this.orders = orders;
        this.preReviews = preReviews;
        this.handover = handover;
        this.permissionChecker = permissionChecker;
        this.currentUserService = currentUserService;
        this.demoControl = demoControl;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<StaffAssistantRunner>();
    }

    /// <summary>目前不能使用助手的原因（回放模式、AI 未設定）；可以使用時為 null。每次提問前也會再檢查一次。</summary>
    public string? UnavailableReason
        => demoControl.Mode == DemoMode.Replay ? ReplayDisabledText
        : chatClientFactory.Validate() is not null ? AiNotConfiguredText
        : null;

    /// <summary>開始一段對話：案件綁定為 <paramref name="requestId"/>，客服為目前登入使用者。</summary>
    public StaffAssistantSession StartSession(int requestId) => new(requestId, currentUserService.CurrentUser.Id);

    /// <param name="onTextDelta">串流的文字增量（預覽用；最後以回傳的 <see cref="StaffAssistantReply.Text"/> 為準）。</param>
    /// <param name="onToolCall">每次工具呼叫完成時的標籤（「查了：規則 v1」）。</param>
    /// <exception cref="OperationCanceledException">呼叫端取消（例如關閉審核窗）。</exception>
    public async Task<StaffAssistantReply> AskAsync(
        StaffAssistantSession session,
        string question,
        Func<string, Task> onTextDelta,
        Func<string, Task> onToolCall,
        CancellationToken cancellationToken)
    {
        if (UnavailableReason is { } unavailable)
        {
            logger.LogInformation("Staff assistant question refused because the assistant is unavailable. RequestId={RequestId}", session.RequestId);
            return new StaffAssistantReply(unavailable, true, []);
        }

        var tags = new List<string>();
        try
        {
            var agent = await BuildAgentAsync(session, async tag =>
            {
                tags.Add(tag);
                await onToolCall(tag);
            }, cancellationToken);
            session.AgentSession ??= await agent.CreateSessionAsync(cancellationToken);

            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, question), session.AgentSession, cancellationToken: cancellationToken))
            {
                updates.Add(update);
                if (!string.IsNullOrEmpty(update.Text))
                {
                    await onTextDelta(update.Text);
                }
            }

            var text = updates.ToAgentResponse().Text.Trim();
            logger.LogInformation(
                "Staff assistant turn finished. RequestId={RequestId}, ToolCalls={ToolCalls}, Empty={Empty}",
                session.RequestId, tags.Count, text.Length == 0);
            return text.Length == 0
                ? new StaffAssistantReply(EmptyReplyText, true, tags)
                : new StaffAssistantReply(text, false, tags);
        }
        catch (AiQuotaExceededException ex)
        {
            logger.LogInformation("Staff assistant turn stopped by the AI quota. RequestId={RequestId}", session.RequestId);
            return new StaffAssistantReply(ex.Message, true, tags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Staff assistant turn failed. RequestId={RequestId}", session.RequestId);
            return new StaffAssistantReply(ModelErrorText, true, tags);
        }
    }

    private async Task<AIAgent> BuildAgentAsync(StaffAssistantSession session, Func<string, Task> onToolCall, CancellationToken ct)
    {
        var options = settings.CurrentValue;
        var tools = new StaffAssistantTools(
            requests,
            policies,
            orders,
            preReviews,
            handover,
            permissionChecker,
            currentUserService,
            timeProvider,
            loggerFactory.CreateLogger<StaffAssistantTools>(),
            new StaffAssistantToolContext
            {
                RequestId = session.RequestId,
                StaffUserId = session.StaffUserId,
                ToolTimeout = TimeSpan.FromSeconds(options.ToolTimeoutSeconds),
            });

        var chatClient = chatClientFactory
            .Create(TokenUsageOperations.AfterSalesStaffAssistant, session.ConversationId, options.GetApiStyle(), options.ReasoningEffort)
            .AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = MaxSteps;
                f.MaximumConsecutiveErrorsPerRequest = options.MaxConsecutiveToolFailures;
            })
            .Build();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = await promptProvider.GetAsync(PromptTemplateKeys.AfterSalesStaffAssistant, ct),
                Tools = [.. tools.CreateFunctions()],
                // 預設不送：推論模型不接受 temperature（AfterSalesAgentSettings.Temperature）。
                Temperature = (float?)options.Temperature,
            },
        }, loggerFactory);

        return agent.AsBuilder().Use(async (_, context, next, token) =>
        {
            var result = await next(context, token);
            if (ToolOutcome.TryParse(result, out var outcome))
            {
                await onToolCall(StaffAssistantTools.DescribeCall(context.Function.Name, outcome));
            }

            return result;
        }).Build();
    }
}
