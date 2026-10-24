using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

/// <summary>M6 劇本執行：建立、埋點蓋上編號、重置保留執行並清空目前執行，以及新劇本的定義。</summary>
public sealed class DemoScenarioRunTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);

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

    private DemoScenarioRunService RunService() => new(factory, new ManualTimeProvider(UtcNow), NullLogger<DemoScenarioRunService>.Instance);

    private ProcessMetricService Metrics(DemoControlService demo) => new(factory, demo, new ManualTimeProvider(UtcNow), NullLogger<ProcessMetricService>.Instance);

    [Fact]
    public async Task PrepareScenario_CreatesRun_AndStampsMetrics()
    {
        var scenario = DemoScenarios.All.Single(x => x.Code == "A-T");
        var run = await RunService().CreateAsync(scenario, isReplay: false);
        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.SetActiveRunAsync(run.Id);

        await Metrics(demo).RecordAsync(Guid.NewGuid(), AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.OpenForm, 5);

        var saved = await RunService().GetByIdAsync(run.Id);
        Assert.NotNull(saved);
        Assert.Equal("A-T", saved!.ScenarioCode);
        Assert.Equal("換尺碼", saved.Family);
        Assert.Equal(ScenarioFlow.Traditional, saved.Flow);
        Assert.Equal(DemoSeedData.Customer1, saved.Account);
        Assert.False(saved.IsReplay);
        Assert.Equal(LocalNow, saved.StartedAt);
        await using var db = factory.CreateDbContext();
        Assert.Equal(run.Id, (await db.ProcessMetric.SingleAsync()).DemoRunId);

        // 重啟後（重新載入狀態）仍會蓋上同一個編號。
        var restarted = TestDemoControl.Create(factory, enabled: true);
        await restarted.LoadAsync();
        Assert.Equal(run.Id, restarted.ActiveRunId);
    }

    [Fact]
    public async Task RecordAsync_WithoutActiveRunOrDemoDisabled_LeavesRunIdNull()
    {
        var demo = TestDemoControl.Create(factory, enabled: true);
        await Metrics(demo).RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, null);

        // 示範模式開啟時設定並存下執行；同一個資料庫改以示範模式關閉的服務載入，目前執行必須被擋掉。
        var run = await RunService().CreateAsync(DemoScenarios.All.Single(x => x.Code == "A"), isReplay: false);
        await demo.SetActiveRunAsync(run.Id);
        var disabled = TestDemoControl.Create(factory, enabled: false);
        await disabled.LoadAsync();
        Assert.Null(disabled.ActiveRunId);
        await Metrics(disabled).RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, null);

        await using var db = factory.CreateDbContext();
        var rows = await db.ProcessMetric.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.Null(x.DemoRunId));
        Assert.Equal(run.Id, (await db.DemoControlState.SingleAsync()).ActiveRunId);
    }

    [Fact]
    public async Task GetByRange_ReturnsRunsInRangeOldestFirst()
    {
        var service = RunService();
        var first = await service.CreateAsync(DemoScenarios.All.Single(x => x.Code == "A"), isReplay: false);
        var second = await service.CreateAsync(DemoScenarios.All.Single(x => x.Code == "H"), isReplay: true);

        var inRange = await service.GetByRangeAsync(LocalNow.AddHours(-1), LocalNow.AddHours(1));
        Assert.Equal([first.Id, second.Id], inRange.Select(x => x.Id));
        Assert.True(inRange[1].IsReplay);
        Assert.Empty(await service.GetByRangeAsync(LocalNow.AddDays(1), LocalNow.AddDays(2)));
        Assert.Null(await service.GetByIdAsync(9999));
    }

    [Fact]
    public async Task Reset_KeepsRuns_ClearsActiveRun()
    {
        var demoSettings = Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" });
        await using (var db = factory.CreateDbContext())
        {
            var rolePermissionService = new RolePermissionService();
            IDatabaseSeeder[] seeders =
            [
                new DefaultRoleViewSeeder(db, rolePermissionService, NullLogger<DefaultRoleViewSeeder>.Instance),
                new SupportUserSeeder(db, Options.Create(new BootstrapSettings { SupportPassword = "support-test-password" }), NullLogger<SupportUserSeeder>.Instance),
                new RbacBackfillSeeder(new RbacBackfillService(db, rolePermissionService, NullLogger<RbacBackfillService>.Instance), NullLogger<RbacBackfillSeeder>.Instance),
                new DemoAccountSeeder(db, new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), demoSettings, NullLogger<DemoAccountSeeder>.Instance),
                new DemoCatalogSeeder(db, demoSettings, new ManualTimeProvider(UtcNow), NullLogger<DemoCatalogSeeder>.Instance),
            ];
            foreach (var seeder in seeders.OrderBy(x => x.Order))
            {
                await seeder.SeedAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }
        }

        var demo = TestDemoControl.Create(factory, enabled: true);
        var run = await RunService().CreateAsync(DemoScenarios.All.Single(x => x.Code == "B"), isReplay: false);
        await demo.SetActiveRunAsync(run.Id);
        await Metrics(demo).RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, null);

        var reset = new DemoResetService(
            factory,
            demo,
            new AfterSalesFileStore(Options.Create(new SystemSettings()), NullLogger<AfterSalesFileStore>.Instance),
            demoSettings,
            new ManualTimeProvider(UtcNow),
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [],
            NullLogger<DemoResetService>.Instance);
        Assert.True((await reset.ResetAsync()).Success);

        Assert.Null(demo.ActiveRunId);
        await using var check = factory.CreateDbContext();
        Assert.Equal(run.Id, (await check.DemoScenarioRun.SingleAsync()).Id);
        Assert.Equal(run.Id, (await check.ProcessMetric.SingleAsync()).DemoRunId);
        Assert.Null((await check.DemoControlState.SingleAsync()).ActiveRunId);
    }

    [Fact]
    public void Scenarios_FamilyAndFlowDefined()
    {
        string Key(string code)
        {
            var s = DemoScenarios.All.Single(x => x.Code == code);
            return s.Family + "/" + s.Flow;
        }

        Assert.Equal("換尺碼/SingleAgent", Key("A"));
        Assert.Equal("一句兩事/SingleAgent", Key("B"));
        Assert.Equal("一句兩事/MultiAgent", Key("H"));
        Assert.Equal("換尺碼/Traditional", Key("A-T"));
        Assert.Equal("一句兩事/Traditional", Key("B-T"));
        Assert.Equal("換尺碼/MultiAgent", Key("A-M"));
        Assert.Equal(AgentMode.Handoff, DemoScenarios.All.Single(x => x.Code == "A-M").Mode);
        Assert.All(DemoScenarios.All.Where(x => x.Flow == ScenarioFlow.Traditional), x => Assert.Equal(AgentMode.Single, x.Mode));
        Assert.All(DemoScenarios.All, x => Assert.False(string.IsNullOrWhiteSpace(x.Family)));
        Assert.Equal(DemoScenarios.All.Count, DemoScenarios.All.Select(x => x.Code).Distinct().Count());
    }
}
