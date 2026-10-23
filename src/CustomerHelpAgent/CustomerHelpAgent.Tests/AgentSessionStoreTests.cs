using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Tests;

/// <summary>售後 Agent 會話存放：確認卡作廢的條件式更新、會話狀態的樂觀版本。</summary>
public sealed class AgentSessionStoreTests : IAsyncLifetime
{
    private const int CustomerId = 7;
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

    private AgentSessionStore CreateStore() =>
        new(new TestDbContextFactory(connection), new ManualTimeProvider(UtcNow), NullLogger<AgentSessionStore>.Instance);

    [Fact]
    public async Task VoidPending_DoesNotOverwriteConcurrentClaim()
    {
        var store = CreateStore();
        var key = await store.CreateAsync(CustomerId);
        var confirmation = await store.AddConfirmationAsync(key, CustomerId, "ficc_1", "create_after_sales_request", "{}", "{}");

        // 送出文字的分頁先讀到「未決」，另一個分頁搶先按下確認；之後的作廢不可把「同意」改成「拒絕」。
        Assert.NotNull(await store.GetPendingConfirmationAsync(key, CustomerId));
        Assert.True(await store.TryClaimConfirmationAsync(confirmation.Id, CustomerId, approved: true));

        Assert.False(await store.VoidPendingConfirmationAsync(key, CustomerId));

        await using var db = new TestDbContextFactory(connection).CreateDbContext();
        Assert.True((await db.AgentConfirmation.SingleAsync()).Approved);
    }

    [Fact]
    public async Task VoidPending_VoidsOnlyOwnPendingCard()
    {
        var store = CreateStore();
        var key = await store.CreateAsync(CustomerId);
        await store.AddConfirmationAsync(key, CustomerId, "ficc_1", "create_after_sales_request", "{}", "{}");

        Assert.False(await store.VoidPendingConfirmationAsync(key, CustomerId + 1));
        Assert.True(await store.VoidPendingConfirmationAsync(key, CustomerId));

        Assert.Null(await store.GetPendingConfirmationAsync(key, CustomerId));
        await using var db = new TestDbContextFactory(connection).CreateDbContext();
        var row = await db.AgentConfirmation.SingleAsync();
        Assert.False(row.Approved);
        Assert.NotNull(row.DecidedAt);
    }

    [Fact]
    public async Task SaveState_StaleVersion_Throws()
    {
        var store = CreateStore();
        var key = await store.CreateAsync(CustomerId);
        Assert.Equal(0, (await store.GetAsync(key, CustomerId))!.StateVersion);

        await store.SaveStateAsync(key, CustomerId, "{\"turn\":1}", AgentStopReason.Answered, expectedVersion: 0);

        var conflict = await Assert.ThrowsAsync<AgentSessionConflictException>(() =>
            store.SaveStateAsync(key, CustomerId, "{\"turn\":\"stale\"}", AgentStopReason.Answered, expectedVersion: 0));
        Assert.Equal("這段對話已在其他分頁更新，請重新整理頁面。", conflict.Message);

        var record = (await store.GetAsync(key, CustomerId))!;
        Assert.Equal(1, record.StateVersion);
        Assert.Equal("{\"turn\":1}", record.StateJson);
    }

    [Fact]
    public async Task SaveState_OtherCustomer_ThrowsWithoutWriting()
    {
        var store = CreateStore();
        var key = await store.CreateAsync(CustomerId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveStateAsync(key, CustomerId + 1, "{}", AgentStopReason.Answered, expectedVersion: 0));

        Assert.IsNotType<AgentSessionConflictException>(ex);
        var record = (await store.GetAsync(key, CustomerId))!;
        Assert.Equal(0, record.StateVersion);
        Assert.Equal(string.Empty, record.StateJson);
    }
}
