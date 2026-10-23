using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 錄製每一次成功的模型呼叫（1.0.5 起）：請求摘要（<see cref="ModelRequestDigest"/>）＋完整回應（<see cref="ChatResponse"/> JSON，
/// <see cref="AIJsonUtilities.DefaultOptions"/>）寫入 <see cref="AgentRecordingService.AppendCallAsync"/>，回放（Task 6）據此重播。
///
/// <para>位置：<see cref="TracingChatClient"/> → <b>這裡</b> → <c>AgentChatClientFactory.Create</c> 的回傳值（<see cref="AuditingChatClient"/> → 模型），
/// 看到的是實際送給模型的訊息；回放時同一位置換成回放用的 client，摘要才比得起來。
/// 只在示範模式開啟且為即時模式時由 Runner 插入（每輪判定一次）。</para>
///
/// <para>只記成功的回應：失敗、取消、或呼叫端中途放棄串流都不記。串流呼叫照原樣逐段轉交，不改內容；
/// 全部取完後才合併（<c>ToChatResponse</c>）並寫入，所以只在最後一段之後多一次資料庫寫入。
/// 錄製失敗（含合併、序列化、寫入）只記 Warning，不影響對話。</para>
/// </summary>
internal sealed class RecordingChatClient : DelegatingChatClient
{
    private readonly Guid sessionKey;
    private readonly string? configuredModel;
    private readonly AgentRecordingService recordingService;
    private readonly ILogger logger;

    /// <param name="configuredModel">設定的模型（<c>AiSettings:Model</c>）；回應沒有帶模型名稱時用它。</param>
    public RecordingChatClient(
        IChatClient innerClient, Guid sessionKey, string? configuredModel, AgentRecordingService recordingService, ILogger logger)
        : base(innerClient)
    {
        this.sessionKey = sessionKey;
        this.configuredModel = configuredModel;
        this.recordingService = recordingService;
        this.logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var digest = TryComputeDigest(list);
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        await RecordAsync(digest, () => response);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var digest = TryComputeDigest(list);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        // 合併（ToChatResponse）也放進 RecordAsync 的 try：錄製的任何失敗都不可讓已計費的即時回合失敗。
        await RecordAsync(digest, () => updates.ToChatResponse());
    }

    /// <summary>送出前先算摘要：之後函式呼叫迴圈會往同一份清單追加訊息。</summary>
    private string? TryComputeDigest(IList<ChatMessage> messages)
    {
        try
        {
            return ModelRequestDigest.Compute(messages);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute the agent model request digest; this call will not be recorded. SessionKey={SessionKey}", sessionKey);
            return null;
        }
    }

    /// <param name="getResponse">取得要錄製的回應；串流時在這裡才合併，合併失敗同樣只記 Warning。</param>
    private async Task RecordAsync(string? digest, Func<ChatResponse> getResponse)
    {
        if (digest is null)
        {
            return;
        }

        try
        {
            var response = getResponse();
            var responseJson = JsonSerializer.Serialize(response, AIJsonUtilities.DefaultOptions);
            var usageJson = response.Usage is null ? null : JsonSerializer.Serialize(response.Usage, AIJsonUtilities.DefaultOptions);
            var modelName = string.IsNullOrWhiteSpace(response.ModelId) ? configuredModel : response.ModelId;
            // AppendCallAsync 本身不拋例外；合併或序列化失敗（例如未登錄的 AIContent 子類）在這裡攔下。
            await recordingService.AppendCallAsync(sessionKey, digest, responseJson, usageJson, modelName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record an agent model response. SessionKey={SessionKey}", sessionKey);
        }
    }
}
