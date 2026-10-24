using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>以 SQLite 保存 MAF checkpoint（<see cref="SqliteWorkflowCheckpointStore"/>）：往返、由舊到新排序、經 CheckpointManager 取最新。</summary>
public sealed class SqliteWorkflowCheckpointStoreTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private SqliteWorkflowCheckpointStore store = null!;
    private WorkflowCheckpointService service = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        service = new WorkflowCheckpointService(factory, TimeProvider.System, NullLogger<WorkflowCheckpointService>.Instance);
        store = new SqliteWorkflowCheckpointStore(service);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private sealed class UpperExecutor() : Executor<string, string>("upper")
    {
        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(message.ToUpperInvariant());
    }

    private sealed class SuffixExecutor() : Executor<string, string>("suffix")
    {
        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(message + "!");
    }

    [Fact]
    public async Task CheckpointStore_RoundTripsJsonAndIndexOldestFirst()
    {
        using var doc1 = JsonDocument.Parse("{\"n\":1,\"text\":\"一\"}");
        using var doc2 = JsonDocument.Parse("{\"n\":2}");
        using var doc3 = JsonDocument.Parse("{\"n\":3}");

        var first = await store.CreateCheckpointAsync("s1", doc1.RootElement.Clone());
        var second = await store.CreateCheckpointAsync("s1", doc2.RootElement.Clone(), first);
        var third = await store.CreateCheckpointAsync("s1", doc3.RootElement.Clone(), second);
        await store.CreateCheckpointAsync("other", doc1.RootElement.Clone());

        var index = (await store.RetrieveIndexAsync("s1")).ToList();
        Assert.Equal([first.CheckpointId, second.CheckpointId, third.CheckpointId], index.Select(x => x.CheckpointId));
        Assert.All(index, x => Assert.Equal("s1", x.SessionId));
        var children = (await store.RetrieveIndexAsync("s1", first)).ToList();
        Assert.Equal([second.CheckpointId], children.Select(x => x.CheckpointId));
        var loaded = await store.RetrieveCheckpointAsync("s1", first);
        Assert.Equal(1, loaded.GetProperty("n").GetInt32());
        Assert.Equal("一", loaded.GetProperty("text").GetString());
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await store.RetrieveCheckpointAsync("s1", new CheckpointInfo("s1", "missing")));
    }

    [Fact]
    public async Task CheckpointStore_GetLatest_ReturnsNewest()
    {
        var upper = new UpperExecutor();
        var suffix = new SuffixExecutor();
        var workflow = new WorkflowBuilder(upper).AddEdge(upper, suffix).WithOutputFrom(suffix).Build();
        var manager = CheckpointManager.CreateJson(store, new JsonSerializerOptions { TypeInfoResolver = PreReviewJsonContext.Default });

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, "abc", manager, "session-x");
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
        {
            if (evt is WorkflowOutputEvent outputEvent)
            {
                output = outputEvent.As<string>();
            }
        }

        var index = (await store.RetrieveIndexAsync("session-x")).ToList();
        Assert.Equal("ABC!", output);
        Assert.True(index.Count >= 2, $"checkpoints={index.Count}");
        var latest = await manager.GetLatestCheckpointAsync("session-x");
        Assert.NotNull(latest);
        Assert.Equal(index[^1].CheckpointId, latest.CheckpointId);
        Assert.Null(await manager.GetLatestCheckpointAsync("no-such-session"));
    }

    /// <summary>多 Agent 會話（M5）每輪存回後只留下指定的 checkpoint；不給要保留的 Id 時整批刪除；別的會話不受影響。</summary>
    [Fact]
    public async Task DeleteSessionCheckpoints_KeepsOnlyGivenCheckpoint()
    {
        await service.AddAsync("s1", "a", null, "{}");
        await service.AddAsync("s1", "b", "a", "{}");
        await service.AddAsync("s1", "c", "b", "{}");
        await service.AddAsync("other", "x", null, "{}");

        Assert.Equal(2, await service.DeleteSessionCheckpointsAsync("s1", "c"));
        Assert.Equal(["c"], await service.ListCheckpointIdsAsync("s1"));

        Assert.Equal(1, await service.DeleteSessionCheckpointsAsync("s1", keepCheckpointId: null));
        Assert.Empty(await service.ListCheckpointIdsAsync("s1"));
        Assert.Equal(["x"], await service.ListCheckpointIdsAsync("other"));
    }
}
