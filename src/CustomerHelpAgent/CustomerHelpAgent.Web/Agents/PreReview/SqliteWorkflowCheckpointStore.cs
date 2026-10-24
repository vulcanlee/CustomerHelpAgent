using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 以 SQLite（<c>WorkflowCheckpoint</c> 資料表）保存 MAF Workflow 的 checkpoint，應用程式重啟後可續跑。
/// ⚠️ <see cref="RetrieveIndexAsync"/> 必須由舊到新排序，<c>CheckpointManager.GetLatestCheckpointAsync</c> 取最後一筆當最新。
/// </summary>
public sealed class SqliteWorkflowCheckpointStore : JsonCheckpointStore
{
    private readonly WorkflowCheckpointService service;

    public SqliteWorkflowCheckpointStore(WorkflowCheckpointService service)
    {
        this.service = service;
    }

    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var checkpointId = Guid.NewGuid().ToString("N");
        await service.AddAsync(sessionId, checkpointId, parent?.CheckpointId, value.GetRawText());
        return new CheckpointInfo(sessionId, checkpointId);
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        var payload = await service.GetPayloadAsync(sessionId, key.CheckpointId)
            ?? throw new KeyNotFoundException($"Checkpoint not found. SessionId={sessionId}, CheckpointId={key.CheckpointId}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        var ids = await service.ListCheckpointIdsAsync(sessionId, withParent?.CheckpointId);
        return ids.Select(id => new CheckpointInfo(sessionId, id)).ToList();
    }
}
