using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;

namespace CustomerHelpAgent.Tests;

/// <summary>M6 假設值：預設值、儲存與稽核、不合理的值被拒絕、重置不刪除（重置的驗證在 DemoResetServiceTests）。</summary>
public sealed class DemoAssumptionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);

    private readonly RecordingAuditLogService audit = new();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private DemoAssumptionService Create() => new(
        factory,
        new ManualTimeProvider(UtcNow),
        audit,
        new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin" } },
        NullLogger<DemoAssumptionService>.Instance);

    private static DemoAssumptionInput Valid() => new(240, 30, 2.0, 0.5, 5, 8, 1000);

    [Fact]
    public async Task Get_ReturnsDefaultsWhenEmpty()
    {
        var view = await Create().GetAsync();

        Assert.Equal(240, view.QueueWaitMinutesTraditional);
        Assert.Equal(30, view.QueueWaitMinutesAi);
        Assert.Equal(2.0, view.ContactsPerCaseTraditional);
        Assert.Equal(0.5, view.ContactsPerCaseAi);
        Assert.Equal(5, view.MinutesPerContact);
        Assert.Equal(8, view.StaffCostPerMinuteTwd);
        Assert.Equal(1000, view.MonthlyCaseVolume);
        Assert.Null(view.UpdatedAt);
        Assert.Null(view.UpdatedByUserId);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.DemoAssumption.CountAsync());
    }

    [Fact]
    public async Task Save_Persists_AndAudits()
    {
        var result = await Create().SaveAsync(Valid() with { QueueWaitMinutesAi = 15.5, MonthlyCaseVolume = 2500 });

        Assert.True(result.Success, result.Message);
        Assert.Equal(15.5, result.Assumption.QueueWaitMinutesAi);
        Assert.Equal(2500, result.Assumption.MonthlyCaseVolume);
        var reloaded = await Create().GetAsync();
        Assert.Equal(15.5, reloaded.QueueWaitMinutesAi);
        Assert.Equal(2500, reloaded.MonthlyCaseVolume);
        Assert.Equal(LocalNow, reloaded.UpdatedAt);
        Assert.Equal(7, reloaded.UpdatedByUserId);

        // 再存一次仍是單列。
        Assert.True((await Create().SaveAsync(Valid())).Success);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.DemoAssumption.CountAsync());
        Assert.Equal(30, (await Create().GetAsync()).QueueWaitMinutesAi);

        Assert.Equal(2, audit.Entries.Count);
        var first = audit.Entries[0];
        Assert.Equal(AuditActions.Demo.AssumptionChanged, first.Action);
        Assert.True(first.Success);
        Assert.Equal(7, first.ActorUserId);
        Assert.Contains("QueueWaitMinutesAi=30→15.5", first.Detail);
        Assert.Contains("MonthlyCaseVolume=1000→2500", first.Detail);
    }

    public static TheoryData<DemoAssumptionInput> InvalidInputs()
    {
        var v = Valid();
        return new TheoryData<DemoAssumptionInput>
        {
            v with { QueueWaitMinutesTraditional = -1 },
            v with { QueueWaitMinutesAi = 10081 },
            v with { MinutesPerContact = -0.1 },
            v with { MinutesPerContact = double.NaN },
            v with { ContactsPerCaseTraditional = -1 },
            v with { ContactsPerCaseAi = 50.1 },
            v with { StaffCostPerMinuteTwd = -1 },
            v with { StaffCostPerMinuteTwd = double.PositiveInfinity },
            v with { MonthlyCaseVolume = 0 },
            v with { MonthlyCaseVolume = -5 },
            v with { MonthlyCaseVolume = 1_000_001 },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Assumption_InvalidValues_Rejected(DemoAssumptionInput input)
    {
        var result = await Create().SaveAsync(input);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(240, result.Assumption.QueueWaitMinutesTraditional);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.DemoAssumption.CountAsync());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Assumption_BoundaryValues_Accepted()
    {
        var result = await Create().SaveAsync(new DemoAssumptionInput(0, 10080, 0, 50, 0, 0, 1));
        Assert.True(result.Success, result.Message);

        result = await Create().SaveAsync(Valid() with { MonthlyCaseVolume = 1_000_000 });
        Assert.True(result.Success, result.Message);
    }
}
