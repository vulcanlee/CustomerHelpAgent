using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

public sealed class ProcessMetricServiceTests
{
    // UTC 02:00 = 本地（UTC+8）10:00
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);

    [Fact]
    public async Task ProcessMetricService_Record_ShouldPersistWithCaseKey()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var service = new ProcessMetricService(new TestDbContextFactory(conn), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);
        var caseKey = Guid.NewGuid();

        await service.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.OpenForm, 5);
        await service.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Staff, ProcessMetricActions.Approve, 9, requestId: 3);

        await using var check = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options);
        var rows = await check.ProcessMetric.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(caseKey, r.CaseKey));
        Assert.All(rows, r => Assert.Equal(LocalNow, r.OccurredAt));
        Assert.Equal("OpenForm", rows[0].Action);
        Assert.Equal(ProcessActor.Customer, rows[0].Actor);
        Assert.Equal(5, rows[0].UserId);
        Assert.Null(rows[0].AfterSalesRequestId);
        Assert.Equal("Approve", rows[1].Action);
        Assert.Equal(3, rows[1].AfterSalesRequestId);
    }

    [Fact]
    public async Task ProcessMetricService_Record_WhenDatabaseFails_ShouldNotThrow()
    {
        // 沒有建表：寫入一定失敗，但埋點不可以中斷主要流程。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var service = new ProcessMetricService(new TestDbContextFactory(conn), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);

        await service.RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, null);
    }
}
