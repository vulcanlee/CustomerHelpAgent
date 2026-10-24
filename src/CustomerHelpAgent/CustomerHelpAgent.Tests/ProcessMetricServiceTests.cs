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

        var service = new ProcessMetricService(new TestDbContextFactory(conn), TestDemoControl.Off(new TestDbContextFactory(conn)), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);
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
    public async Task RecordAgentRequestCreated_RequestNotFound_StillRecordedWithoutId()
    {
        // 查不到申請編號（例如已被重置刪掉）：仍記一筆，申請筆數不少算；Actor＝顧客、管道照傳入值。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var service = new ProcessMetricService(new TestDbContextFactory(conn), TestDemoControl.Off(new TestDbContextFactory(conn)), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);
        var caseKey = Guid.NewGuid();

        await service.RecordAgentRequestCreatedAsync(caseKey, AfterSalesChannel.AgentHandoff, "AS202610230099", 5);

        await using var check = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options);
        var row = await check.ProcessMetric.SingleAsync();
        Assert.Equal(ProcessMetricActions.AgentRequestCreated, row.Action);
        Assert.Equal(ProcessActor.Customer, row.Actor);
        Assert.Equal(AfterSalesChannel.AgentHandoff, row.Channel);
        Assert.Equal(caseKey, row.CaseKey);
        Assert.Null(row.AfterSalesRequestId);
    }

    [Fact]
    public async Task RecordAgentRequestCreated_OtherCustomersRequest_NotLinked()
    {
        // 申請編號只在同一位顧客的申請裡查：別人的同號申請不可被連到這筆埋點（縱深防禦）。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options))
        {
            await ctx.Database.EnsureCreatedAsync();
            // 只測查詢條件，不建訂單與顧客的整串外鍵資料。
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            ctx.AfterSalesRequest.Add(new AccessDatas.Models.AfterSalesRequest
            {
                RequestNo = "AS202610230001",
                CaseKey = Guid.NewGuid(),
                CustomerUserId = 7,
                SalesOrderItemId = 1,
                Description = "測試",
                CreatedAt = LocalNow,
                UpdatedAt = LocalNow,
            });
            await ctx.SaveChangesAsync();
        }

        var service = new ProcessMetricService(new TestDbContextFactory(conn), TestDemoControl.Off(new TestDbContextFactory(conn)), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);

        await service.RecordAgentRequestCreatedAsync(Guid.NewGuid(), AfterSalesChannel.Agent, "AS202610230001", 5);
        await service.RecordAgentRequestCreatedAsync(Guid.NewGuid(), AfterSalesChannel.Agent, "AS202610230001", 7);

        await using var check = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options);
        var rows = await check.ProcessMetric.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].AfterSalesRequestId);
        Assert.NotNull(rows[1].AfterSalesRequestId);
    }

    [Fact]
    public async Task ProcessMetricService_Record_WhenDatabaseFails_ShouldNotThrow()
    {
        // 沒有建表：寫入一定失敗，但埋點不可以中斷主要流程。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var service = new ProcessMetricService(new TestDbContextFactory(conn), TestDemoControl.Off(new TestDbContextFactory(conn)), new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);

        await service.RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, null);
    }
}
