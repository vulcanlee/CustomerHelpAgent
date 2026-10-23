using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 回放用的模型（1.0.5 起）：不連線、不查額度、不記 Token 用量，依 <see cref="ReplayCursor"/> 逐筆送出錄製的回應
/// （<see cref="AgentRecordedCallView.ResponseJson"/>，<see cref="AIJsonUtilities.DefaultOptions"/>）。
///
/// <para>位置與 <see cref="RecordingChatClient"/> 相同：<see cref="TracingChatClient"/> → <b>這裡</b>（取代 <c>AgentChatClientFactory.Create</c>），
/// 所以看到的訊息與錄製時同一層，<see cref="ModelRequestDigest"/> 才比得起來。函式呼叫迴圈、Guard、工具都照常執行（工具會真的寫入）。</para>
///
/// <list type="bullet">
/// <item>每次呼叫先比對這次請求的摘要與下一筆錄製的 <see cref="AgentRecordedCallView.RequestDigest"/>：
/// 不同拋 <see cref="ReplayDivergedException"/>（不消耗這一筆）；錄製用完拋 <see cref="ReplayExhaustedException"/>。</item>
/// <item>申請編號替換：錄製的文字可能提到錄製當時建立的編號，回放時工具建立的是新編號。請求裡若有
/// <c>create_after_sales_request</c> 的成功結果，就把回應文字裡的 <c>AS</c>＋12 碼換成最近一次建立的編號；找不到就原樣保留。</item>
/// <item>串流：以 <see cref="ChatResponse.ToChatResponseUpdates"/> 逐段送出，每段之間等 <c>updateDelay</c>（Runner 預設 30 ms）模擬打字。</item>
/// </list>
/// </summary>
internal sealed partial class ReplayChatClient : IChatClient
{
    private readonly IReadOnlyList<AgentRecordedCallView> calls;
    private readonly ReplayCursor cursor;
    private readonly TimeSpan updateDelay;
    private readonly ILogger logger;

    /// <param name="calls">錄製的模型呼叫，已依 <c>(Sequence, Id)</c> 排序（<c>AgentRecordingService.GetCallsAsync</c>）。</param>
    public ReplayChatClient(IReadOnlyList<AgentRecordedCallView> calls, ReplayCursor cursor, TimeSpan updateDelay, ILogger logger)
    {
        this.calls = calls;
        this.cursor = cursor;
        this.updateDelay = updateDelay;
        this.logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Take(messages as IList<ChatMessage> ?? messages.ToList()));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = Take(messages as IList<ChatMessage> ?? messages.ToList());
        var first = true;
        foreach (var update in response.ToChatResponseUpdates())
        {
            if (!first && updateDelay > TimeSpan.Zero)
            {
                await Task.Delay(updateDelay, cancellationToken);
            }

            first = false;
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private ChatResponse Take(IList<ChatMessage> messages)
    {
        var index = cursor.NextCall;
        if (index >= calls.Count)
        {
            logger.LogInformation("Agent replay has no more recorded model responses. RecordingId={RecordingId}, CallIndex={CallIndex}", cursor.RecordingId, index);
            throw new ReplayExhaustedException();
        }

        var call = calls[index];
        if (!string.Equals(ModelRequestDigest.Compute(messages), call.RequestDigest, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Agent replay diverged: the model request differs from the recording. RecordingId={RecordingId}, CallIndex={CallIndex}, Sequence={Sequence}",
                cursor.RecordingId, index, call.Sequence);
            throw new ReplayDivergedException();
        }

        var response = JsonSerializer.Deserialize<ChatResponse>(call.ResponseJson, AIJsonUtilities.DefaultOptions)
            ?? throw new JsonException("Recorded model response is empty.");
        cursor.AdvanceCall();

        if (LatestCreatedRequestNo(messages) is { } requestNo)
        {
            foreach (var text in response.Messages.SelectMany(m => m.Contents).OfType<TextContent>())
            {
                text.Text = RequestNoPattern().Replace(text.Text, requestNo);
            }
        }

        return response;
    }

    /// <summary>請求裡最近一次 <c>create_after_sales_request</c> 成功結果的 <c>request_no</c>；沒有就是 null。</summary>
    private static string? LatestCreatedRequestNo(IList<ChatMessage> messages)
    {
        var createCallIds = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Where(x => x.Name == AfterSalesAgentTools.CreateAfterSalesRequestName)
            .Select(x => x.CallId)
            .ToHashSet(StringComparer.Ordinal);
        if (createCallIds.Count == 0)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            foreach (var result in messages[i].Contents.OfType<FunctionResultContent>().Reverse())
            {
                if (createCallIds.Contains(result.CallId)
                    && ToolOutcome.TryParse(result.Result, out var outcome)
                    && outcome.Status == ToolOutcomeStatus.Ok
                    && outcome.Data is JsonElement { ValueKind: JsonValueKind.Object } data
                    && data.TryGetProperty("request_no", out var no)
                    && no.ValueKind == JsonValueKind.String
                    && no.GetString() is { } value
                    && RequestNoPattern().IsMatch(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"AS\d{12}")]
    private static partial Regex RequestNoPattern();
}

/// <summary>回放中這次送給模型的內容與錄製時不同（摘要不一致）：資料或故障開關和錄製時不一樣。</summary>
internal sealed class ReplayDivergedException() : Exception("The model request differs from the recording.");

/// <summary>回放中錄製的模型回應已經用完。</summary>
internal sealed class ReplayExhaustedException() : Exception("The recording has no more model responses.");
