using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 每一次模型呼叫前發出 <see cref="AgentStepKind.ModelContext"/> 事件（送了什麼、刻意沒送什麼），並計算本輪的模型呼叫次數。
/// 位置：<c>FunctionInvokingChatClient</c> → <b>這裡</b> → <see cref="AuditingChatClient"/> → 模型，
/// 所以函式呼叫迴圈裡的每一回都會經過，看到的是實際送出的訊息。
///
/// <see cref="ForcedFinalCall"/>：<c>FunctionInvokingChatClient</c> 用完 <c>MaximumIterationsPerRequest</c> 後，
/// 會再呼叫模型一次並拿掉所有工具（實測 MEAI 10.10），Runner 據此判定「步數上限」。
///
/// 每輪總量上限（<c>turnBudget</c>）：<c>MaximumIterationsPerRequest</c> 只管單次 <c>RunStreamingAsync</c>；
/// 確認請求被程式拒絕後 Runner 會再跑一次，步數就重新計算。這裡跨子回合累計，再呼叫就超過上限時拋
/// <see cref="AgentTurnBudgetExceededException"/>（不送出、不計數）。被額度擋下（<see cref="AiQuotaExceededException"/>）的呼叫也不計入。
///
/// 回放（1.0.5 起，<c>replayRecordingId</c> 有值）：底層是 <see cref="ReplayChatClient"/>，沒有呼叫模型，白話層改寫
/// 「回放：使用錄製 #N 的回應（未呼叫模型）…」，不寫「第 N 次呼叫模型」，幕後面板與 /agent-runs 才分得出回放。
/// </summary>
internal sealed class TracingChatClient : DelegatingChatClient
{
    /// <summary>白話層一律列出的「刻意不送給模型」的資料。</summary>
    internal const string WithheldText = "使用者 Id、收件人姓名、地址、手機、付款資訊";

    private static readonly JsonSerializerOptions RawJsonOptions = new(AIJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<AgentTraceEvent, Task> emit;
    private readonly int turnBudget;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly int? replayRecordingId;

    /// <param name="turnBudget">本輪最多呼叫模型幾次（跨所有子回合）；Runner 傳入 <c>MaxSteps + 1</c>。</param>
    /// <param name="replayRecordingId">回放中這一輪用的錄製；即時執行為 null。</param>
    public TracingChatClient(
        IChatClient innerClient,
        Func<AgentTraceEvent, Task> emit,
        int turnBudget,
        TimeProvider timeProvider,
        ILogger logger,
        int? replayRecordingId = null)
        : base(innerClient)
    {
        this.emit = emit;
        this.turnBudget = turnBudget;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.replayRecordingId = replayRecordingId;
    }

    /// <summary>本輪實際送出的模型呼叫次數（不含被每輪上限或額度擋下的呼叫）。</summary>
    public int ModelCallCount { get; private set; }

    /// <summary>本輪是否有一次呼叫是在沒有提供任何工具的情況下送出（＝步數已用完的最後一次呼叫）。</summary>
    public bool ForcedFinalCall { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        await BeforeCallAsync(list, options);
        try
        {
            return await base.GetResponseAsync(list, options, cancellationToken);
        }
        catch (AiQuotaExceededException)
        {
            ModelCallCount--;
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        await BeforeCallAsync(list, options);
        await using var enumerator = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            // yield 不能放在有 catch 的 try 裡，所以只包「取下一段」這一步；額度在第一段之前就會擋下。
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (AiQuotaExceededException)
            {
                ModelCallCount--;
                throw;
            }

            if (!moved)
            {
                break;
            }

            yield return enumerator.Current;
        }
    }

    private async Task BeforeCallAsync(IList<ChatMessage> messages, ChatOptions? options)
    {
        if (ModelCallCount >= turnBudget)
        {
            logger.LogInformation("After-sales agent turn reached its model-call budget. Budget={Budget}", turnBudget);
            throw new AgentTurnBudgetExceededException(turnBudget);
        }

        ModelCallCount++;
        var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
        if (tools.Count == 0)
        {
            ForcedFinalCall = true;
        }

        var toolResults = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        var plain = replayRecordingId is { } recordingId
            ? $"回放：使用錄製 #{recordingId} 的回應（未呼叫模型）；比對 {messages.Count} 則對話訊息（其中 {toolResults} 筆工具結果）"
                + (tools.Count == 0 ? "，這次不提供工具（步數已用完）" : $"與 {tools.Count} 個工具說明") + "。"
            : $"第 {ModelCallCount} 次呼叫模型：送出系統指示、{messages.Count} 則對話訊息（其中 {toolResults} 筆工具結果）"
                + (tools.Count == 0 ? "，這次不提供工具（步數已用完，要求模型直接回覆）" : $"與 {tools.Count} 個工具說明")
                + $"。刻意不送：{WithheldText}。";
        await emit(new AgentTraceEvent(
            AgentStepKind.ModelContext,
            AgentDataSource.Conversation,
            plain,
            Snapshot(new { tools, messages }),
            timeProvider.GetLocalNow().DateTime));
    }

    /// <summary>序列化失敗（例如未登錄的 AIContent 子類）改存錯誤型別，不影響模型呼叫。</summary>
    private string Snapshot(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, RawJsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to serialize the agent model context snapshot.");
            return JsonSerializer.Serialize(new { snapshotError = ex.GetType().Name });
        }
    }
}
