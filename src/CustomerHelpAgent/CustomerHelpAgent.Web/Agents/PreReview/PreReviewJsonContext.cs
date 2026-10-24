using System.Text.Json;
using System.Text.Json.Serialization;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 初審 Workflow 節點之間的訊息與狀態型別的 JSON 中繼資料（交給 <c>CheckpointManager.CreateJson</c>），
/// 也用來序列化存進 <c>PreReviewRun</c> 的結論 JSON。
/// ⚠️ 每個會跨邊傳遞的訊息、RequestPort 的請求／回應型別、executor 存進 checkpoint 的狀態都要列在這裡，
/// 否則 checkpoint 序列化會拋「No JSON type info is available for type ...」。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PreReviewStatus))]
[JsonSerializable(typeof(PreReviewNode))]
[JsonSerializable(typeof(NodeStatus))]
[JsonSerializable(typeof(PreReviewStart))]
[JsonSerializable(typeof(PreReviewCase))]
[JsonSerializable(typeof(PreReviewFinding))]
[JsonSerializable(typeof(PreReviewFindingData))]
[JsonSerializable(typeof(List<PreReviewFinding>))]
[JsonSerializable(typeof(RuleCheckResult))]
[JsonSerializable(typeof(PreReviewSummary))]
[JsonSerializable(typeof(PhotoReviewResult))]
[JsonSerializable(typeof(HistoryReviewResult))]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(StaffDecision))]
[JsonSerializable(typeof(ExecutionResult))]
[JsonSerializable(typeof(DecisionResult))]
public sealed partial class PreReviewJsonContext : JsonSerializerContext
{
    /// <summary>checkpoint 用的序列化選項（<c>CheckpointManager.CreateJson(store, CheckpointOptions)</c>）。</summary>
    public static JsonSerializerOptions CheckpointOptions { get; } = new() { TypeInfoResolver = Default };
}
