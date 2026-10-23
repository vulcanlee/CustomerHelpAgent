using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Tests;

public sealed class AgentTraceServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private SqliteConnection connection = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private AgentTraceService CreateService() =>
        new(new TestDbContextFactory(connection), new ManualTimeProvider(UtcNow), NullLogger<AgentTraceService>.Instance);

    private async Task<MyUser> AddUserAsync(string name)
    {
        await using var ctx = new TestDbContextFactory(connection).CreateDbContext();
        var user = new MyUser { Account = name.ToLowerInvariant(), Password = "x", Name = name };
        ctx.MyUser.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private sealed class ThrowingFactory : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext() => throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task AppendStep_AssignsIncreasingSequence()
    {
        var service = CreateService();
        var runId = await service.StartRunAsync(Guid.NewGuid(), 1);

        await service.AppendStepAsync(runId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "a", null);
        await service.AppendStepAsync(runId, AgentStepKind.ModelContext, AgentDataSource.Memory, "b", "{}");
        await service.AppendStepAsync(runId, AgentStepKind.Stop, AgentDataSource.Program, "c", null);

        var steps = await service.GetStepsAsync(runId);
        Assert.Equal([1, 2, 3], steps.Select(s => s.Sequence));
    }

    [Fact]
    public async Task GetRuns_SoftDeletedCustomer_StillListed()
    {
        var user = await AddUserAsync("Alice");
        var service = CreateService();
        var runId = await service.StartRunAsync(Guid.NewGuid(), user.Id);
        await service.EndRunAsync(runId, AgentStopReason.Answered, 2, 1);

        await using (var ctx = new TestDbContextFactory(connection).CreateDbContext())
        {
            var u = await ctx.MyUser.SingleAsync(x => x.Id == user.Id);
            SoftDeleteHelper.MarkDeleted(u, "support");
            await ctx.SaveChangesAsync();
        }

        var result = await service.GetRunsAsync(new DataRequest());

        var row = Assert.Single(result.Result);
        Assert.Equal(1, result.Count);
        Assert.Equal("Alice", row.CustomerName);
        Assert.Equal(AgentStopReason.Answered, row.StopReason);
        Assert.Equal(2, row.ModelCallCount);
        Assert.Equal(1, row.ToolCallCount);
        Assert.NotNull(row.EndedAt);
    }

    [Fact]
    public async Task GetRuns_OrdersNewestFirst_AndPages()
    {
        var service = CreateService();
        var a = await service.StartRunAsync(Guid.NewGuid(), 1);
        var b = await service.StartRunAsync(Guid.NewGuid(), 1);
        var c = await service.StartRunAsync(Guid.NewGuid(), 1);

        var page = await service.GetRunsAsync(new DataRequest { CurrentPage = 2, PageSize = 2, Take = 2 });

        Assert.Equal(3, page.Count);
        Assert.Equal([a], page.Result.Select(r => r.Id));
        var first = await service.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 2, Take = 2 });
        Assert.Equal([c, b], first.Result.Select(r => r.Id));
    }

    [Fact]
    public async Task GetSteps_ReturnsInSequence()
    {
        var service = CreateService();
        var runId = await service.StartRunAsync(Guid.NewGuid(), 1);
        var other = await service.StartRunAsync(Guid.NewGuid(), 1);
        await service.AppendStepAsync(runId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "first", null);
        await service.AppendStepAsync(other, AgentStepKind.UserMessage, AgentDataSource.Conversation, "other", null);
        await service.AppendStepAsync(runId, AgentStepKind.ModelReply, AgentDataSource.Memory, "second", "{\"k\":1}");

        var steps = await service.GetStepsAsync(runId);

        Assert.Equal(["first", "second"], steps.Select(s => s.PlainText));
        Assert.Equal("{\"k\":1}", steps[1].RawJson);
        Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 0), steps[0].OccurredAt);
    }

    [Fact]
    public async Task AppendStep_DbFailure_DoesNotThrow()
    {
        var service = new AgentTraceService(new ThrowingFactory(), new ManualTimeProvider(UtcNow), NullLogger<AgentTraceService>.Instance);

        var ex = await Record.ExceptionAsync(() =>
            service.AppendStepAsync(1, AgentStepKind.Stop, AgentDataSource.Program, "x", null));

        Assert.Null(ex);
    }
}
