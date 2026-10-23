using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 售後 Agent 的函式呼叫中介層（每輪一個實例，掛在 <c>agent.AsBuilder().Use(guard.InvokeAsync)</c>）：
/// 「不確定即停」由這裡以程式強制，模型改寫不了。
/// <list type="number">
/// <item>每次工具呼叫發 <see cref="AgentStepKind.ToolRequest"/>、<see cref="AgentStepKind.ProgramCheck"/>（身分核對）、<see cref="AgentStepKind.ToolResult"/>（含刻意不送欄位）。</item>
/// <item>本輪出現 <c>not_found</c>／<c>no_policy</c>／<c>conflict</c> 後封鎖寫入（<see cref="WriteBlocked"/>）：
/// <c>create_after_sales_request</c> 不執行、回 <c>blocked</c>。⚠️ 寫入工具需要確認，模型提出時其實先變成核准請求而不會走到這裡，
/// 所以 Runner 在顯示確認卡前也會檢查 <see cref="WriteBlocked"/>；這裡是第二道防線。</item>
/// <item><c>forbidden</c>／<c>timeout</c>／<c>unavailable</c> → 立即終止（<c>ctx.Terminate</c>），回覆改用程式的固定說明（<see cref="FixedReply"/>）。</item>
/// <item><c>conflict</c> → 停止原因 <see cref="AgentStopReason.PolicyConflict"/>，不終止（讓模型說明轉人工）。</item>
/// <item><c>request_photo_upload</c> 成功 → 終止並等待上傳。</item>
/// <item>工具連續失敗（非 ok，或拋例外）達 <see cref="Configuration.AfterSalesAgentSettings.MaxConsecutiveToolFailures"/> 次 → 終止。
/// 工具都以信封回報失敗而不拋例外，所以 <c>FunctionInvokingChatClient</c> 自己的連續錯誤計數只涵蓋例外，這裡另外計算。</item>
/// </list>
/// </summary>
public sealed class AfterSalesAgentGuard
{
    public const string TimeoutExplanation = "訂單系統暫時沒有回應，目前無法確認，不代表您沒有這筆訂單。已為您轉交客服。";
    public const string ForbiddenExplanation = "您的登入狀態已失效，請重新登入後再試。";
    public const string UnavailableExplanation = "售後規則服務暫時無法使用，目前無法核實，請稍後再試或洽客服。";
    public const string BlockedMessage = "資訊不足或規則衝突，請先向顧客確認或轉人工";

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly int maxConsecutiveFailures;
    private readonly Func<AgentTraceEvent, Task> emit;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly List<AgentPolicyCard> policyCards = [];

    public AfterSalesAgentGuard(int maxConsecutiveFailures, Func<AgentTraceEvent, Task> emit, TimeProvider timeProvider, ILogger logger)
    {
        this.maxConsecutiveFailures = maxConsecutiveFailures;
        this.emit = emit;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>由 Guard 判定的停止原因；null＝交給 Runner 依回應判定。</summary>
    public AgentStopReason? StopReason { get; private set; }

    /// <summary>取代模型輸出的固定說明（只有逾時、權限、服務不可用時有值）。</summary>
    public string? FixedReply { get; private set; }

    /// <summary>本輪是否已封鎖寫入（出現過查無、無規則或規則衝突）。</summary>
    public bool WriteBlocked { get; private set; }

    public int ToolCallCount { get; private set; }

    public IReadOnlyList<AgentPolicyCard> PolicyCards => policyCards;

    public string? CreatedRequestNo { get; private set; }

    private int consecutiveFailures;

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var name = context.Function.Name;
        ToolCallCount++;
        await EmitAsync(AgentStepKind.ToolRequest, AgentDataSource.Conversation, DescribeRequest(name, context.Arguments),
            Serialize(new { tool = name, arguments = context.Arguments }));

        if (name == AfterSalesAgentTools.CreateAfterSalesRequestName && WriteBlocked)
        {
            var blocked = ToolOutcome.Fail(ToolOutcomeStatus.Blocked, BlockedMessage);
            await EmitBlockedAsync(blocked);
            logger.LogInformation("Agent write tool blocked by the guard. ToolName={ToolName}", name);
            return blocked.ToJsonElement();
        }

        object? result;
        try
        {
            result = await next(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            CountFailure(context);
            logger.LogWarning(ex, "Agent tool threw an exception. ToolName={ToolName}", name);
            throw;
        }

        if (!ToolOutcome.TryParse(result, out var outcome))
        {
            logger.LogWarning("Agent tool returned a result that is not a tool outcome envelope. ToolName={ToolName}", name);
            return result;
        }

        await EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program, outcome.Status == ToolOutcomeStatus.Forbidden
            ? "程式核對你的登入身分：未通過（登入已失效或沒有使用權限），立即停止。"
            : "程式核對你的登入身分：與這段對話的顧客相符，只查你本人的資料。", null);
        await EmitAsync(AgentStepKind.ToolResult, SourceOf(name, outcome.Status), DescribeResult(name, outcome), Serialize(result));
        Apply(context, name, outcome);
        return result;
    }

    /// <summary>寫入被封鎖時的事件；Runner 攔下寫入的核准請求（不顯示確認卡）時也記錄同一組。</summary>
    internal async Task EmitBlockedAsync(ToolOutcome blocked)
    {
        await EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
            "程式封鎖寫入：本輪出現查無資料、查無規則或規則衝突，不可建立申請。", null);
        await EmitAsync(AgentStepKind.ToolResult, AgentDataSource.Program,
            $"未執行建立申請（{BlockedMessage}）。", Serialize(blocked.ToJsonElement()));
    }

    private void Apply(FunctionInvocationContext context, string name, ToolOutcome outcome)
    {
        switch (outcome.Status)
        {
            case ToolOutcomeStatus.Ok:
                consecutiveFailures = 0;
                OnSuccess(context, name, outcome);
                break;

            case ToolOutcomeStatus.Forbidden:
                Terminate(context, AgentStopReason.Forbidden, ForbiddenExplanation);
                break;

            case ToolOutcomeStatus.Timeout:
                Terminate(context, AgentStopReason.Timeout, TimeoutExplanation);
                break;

            case ToolOutcomeStatus.Unavailable:
                // 送出申請拋例外時，工具的訊息（「申請可能未完成…請勿重複送出」）本身就是程式產生的固定說明，比「規則服務」準確。
                Terminate(context, AgentStopReason.ServiceUnavailable,
                    name == AfterSalesAgentTools.CreateAfterSalesRequestName ? outcome.Message : UnavailableExplanation);
                break;

            case ToolOutcomeStatus.Conflict:
                WriteBlocked = true;
                StopReason ??= AgentStopReason.PolicyConflict;
                CountFailure(context);
                break;

            case ToolOutcomeStatus.NotFound:
            case ToolOutcomeStatus.NoPolicy:
                WriteBlocked = true;
                CountFailure(context);
                break;

            default:
                CountFailure(context);
                break;
        }
    }

    private void OnSuccess(FunctionInvocationContext context, string name, ToolOutcome outcome)
    {
        if (outcome.Data is not JsonElement data)
        {
            return;
        }

        switch (name)
        {
            case AfterSalesAgentTools.GetEffectivePolicyName:
                if (data.Deserialize<AgentPolicyCard>(ToolOutcome.JsonOptions) is { } card)
                {
                    policyCards.Add(card);
                }

                break;

            case AfterSalesAgentTools.CreateAfterSalesRequestName:
                if (data.TryGetProperty("request_no", out var requestNo))
                {
                    CreatedRequestNo = requestNo.GetString();
                }

                break;

            case AfterSalesAgentTools.RequestPhotoUploadName:
                context.Terminate = true;
                SetStop(AgentStopReason.AwaitingPhotoUpload, terminating: true);
                break;
        }
    }

    private void CountFailure(FunctionInvocationContext context)
    {
        consecutiveFailures++;
        if (consecutiveFailures >= maxConsecutiveFailures)
        {
            context.Terminate = true;
            SetStop(AgentStopReason.ConsecutiveToolFailures, terminating: true);
        }
    }

    private void Terminate(FunctionInvocationContext context, AgentStopReason reason, string fixedReply)
    {
        context.Terminate = true;
        if (SetStop(reason, terminating: true))
        {
            FixedReply = fixedReply;
        }
    }

    /// <summary>終止類的原因蓋過非終止類（規則衝突）；同為終止類時保留第一個。</summary>
    private bool SetStop(AgentStopReason reason, bool terminating)
    {
        if (StopReason is null || (terminating && StopReason == AgentStopReason.PolicyConflict))
        {
            StopReason = reason;
            return true;
        }

        return false;
    }

    private Task EmitAsync(AgentStepKind kind, AgentDataSource source, string plainText, string? rawJson)
        => emit(new AgentTraceEvent(kind, source, plainText, rawJson, timeProvider.GetLocalNow().DateTime));

    private static AgentDataSource SourceOf(string name, ToolOutcomeStatus status) => status switch
    {
        ToolOutcomeStatus.Ok or ToolOutcomeStatus.NotFound or ToolOutcomeStatus.NoPolicy or ToolOutcomeStatus.Conflict or ToolOutcomeStatus.Invalid => name switch
        {
            AfterSalesAgentTools.GetEffectivePolicyName => AgentDataSource.Policy,
            AfterSalesAgentTools.RequestPhotoUploadName => AgentDataSource.Program,
            _ => AgentDataSource.Database,
        },
        _ => AgentDataSource.Program,
    };

    private static string DescribeRequest(string name, IDictionary<string, object?> args)
    {
        var issue = IssueLabel(Arg(args, "issueType"));
        return name switch
        {
            AfterSalesAgentTools.QueryMyOrdersName => "Agent 決定先查你的訂單" + Conditions(
                ("顏色", Arg(args, "color")), ("關鍵字", Arg(args, "keyword"))),
            AfterSalesAgentTools.GetEffectivePolicyName => $"Agent 決定查詢這件商品「{issue}」適用的售後規則",
            AfterSalesAgentTools.CheckVariantStockName => "Agent 決定查詢同款其他尺碼、顏色有沒有貨",
            AfterSalesAgentTools.TrackShipmentName => "Agent 決定查詢這筆訂單的物流狀態",
            AfterSalesAgentTools.RequestPhotoUploadName => "Agent 請你上傳照片" + Conditions(("原因", Arg(args, "reason"))),
            AfterSalesAgentTools.CreateAfterSalesRequestName => $"你已確認，Agent 送出「{issue}」售後申請",
            _ => $"Agent 決定呼叫工具 {name}",
        };
    }

    private static string DescribeResult(string name, ToolOutcome outcome)
    {
        if (outcome.Status != ToolOutcomeStatus.Ok)
        {
            return $"{StatusText(outcome.Status)}：{outcome.Message}";
        }

        var data = outcome.Data as JsonElement?;
        var text = name switch
        {
            AfterSalesAgentTools.QueryMyOrdersName when data is { ValueKind: JsonValueKind.Array } items
                => $"查到 {items.GetArrayLength()} 筆訂單商品",
            AfterSalesAgentTools.GetEffectivePolicyName when data is { } card
                => $"取得規則「{Text(card, "title")}」（第 {Number(card, "version")} 版，來源：{Text(card, "source")}），簽收天數由程式計算",
            AfterSalesAgentTools.CheckVariantStockName when data is { ValueKind: JsonValueKind.Array } options
                => $"查到 {options.GetArrayLength()} 種規格的有無貨",
            AfterSalesAgentTools.TrackShipmentName when data is { } shipment
                => $"物流狀態：{Text(shipment, "status_text")}",
            AfterSalesAgentTools.RequestPhotoUploadName => "等待你上傳照片，本輪先停在這裡",
            AfterSalesAgentTools.CreateAfterSalesRequestName when data is { } created
                => $"售後申請已建立並回查確認，申請編號 {Text(created, "request_no")}",
            _ => "完成",
        };
        return outcome.WithheldFields.Count == 0 ? text : $"{text}，未送出：{string.Join("、", outcome.WithheldFields)}";
    }

    private static string StatusText(ToolOutcomeStatus status) => status switch
    {
        ToolOutcomeStatus.NotFound => "查無資料",
        ToolOutcomeStatus.Timeout => "逾時",
        ToolOutcomeStatus.Forbidden => "身分不符",
        ToolOutcomeStatus.Unavailable => "服務暫時無法使用",
        ToolOutcomeStatus.NoPolicy => "查無適用規則",
        ToolOutcomeStatus.Conflict => "規則衝突",
        ToolOutcomeStatus.Blocked => "已封鎖",
        ToolOutcomeStatus.Invalid => "不符條件",
        _ => status.ToString(),
    };

    private static string IssueLabel(string? issueType)
        => AfterSalesAgentTools.TryParseIssueType(issueType, out var type) ? AfterSalesLabels.TypeText(type) : issueType ?? "未指定";

    /// <summary>模型送來的引數值（實際連線時是 <see cref="JsonElement"/>，測試腳本可能是字串或數字）轉成字串；空白回 null。</summary>
    internal static string? Arg(IDictionary<string, object?>? args, string name)
    {
        if (args is null || !args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string Conditions(params (string Label, string? Value)[] items)
    {
        var parts = items.Where(x => x.Value is not null).Select(x => $"{x.Label}：{x.Value}").ToList();
        return parts.Count == 0 ? string.Empty : $"（{string.Join("，", parts)}）";
    }

    private static string Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Number(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : "?";

    private string? Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, RawJsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to serialize an agent trace snapshot.");
            return JsonSerializer.Serialize(new { snapshotError = ex.GetType().Name });
        }
    }
}
