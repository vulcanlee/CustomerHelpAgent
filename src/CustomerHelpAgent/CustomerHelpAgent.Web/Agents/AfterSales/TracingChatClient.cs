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
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    public TracingChatClient(IChatClient innerClient, Func<AgentTraceEvent, Task> emit, TimeProvider timeProvider, ILogger logger)
        : base(innerClient)
    {
        this.emit = emit;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public int ModelCallCount { get; private set; }

    /// <summary>本輪是否有一次呼叫是在沒有提供任何工具的情況下送出（＝步數已用完的最後一次呼叫）。</summary>
    public bool ForcedFinalCall { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        await BeforeCallAsync(list, options);
        return await base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        await BeforeCallAsync(list, options);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return update;
        }
    }

    private async Task BeforeCallAsync(IList<ChatMessage> messages, ChatOptions? options)
    {
        ModelCallCount++;
        var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
        if (tools.Count == 0)
        {
            ForcedFinalCall = true;
        }

        var toolResults = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        var plain = $"第 {ModelCallCount} 次呼叫模型：送出系統指示、{messages.Count} 則對話訊息（其中 {toolResults} 筆工具結果）"
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
