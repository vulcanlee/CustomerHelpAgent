using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>多 Agent 模式的一位參與者：固定的 Id（也是 Name）、顯示名稱、交接說明（框架拿來當交接函式的說明）、提示詞種類與工具子集。</summary>
public sealed record HandoffParticipant(string Id, string DisplayName, string Description, string PromptKey, IReadOnlyList<string> ToolNames);

/// <summary>
/// 多 Agent 交接模式（M5）的結構：分流 Agent → 換退貨／品質／物流專員，三位專員 → 分流 Agent，開啟 <c>EnableReturnToPrevious</c>
/// （下一輪直接由上一位發言的專員接手）。以 MAF 內建的 <see cref="HandoffWorkflowBuilder"/> 建立（不自己寫路由）。
/// <list type="bullet">
/// <item>⚠️ 參與者的 <see cref="HandoffParticipant.Id"/> 是 <c>ChatClientAgentOptions.Id</c>／<c>Name</c>，必須固定：executor id（<c>{Name}_{Id}</c>）、
/// 核准的 port id 與外部 RequestId（<c>"{長度}:{executorId}_UserInput:ficc_{CallId}"</c>）都由它組成，改名會讓進行中的 checkpoint 與待決確認卡失效。</item>
/// <item>交接函式由框架產生，名稱是 <c>handoff_to_N</c>：N 依「來源 Agent 的目標註冊順序」從 1 起算（<see cref="Routes"/> 的順序），
/// 不是 Agent id。交接沒有事件型別，Runner 以 update 裡作者為 X 的 <c>handoff_to_N</c> 呼叫，經 <see cref="TargetOf"/> 對應出接手者。</item>
/// <item>每位專員只拿得到自己的工具子集（<see cref="HandoffParticipant.ToolNames"/>）；模型亂編的工具名稱不會被執行。</item>
/// </list>
/// </summary>
public static class HandoffTopologyFactory
{
    public const string TriageId = "triage";
    public const string ExchangeId = "exchange";
    public const string QualityId = "quality";
    public const string LogisticsId = "logistics";

    /// <summary>包裝成 Agent（<c>AsAIAgent</c>）時的 Id 與名稱。</summary>
    public const string WorkflowAgentId = "aftersales-handoff";

    private const string HandoffFunctionPrefix = "handoff_to_";

    /// <summary>四位參與者（第一位是起點的分流 Agent）。</summary>
    public static IReadOnlyList<HandoffParticipant> Participants { get; } =
    [
        new(TriageId, "分流 Agent", "分流 Agent：專員處理完自己負責的事、或顧客提出不屬於該專員負責的需求時，交回這裡重新分流",
            PromptTemplateKeys.AfterSalesTriage,
            [AfterSalesAgentTools.QueryMyOrdersName, AfterSalesAgentTools.RememberPreferenceName]),
        new(ExchangeId, "換退貨專員", "換退貨專員：處理換尺碼、換顏色與退貨退款（查規則、查庫存、建立申請）",
            PromptTemplateKeys.AfterSalesExchangeSpecialist,
            [
                AfterSalesAgentTools.QueryMyOrdersName, AfterSalesAgentTools.GetEffectivePolicyName, AfterSalesAgentTools.CheckVariantStockName,
                AfterSalesAgentTools.CreateAfterSalesRequestName, AfterSalesAgentTools.RememberPreferenceName,
            ]),
        new(QualityId, "品質專員", "品質專員：處理商品品質問題（開膠、脫線、瑕疵、損壞），可請顧客上傳照片並建立申請",
            PromptTemplateKeys.AfterSalesQualitySpecialist,
            [
                AfterSalesAgentTools.QueryMyOrdersName, AfterSalesAgentTools.GetEffectivePolicyName, AfterSalesAgentTools.RequestPhotoUploadName,
                AfterSalesAgentTools.CreateAfterSalesRequestName, AfterSalesAgentTools.RememberPreferenceName,
            ]),
        new(LogisticsId, "物流專員", "物流專員：查詢出貨與配送進度、處理物流催件",
            PromptTemplateKeys.AfterSalesLogisticsSpecialist,
            [
                AfterSalesAgentTools.QueryMyOrdersName, AfterSalesAgentTools.TrackShipmentName, AfterSalesAgentTools.GetEffectivePolicyName,
                AfterSalesAgentTools.CreateAfterSalesRequestName, AfterSalesAgentTools.RememberPreferenceName,
            ]),
    ];

    /// <summary>交接路線：來源 → 有序的目標（第 N 個目標＝<c>handoff_to_N</c>）。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Routes { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [TriageId] = [ExchangeId, QualityId, LogisticsId],
        [ExchangeId] = [TriageId],
        [QualityId] = [TriageId],
        [LogisticsId] = [TriageId],
    };

    /// <summary>參與者 Id（update 的 <c>AuthorName</c>）→ 參與者；不是參與者回 null。</summary>
    public static HandoffParticipant? Find(string? id) => Participants.FirstOrDefault(x => x.Id == id);

    /// <summary>參與者 Id → 顯示名稱（例如 <c>quality</c> → 品質專員）；不是參與者回 null。</summary>
    public static string? DisplayNameOf(string? id) => Find(id)?.DisplayName;

    /// <summary>來源參與者呼叫的函式若是交接函式（<c>handoff_to_N</c>），回傳接手的參與者 Id；不是交接函式或 N 超出範圍回 null。</summary>
    public static string? TargetOf(string? sourceId, string? functionName)
    {
        if (sourceId is null || functionName is null || !functionName.StartsWith(HandoffFunctionPrefix, StringComparison.Ordinal)
            || !Routes.TryGetValue(sourceId, out var targets)
            || !int.TryParse(functionName.AsSpan(HandoffFunctionPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 1 || index > targets.Count)
        {
            return null;
        }

        return targets[index - 1];
    }

    /// <summary>
    /// 包在每位參與者外面：送出的 update 拿掉 <c>AdditionalProperties["_attribution"]</c>（MAF 標記「這則訊息來自哪裡」，只對請求訊息有意義）。
    /// ⚠️ 不可移除：核准（或拒絕）後，<c>FunctionInvokingChatClient</c> 以「從歷史載入的原始呼叫訊息」複製出呼叫訊息再送出，連同「來自歷史」的標記；
    /// Handoff 把它原封不動轉給其他參與者，對方存歷史時（預設過濾器不存「來自歷史」的請求訊息）丟掉呼叫、只留下結果，
    /// 下次再被呼叫就送出沒有對應呼叫的工具結果，Responses API 回 HTTP 400（M5 實機劇本 H）。
    /// 參與者自己的歷史由 <see cref="ChatClientAgent"/> 內部存，不受影響；只換掉 update 的屬性與 RawRepresentation（複製後再改），不動共用的字典。
    /// 模型請求只多了原本就該有的呼叫訊息；回放摘要（<see cref="ModelRequestDigest"/>）只取顧客文字與工具結果，不受影響、仍是決定性的。
    /// 由 <c>MafHandoffBehaviorTests.Handoff_AfterDecision_*</c> 釘住。
    /// </summary>
    public static AIAgent ScrubHistoryAttribution(AIAgent participant)
        => participant.AsBuilder().Use(runFunc: null, runStreamingFunc: ScrubHistoryAttributionAsync).Build();

    private static async IAsyncEnumerable<AgentResponseUpdate> ScrubHistoryAttributionAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in inner.RunStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            // 框架把 update 轉回訊息時優先用 RawRepresentation 的 ChatResponseUpdate（不看 update 本身的屬性），兩邊都要拿掉；
            // 原始物件與歷史訊息共用同一個字典，所以複製後再改。
            if (update.RawRepresentation is ChatResponseUpdate { AdditionalProperties: { } rawProperties } raw && HasAttribution(rawProperties))
            {
                var copy = raw.Clone();
                copy.AdditionalProperties = WithoutAttribution(rawProperties);
                update.RawRepresentation = copy;
            }

            if (update.AdditionalProperties is { } properties && HasAttribution(properties))
            {
                update.AdditionalProperties = WithoutAttribution(properties);
            }

            yield return update;
        }
    }

    private static bool HasAttribution(AdditionalPropertiesDictionary properties)
        => properties.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey);

    private static AdditionalPropertiesDictionary? WithoutAttribution(AdditionalPropertiesDictionary properties)
    {
        var scrubbed = new AdditionalPropertiesDictionary(properties);
        scrubbed.Remove(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey);
        // ⚠️ 修正有效的前提：拿掉標記後字典通常是空的，所以回傳 null（不是空字典）。
        // MAF 的 WithAgentRequestMessageSource 只做淺層 Clone()，字典不是 null 時直接在「共用的」字典上寫入標記，
        // 會連帶改到共享對話與其他參與者歷史裡的同一則訊息；是 null 時才會建立新字典。若之後還有其他屬性留下，要重新檢查這一點。
        return scrubbed.Count == 0 ? null : scrubbed;
    }

    /// <summary>
    /// 以 <paramref name="createParticipant"/> 建立四位參與者（呼叫端負責工具子集、Guard、記憶 provider），依 <see cref="Routes"/> 建立交接 Workflow。
    /// 交接說明改用固定的中文版本（<see cref="AfterSalesHandoffPromptDefaults.HandoffInstructions"/>）。每輪重建一次（不跨輪共用 Workflow 物件）。
    /// 每位參與者外面再包 <see cref="ScrubHistoryAttribution"/>（核准後工具呼叫與結果才會在其他參與者的歷史裡成對）。
    /// </summary>
    public static Workflow Build(Func<HandoffParticipant, AIAgent> createParticipant)
    {
        var agents = Participants.ToDictionary(x => x.Id, x => ScrubHistoryAttribution(createParticipant(x)));
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(agents[TriageId])
            .WithHandoffInstructions(AfterSalesHandoffPromptDefaults.HandoffInstructions.ReplaceLineEndings("\n"));
        foreach (var (source, targets) in Routes)
        {
            builder = builder.WithHandoffs(agents[source], targets.Select(x => agents[x]));
        }

        return builder.EnableReturnToPrevious().Build();
    }
}
