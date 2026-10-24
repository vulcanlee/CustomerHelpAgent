using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 替 AI 初審的模型節點（照片、歷史紀錄、摘要）組模型 client（scoped）。
///
/// <list type="bullet">
/// <item><b>回放</b>：這個案件（CaseKey＝顧客會話的 SessionKey）的顧客端執行紀錄帶有 <c>AgentRunTrace.ReplayRecordingId</c>，
/// 就改用 <see cref="ReplayChatClient"/> 依同一份錄製、該節點的 stream 逐筆重播，<b>完全不碰</b> <see cref="AgentChatClientFactory"/>
/// （不需金鑰、不查額度、不記用量）；並把錄製 Id 記到 <c>PreReviewRun.ReplayRecordingId</c>。回應用完或摘要對不上時拋例外，
/// 節點照常改為 Failed 結論，Workflow 仍走到人工決策。</item>
/// <item><b>即時</b>：<see cref="AgentChatClientFactory.Create"/>（作業 <see cref="TokenUsageOperations.AfterSalesPreReview"/>、
/// 對話識別碼＝CaseKey、API 形式與推理強度沿用 <see cref="AfterSalesAgentSettings"/>；照片節點改用 <see cref="AfterSalesAgentSettings.VisionModel"/>）。
/// 示範模式開啟且為即時模式時外面再包 <see cref="RecordingChatClient"/>，記在顧客會話的同一份錄製、該節點自己的 stream
/// （這個會話沒有錄製時 <c>AppendCallAsync</c> 只記 Warning 並略過）。</item>
/// <item><b>回放模式但案件沒有錄製</b>（展示控制台為回放，案件卻不是回放建立的）：拋例外、節點改為 Failed —— 回放中絕不呼叫真實模型。</item>
/// </list>
///
/// ⚠️ 刻意<b>不</b>包 <see cref="TracingChatClient"/>：它把每次模型呼叫寫成顧客那一輪執行的軌跡步驟，
/// 而顧客重新整理頁面時以會話的軌跡（<c>AgentTraceService.GetSessionStepsAsync</c>）還原對話與幕後面板 ——
/// 初審的提示內容會因此出現在顧客畫面上。初審節點的狀態改由 <c>PreReviewNodeState</c> 呈現。
/// </summary>
public sealed class PreReviewChatClientProvider
{
    /// <summary>結構化輸出的 JSON 形狀：camelCase（schema 的欄位名稱與提示詞一致）、讀取不分大小寫。</summary>
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>回放模式下，這個案件沒有可重播的錄製（節點改為 Failed，不呼叫真實模型）。</summary>
    internal const string ReplayWithoutRecordingMessage = "Replay mode is on but this case has no recording to replay; the model is not called.";

    private readonly AgentChatClientFactory chatClientFactory;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly AgentRecordingService recordingService;
    private readonly AgentTraceService traceService;
    private readonly ReplayCursorStore replayCursors;
    private readonly PreReviewRunService runs;
    private readonly DemoControlService demoControl;
    private readonly IOptionsMonitor<AfterSalesAgentSettings> settings;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<PreReviewChatClientProvider> logger;

    public PreReviewChatClientProvider(
        AgentChatClientFactory chatClientFactory,
        IAiSystemPromptProvider promptProvider,
        AgentRecordingService recordingService,
        AgentTraceService traceService,
        ReplayCursorStore replayCursors,
        PreReviewRunService runs,
        DemoControlService demoControl,
        IOptionsMonitor<AfterSalesAgentSettings> settings,
        ILoggerFactory loggerFactory)
    {
        this.chatClientFactory = chatClientFactory;
        this.promptProvider = promptProvider;
        this.recordingService = recordingService;
        this.traceService = traceService;
        this.replayCursors = replayCursors;
        this.runs = runs;
        this.demoControl = demoControl;
        this.settings = settings;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<PreReviewChatClientProvider>();
    }

    /// <summary>
    /// 以 <see cref="ChatClientAgent"/>（無工具）執行一次結構化輸出（<c>RunAsync&lt;T&gt;</c>，JSON schema 依 <typeparamref name="T"/> 產生），
    /// 系統指示取 <paramref name="promptKey"/> 作用中的版本（或內建預設）＋固定規則。回應不是合法的 JSON 或是 null 時拋例外，由節點改為 Failed 結論。
    /// </summary>
    public async Task<T> RunStructuredAsync<T>(
        PreReviewCase reviewCase, string stream, bool vision, string promptKey, ChatMessage message, CancellationToken cancellationToken)
    {
        var client = await CreateAsync(reviewCase, stream, vision);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = await promptProvider.GetAsync(promptKey, cancellationToken),
                // 預設不送：推論模型不接受 temperature（AfterSalesAgentSettings.Temperature）。
                Temperature = (float?)settings.CurrentValue.Temperature,
            },
        }, loggerFactory);
        var response = await agent.RunAsync<T>(message, serializerOptions: OutputJsonOptions, cancellationToken: cancellationToken);
        return response.Result ?? throw new InvalidDataException("The structured output of the pre-review model is empty.");
    }

    /// <param name="stream">節點的錄製 stream（<see cref="AgentRecordingStreams"/> 的 <c>prereview:*</c>）。</param>
    /// <param name="vision">照片節點：即時模式改用 <see cref="AfterSalesAgentSettings.VisionModel"/>（空字串沿用 <c>AiSettings:Model</c>）。</param>
    private async Task<IChatClient> CreateAsync(PreReviewCase reviewCase, string stream, bool vision)
    {
        if (await traceService.GetReplayRecordingIdAsync(reviewCase.CaseKey) is { } recordingId)
        {
            await runs.SetReplayRecordingAsync(reviewCase.RunId, recordingId);
            logger.LogInformation(
                "Pre-review node replays a recording instead of calling the model. RunId={RunId}, RecordingId={RecordingId}, Stream={Stream}",
                reviewCase.RunId, recordingId, stream);
            return new ReplayChatClient(
                await recordingService.GetCallsAsync(recordingId, stream),
                replayCursors.Get(reviewCase.CaseKey, recordingId, stream),
                TimeSpan.Zero,
                loggerFactory.CreateLogger<ReplayChatClient>());
        }

        // 回放模式絕不呼叫真實模型：這個案件沒有可重播的錄製（在回放模式下由客服按「重新初審」、重啟續跑或補建的案件，
        // 其顧客端執行不是回放）→ 拋例外，節點照常改為 Failed 結論，Workflow 仍走到人工決策。
        if (demoControl.IsEnabled && demoControl.Mode == DemoMode.Replay)
        {
            logger.LogWarning(
                "Pre-review node failed because replay mode is on but the case has no recording to replay. RunId={RunId}, Stream={Stream}",
                reviewCase.RunId, stream);
            throw new InvalidOperationException(ReplayWithoutRecordingMessage);
        }

        var options = settings.CurrentValue;
        var model = vision ? options.VisionModelOverride : null;
        var client = chatClientFactory.Create(
            TokenUsageOperations.AfterSalesPreReview,
            reviewCase.CaseKey,
            options.GetApiStyle(),
            options.ReasoningEffort,
            model);
        if (demoControl.IsEnabled && demoControl.Mode == DemoMode.Live)
        {
            client = new RecordingChatClient(
                client,
                reviewCase.CaseKey,
                model ?? chatClientFactory.ConfiguredModel,
                recordingService,
                loggerFactory.CreateLogger<RecordingChatClient>(),
                stream);
        }

        return client;
    }
}
