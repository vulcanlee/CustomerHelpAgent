using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

/// <summary>初審一輪的持久化（<see cref="PreReviewRunService"/>）：Attempt 遞增、認領只成功一次、取代與續跑清單。</summary>
public sealed class PreReviewRunServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private PreReviewRunService service = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        // 只驗證初審服務本身，不必準備顧客與訂單明細等外鍵來源。
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        service = new PreReviewRunService(factory, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private async Task<int> AddRequestAsync(string requestNo)
    {
        await using var db = factory.CreateDbContext();
        var request = new AfterSalesRequest
        {
            RequestNo = requestNo,
            CaseKey = Guid.NewGuid(),
            Type = AfterSalesType.SizeExchange,
            Status = AfterSalesStatus.Submitted,
            CreatedAt = UtcNow.UtcDateTime,
            UpdatedAt = UtcNow.UtcDateTime,
        };
        db.AfterSalesRequest.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    [Fact]
    public async Task RunService_Attempt_Increments()
    {
        var requestId = await AddRequestAsync("AS1");

        var first = await service.CreateAsync(requestId);
        var second = await service.CreateAsync(requestId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first.Attempt);
        Assert.Equal(2, second.Attempt);
        Assert.Equal($"prereview-{requestId}-1", first.WorkflowSessionId);
        Assert.Equal($"prereview-{requestId}-2", second.WorkflowSessionId);
        Assert.Equal(PreReviewStatus.Pending, first.Status);
        var latest = await service.GetLatestForRequestAsync(requestId);
        Assert.Equal(second.Id, latest!.Id);
        Assert.Null(await service.CreateAsync(requestId + 999));
    }

    /// <summary>已結束（Completed／Superseded）各輪的 checkpoint 刪掉；Failed 與尚未結束的保留；別筆申請的不動。</summary>
    [Fact]
    public async Task Checkpoints_DeletedOnlyForEndedRunsOfTheRequest()
    {
        var requestId = await AddRequestAsync("AS1");
        var otherRequestId = await AddRequestAsync("AS2");
        var completed = (await service.CreateAsync(requestId))!;
        var superseded = (await service.CreateAsync(requestId))!;
        var failed = (await service.CreateAsync(requestId))!;
        var awaiting = (await service.CreateAsync(requestId))!;
        var otherCompleted = (await service.CreateAsync(otherRequestId))!;
        foreach (var run in new[] { completed, otherCompleted })
        {
            await service.SetStatusAsync(run.Id, PreReviewStatus.Executing);
            await service.SetStatusAsync(run.Id, PreReviewStatus.Completed);
        }

        await service.SetStatusAsync(superseded.Id, PreReviewStatus.Executing);
        await service.SetStatusAsync(superseded.Id, PreReviewStatus.Superseded);
        await service.SetStatusAsync(failed.Id, PreReviewStatus.Failed, "boom");
        await service.SetStatusAsync(awaiting.Id, PreReviewStatus.AwaitingDecision);
        var checkpoints = new WorkflowCheckpointService(factory, new ManualTimeProvider(UtcNow), NullLogger<WorkflowCheckpointService>.Instance);
        foreach (var run in new[] { completed, superseded, failed, awaiting, otherCompleted })
        {
            await checkpoints.AddAsync(run.WorkflowSessionId, "c1-" + run.Id, null, "{}");
            await checkpoints.AddAsync(run.WorkflowSessionId, "c2-" + run.Id, "c1-" + run.Id, "{}");
        }

        Assert.Equal(4, await checkpoints.DeleteForEndedRunsAsync(requestId));

        Assert.Empty(await checkpoints.ListCheckpointIdsAsync(completed.WorkflowSessionId));
        Assert.Empty(await checkpoints.ListCheckpointIdsAsync(superseded.WorkflowSessionId));
        Assert.Equal(2, (await checkpoints.ListCheckpointIdsAsync(failed.WorkflowSessionId)).Count);
        Assert.Equal(2, (await checkpoints.ListCheckpointIdsAsync(awaiting.WorkflowSessionId)).Count);
        Assert.Equal(2, (await checkpoints.ListCheckpointIdsAsync(otherCompleted.WorkflowSessionId)).Count);
    }

    [Fact]
    public async Task RunService_TryClaim_OnlyOnce()
    {
        var run = (await service.CreateAsync(await AddRequestAsync("AS1")))!;
        Assert.False(await service.TryClaimDecisionAsync(run.Id));
        await service.SetStatusAsync(run.Id, PreReviewStatus.AwaitingDecision);

        var first = await service.TryClaimDecisionAsync(run.Id);
        var second = await service.TryClaimDecisionAsync(run.Id);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(PreReviewStatus.Executing, (await service.GetAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task RunService_SupersedeAndUnfinished_FilterByStatus()
    {
        var requestId = await AddRequestAsync("AS1");
        var done = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(done.Id, PreReviewStatus.Executing);
        await service.SetStatusAsync(done.Id, PreReviewStatus.Completed);
        var running = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(running.Id, PreReviewStatus.Running);
        var other = (await service.CreateAsync(await AddRequestAsync("AS2")))!;

        var failed = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(failed.Id, PreReviewStatus.Failed, "boom");
        var executing = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(executing.Id, PreReviewStatus.Executing);

        Assert.Equal([running.Id, other.Id], (await service.GetUnfinishedAsync()).Select(x => x.Id));
        Assert.Equal(2, await service.SupersedeAsync(requestId));

        Assert.Equal(PreReviewStatus.Superseded, (await service.GetAsync(failed.Id))!.Status);
        Assert.Equal(PreReviewStatus.Executing, (await service.GetAsync(executing.Id))!.Status);

        Assert.Equal(PreReviewStatus.Completed, (await service.GetAsync(done.Id))!.Status);
        Assert.Equal(PreReviewStatus.Superseded, (await service.GetAsync(running.Id))!.Status);
        Assert.False(await service.SetStatusAsync(running.Id, PreReviewStatus.Completed));
        Assert.Equal([other.Id], (await service.GetUnfinishedAsync()).Select(x => x.Id));
    }

    [Fact]
    public async Task RunService_SetStatus_TerminalStatesStick_AndOnlyExecutingCanFinish()
    {
        var requestId = await AddRequestAsync("AS1");
        var run = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(run.Id, PreReviewStatus.Running);

        // 只有執行中的一輪能收尾（完成或因申請已被別處決定而結束）。
        Assert.False(await service.SetStatusAsync(run.Id, PreReviewStatus.Completed));
        Assert.False(await service.SetStatusAsync(run.Id, PreReviewStatus.Superseded));
        Assert.Equal(PreReviewStatus.Running, (await service.GetAsync(run.Id))!.Status);

        Assert.True(await service.SetStatusAsync(run.Id, PreReviewStatus.AwaitingDecision));
        Assert.True(await service.TryClaimDecisionAsync(run.Id));
        Assert.True(await service.SetStatusAsync(run.Id, PreReviewStatus.Completed));

        // 已完成就不再變動：晚到的「已由人工處理」、失敗、等待決策都蓋不掉它。
        Assert.False(await service.SetStatusAsync(run.Id, PreReviewStatus.Superseded));
        Assert.False(await service.SetStatusAsync(run.Id, PreReviewStatus.Failed, "late"));
        Assert.False(await service.SetStatusAsync(run.Id, PreReviewStatus.AwaitingDecision));
        var saved = (await service.GetAsync(run.Id))!;
        Assert.Equal(PreReviewStatus.Completed, saved.Status);
        Assert.Null(saved.ErrorMessage);

        var superseded = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(superseded.Id, PreReviewStatus.Executing);
        Assert.True(await service.SetStatusAsync(superseded.Id, PreReviewStatus.Superseded));
        Assert.False(await service.SetStatusAsync(superseded.Id, PreReviewStatus.Completed));
        Assert.Equal(PreReviewStatus.Superseded, (await service.GetAsync(superseded.Id))!.Status);
    }

    [Fact]
    public async Task RunService_NodesAndFindings_AreSaved()
    {
        var requestId = await AddRequestAsync("AS1");
        var run = (await service.CreateAsync(requestId))!;

        await service.SetNodeAsync(run.Id, PreReviewNode.PhotoReview, NodeStatus.Running);
        await service.SetNodeAsync(run.Id, PreReviewNode.PhotoReview, NodeStatus.Done, "ok");
        await service.SaveFindingAsync(run.Id, PreReviewNode.PhotoReview, "{\"a\":1}");
        await service.SaveSummaryAsync(run.Id, "approve", hasWarning: true);

        var latest = (await service.GetLatestForRequestAsync(requestId))!;
        var node = Assert.Single(latest.Nodes);
        Assert.Equal(NodeStatus.Done, node.Status);
        Assert.Equal("ok", node.Note);
        Assert.NotNull(node.StartedAt);
        Assert.NotNull(node.EndedAt);
        Assert.Equal("{\"a\":1}", latest.PhotoJson);
        Assert.Equal("approve", latest.Recommendation);
        Assert.True(latest.HasWarning);
        var map = await service.GetLatestByRequestIdsAsync([requestId, requestId + 5]);
        Assert.Equal([requestId], map.Keys);
        // 佇列頁只需要狀態、建議與警示旗標（不載入節點結論 JSON）；有多輪時取最新一輪。
        Assert.Equal(new PreReviewQueueStatus(PreReviewStatus.Pending, "approve", true), map[requestId]);
        var second = (await service.CreateAsync(requestId))!;
        await service.SetStatusAsync(second.Id, PreReviewStatus.Running);
        Assert.Equal(new PreReviewQueueStatus(PreReviewStatus.Running, null, false), (await service.GetLatestByRequestIdsAsync([requestId]))[requestId]);
    }
}

/// <summary>初審服務的併發行為：用檔案型 SQLite（每個操作各自一條連線）才能真實驗證。</summary>
public sealed class PreReviewRunServiceConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "PreReviewTests-" + Guid.NewGuid().ToString("N") + ".db");
    private FileDbContextFactory factory = null!;
    private int requestId;

    public async Task InitializeAsync()
    {
        factory = new FileDbContextFactory(dbPath);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var request = new AfterSalesRequest
        {
            RequestNo = "AS1",
            CaseKey = Guid.NewGuid(),
            Type = AfterSalesType.SizeExchange,
            Status = AfterSalesStatus.Submitted,
            CreatedAt = UtcNow.UtcDateTime,
            UpdatedAt = UtcNow.UtcDateTime,
        };
        db.AfterSalesRequest.Add(request);
        await db.SaveChangesAsync();
        requestId = request.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private PreReviewRunService CreateService()
        => new(factory, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);

    [Fact]
    public async Task RunService_ConcurrentCreate_BothSucceedWithConsecutiveAttempts()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => CreateService().CreateAsync(requestId))));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal([1, 2], results.Select(r => r!.Attempt).OrderBy(x => x));
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.PreReviewRun.CountAsync());
    }

    [Fact]
    public async Task RunService_ConcurrentSetNode_DifferentNodesAllPersist_SameNodeSingleRow()
    {
        var run = (await CreateService().CreateAsync(requestId))!;

        await Task.WhenAll(
            Task.Run(() => CreateService().SetNodeAsync(run.Id, PreReviewNode.PhotoReview, NodeStatus.Running)),
            Task.Run(() => CreateService().SetNodeAsync(run.Id, PreReviewNode.RuleCheck, NodeStatus.Running)),
            Task.Run(() => CreateService().SetNodeAsync(run.Id, PreReviewNode.HistoryReview, NodeStatus.Running)));
        await Task.WhenAll(
            Task.Run(() => CreateService().SetNodeAsync(run.Id, PreReviewNode.Summary, NodeStatus.Running)),
            Task.Run(() => CreateService().SetNodeAsync(run.Id, PreReviewNode.Summary, NodeStatus.Done, "x")));

        await using var db = factory.CreateDbContext();
        var nodes = await db.PreReviewNodeState.Where(x => x.PreReviewRunId == run.Id).OrderBy(x => x.Node).ToListAsync();
        Assert.Equal(
            [PreReviewNode.PhotoReview, PreReviewNode.RuleCheck, PreReviewNode.HistoryReview, PreReviewNode.Summary],
            nodes.Select(x => x.Node));
        Assert.Single(nodes, x => x.Node == PreReviewNode.Summary);
    }

    [Fact]
    public async Task RunService_ConcurrentTryClaim_ExactlyOneWins()
    {
        var run = (await CreateService().CreateAsync(requestId))!;
        await CreateService().SetStatusAsync(run.Id, PreReviewStatus.AwaitingDecision);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => CreateService().TryClaimDecisionAsync(run.Id))));

        Assert.Single(results, r => r);
        Assert.Equal(PreReviewStatus.Executing, (await CreateService().GetAsync(run.Id))!.Status);
    }

    /// <summary>外鍵開啟、並在第一次 SaveChanges 之前（讀取之後）執行一次 <paramref name="beforeFirstSave"/> 的工廠，用來決定性地製造競爭。</summary>
    private sealed class HookedFactory(string path, Func<Task> beforeFirstSave) : IDbContextFactory<BackendDBContext>
    {
        private int fired;

        public int Fired => fired;

        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
                .AddInterceptors(new Hook(this, beforeFirstSave))
                .Options);

        private sealed class Hook(HookedFactory owner, Func<Task> action) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner.fired) == 1)
                {
                    await action();
                }

                return result;
            }
        }
    }

    [Fact]
    public async Task RunService_Create_UniqueCollision_RetriesWithNextAttempt()
    {
        var hooked = new HookedFactory(dbPath, async () =>
        {
            // 在 CreateAsync 讀完 MAX(Attempt) 之後、寫入之前，另一個呼叫搶先建立了 Attempt 1。
            await CreateService().CreateAsync(requestId);
        });
        var service = new PreReviewRunService(hooked, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);

        var run = await service.CreateAsync(requestId);

        Assert.Equal(2, run!.Attempt);
        Assert.True(hooked.Fired >= 2, $"fired={hooked.Fired}");
        await using var db = factory.CreateDbContext();
        Assert.Equal([1, 2], await db.PreReviewRun.OrderBy(x => x.Attempt).Select(x => x.Attempt).ToListAsync());
    }

    [Fact]
    public async Task RunService_Create_NonUniqueFailure_ThrowsWithoutRetry()
    {
        var hooked = new HookedFactory(dbPath, async () =>
        {
            // 申請在讀取之後被刪掉：寫入時外鍵失敗（不是唯一索引衝突），必須立刻拋出、不重試。
            await using var db = factory.CreateDbContext();
            await db.AfterSalesRequest.ExecuteDeleteAsync();
        });
        var service = new PreReviewRunService(hooked, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(requestId));

        Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(1, hooked.Fired);
    }

    private sealed class FileDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=False")
                .Options);
    }
}
