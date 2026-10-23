using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Tests;

/// <summary>展示控制狀態（故障、即時／回放、錄製、幕後面板預設）：存在 SQLite 單列，重啟後載回；示範模式關閉時一律無故障、Live。</summary>
public sealed class DemoControlServiceTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task DemoControl_PersistsAndReloadsFaults()
    {
        var first = TestDemoControl.Create(factory, enabled: true, new ManualTimeProvider(UtcNow));
        await first.LoadAsync();
        Assert.True(first.IsEnabled);
        Assert.Empty(first.ActiveFaults);
        Assert.Equal(DemoMode.Live, first.Mode);
        Assert.Null(first.ActiveRecordingId);
        Assert.True(first.BackstageDefaultOn);
        Assert.Equal(TimeSpan.FromSeconds(30), first.FaultDelay);

        var changed = 0;
        first.Changed += () => changed++;
        await first.SetFaultAsync(DemoFault.OrderServiceTimeout, true);
        await first.SetFaultAsync(DemoFault.PolicyConflict, true);
        await first.SetFaultAsync(DemoFault.OrderServiceTimeout, false);
        await first.SetFaultAsync(DemoFault.PermissionExpired, true);
        await first.SetModeAsync(DemoMode.Replay, 7);
        await first.SetBackstageDefaultAsync(false);
        Assert.Equal(6, changed);

        var second = TestDemoControl.Create(factory, enabled: true);
        await second.LoadAsync();
        Assert.Equal(new HashSet<DemoFault> { DemoFault.PolicyConflict, DemoFault.PermissionExpired }, second.ActiveFaults.ToHashSet());
        Assert.True(second.IsFaultActive(DemoFault.PolicyConflict));
        Assert.False(second.IsFaultActive(DemoFault.OrderServiceTimeout));
        Assert.Equal(DemoMode.Replay, second.Mode);
        Assert.Equal(7, second.ActiveRecordingId);
        Assert.False(second.BackstageDefaultOn);

        await using (var db = factory.CreateDbContext())
        {
            var row = await db.DemoControlState.SingleAsync();
            Assert.Equal(1, row.Id);
            Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 0), row.UpdatedAt);
        }

        // 回到即時模式時不保留錄製；清除故障同樣寫回資料庫。
        await second.SetModeAsync(DemoMode.Live, 7);
        await second.ClearFaultsAsync();
        var third = TestDemoControl.Create(factory, enabled: true);
        await third.LoadAsync();
        Assert.Empty(third.ActiveFaults);
        Assert.Equal(DemoMode.Live, third.Mode);
        Assert.Null(third.ActiveRecordingId);
    }

    [Fact]
    public async Task DemoControl_WhenDemoDisabled_ReportsNoFaults()
    {
        var enabled = TestDemoControl.Create(factory, enabled: true);
        await enabled.LoadAsync();
        foreach (var fault in Enum.GetValues<DemoFault>())
        {
            await enabled.SetFaultAsync(fault, true);
        }

        await enabled.SetModeAsync(DemoMode.Replay, 3);

        var disabled = TestDemoControl.Off(factory);
        await disabled.LoadAsync();

        Assert.False(disabled.IsEnabled);
        Assert.Empty(disabled.ActiveFaults);
        Assert.All(Enum.GetValues<DemoFault>(), fault => Assert.False(disabled.IsFaultActive(fault)));
        Assert.Equal(DemoMode.Live, disabled.Mode);
        Assert.Null(disabled.ActiveRecordingId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.SetFaultAsync(DemoFault.PolicyConflict, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.SetModeAsync(DemoMode.Replay, 3));
    }

    [Fact]
    public async Task DemoControl_ReplayWithoutRecording_IsRejected()
    {
        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => demo.SetModeAsync(DemoMode.Replay, null));
        Assert.Equal(DemoMode.Live, demo.Mode);
    }

    [Fact]
    public async Task DemoControl_UnknownFaultNameInDatabase_IsIgnored()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.DemoControlState.Add(new DemoControlState
            {
                Id = 1,
                FaultsJson = "[\"RemovedFault\",\"PolicyNotFound\"]",
                Mode = DemoMode.Live,
                BackstageDefaultOn = true,
                UpdatedAt = new DateTime(2026, 10, 23),
            });
            await db.SaveChangesAsync();
        }

        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.LoadAsync();

        Assert.Equal([DemoFault.PolicyNotFound], demo.ActiveFaults);
    }

    [Fact]
    public async Task DemoControl_ReplayRowWithoutRecording_LoadsAsLiveWithWarning()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.DemoControlState.Add(new DemoControlState
            {
                Id = 1,
                FaultsJson = "[]",
                Mode = DemoMode.Replay,
                ActiveRecordingId = null,
                BackstageDefaultOn = true,
                UpdatedAt = new DateTime(2026, 10, 23),
            });
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger<DemoControlService>();
        var demo = new DemoControlService(
            factory, Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }), TimeProvider.System, logger);
        await demo.LoadAsync();

        Assert.Equal(DemoMode.Live, demo.Mode);
        Assert.Null(demo.ActiveRecordingId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void FaultLabels_OrderedContainsEveryFaultExactlyOnce()
    {
        Assert.Equal(
            Enum.GetValues<DemoFault>().Order(),
            DemoFaultLabels.Ordered.Order());
        Assert.Equal(DemoFaultLabels.Ordered.Count, DemoFaultLabels.Ordered.Distinct().Count());
    }
}
