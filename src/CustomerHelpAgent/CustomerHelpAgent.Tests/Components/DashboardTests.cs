using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Views.Dashboard;
using CustomerHelpAgent.Web.Dashboard;
using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 首頁儀表板（0.9.106 起）：登記與權限過濾（沒權限的不建立）、一個小工具丟例外不影響其他、三個小工具的內容、排序。
/// </summary>
public sealed class DashboardTests : ComponentTestBase
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly int userId;

    public DashboardTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
            var user = new MyUser { Account = "alice", Name = "alice", Password = SecurePasswordHasher.HashPassword("Passw0rd-1"), Status = true };
            context.MyUser.Add(user);
            context.SaveChanges();
            userId = user.Id;
        }

        Services.AddLogging();
        Services.AddSingleton<IDbContextFactory<BackendDBContext>>(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<INotificationSignal, NotificationSignal>();
        Services.AddSingleton<NotificationQueryService>();
        Services.AddSingleton(new CurrentUserService { CurrentUser = { Id = userId, Account = "alice", IsAuthenticated = true } });
        Services.AddSingleton<IAuditLogService>(new RecordingAuditLogService());
        Services.AddSingleton(PasswordTestDefaults.Policy());
        var resolverContext = factory.CreateDbContext();
        Services.AddSingleton<IEffectiveTeamResolver>(new EffectiveTeamResolver(resolverContext, new ContextTeamTreeCache(resolverContext), NullLogger<EffectiveTeamResolver>.Instance));
        Services.AddSingleton<ProfileService>();
        CountingWidget.Created = 0;
    }

    // ---------- 登記與過濾 ----------

    [Fact]
    public void Catalog_ShouldFilterByAdminAndPermission_AndKeepTheOrder()
    {
        var catalog = new DashboardWidgetCatalog(
        [
            new("projects", "專案", 30, MagicObjectHelper.角色_專案項目, false, typeof(CountingWidget), "work"),
            new("admin", "管理", 20, null, true, typeof(CountingWidget), "settings"),
            new("everyone", "大家", 10, null, false, typeof(CountingWidget), "home"),
        ]);

        Assert.Equal(["everyone", "admin", "projects"], catalog.VisibleTo(true, _ => false).Select(x => x.Id));
        Assert.Equal(["everyone"], catalog.VisibleTo(false, _ => false).Select(x => x.Id));
        Assert.Equal(["everyone", "projects"], catalog.VisibleTo(false, key => key == MagicObjectHelper.角色_專案項目).Select(x => x.Id));
        Assert.Throws<InvalidOperationException>(() => new DashboardWidgetCatalog(
            [new("a", "A", 1, null, false, typeof(CountingWidget), "x"), new("A", "A2", 2, null, false, typeof(CountingWidget), "x")]));
    }

    [Fact]
    public void BuiltInWidgets_ShouldBeRegistered_WithTheScheduledJobsOneAdminOnly()
    {
        var catalog = new ServiceCollection().AddDashboard().BuildServiceProvider().GetRequiredService<DashboardWidgetCatalog>();

        Assert.Equal(["notifications", "account", "scheduled-jobs"], catalog.All.Select(x => x.Id));
        Assert.Equal([typeof(MyNotificationsWidget), typeof(MyAccountWidget), typeof(ScheduledJobsWidget)], catalog.All.Select(x => x.ComponentType));
        Assert.Equal(["notifications", "account"], catalog.VisibleTo(false, _ => true).Select(x => x.Id));
    }

    /// <summary>⭐ 沒權限的小工具連元件都不建立（不會查資料）；壞掉的那一個只影響自己的卡片。</summary>
    [Fact]
    public void Grid_ShouldOnlyCreateVisibleWidgets_AndIsolateAFailingOne()
    {
        var catalog = new DashboardWidgetCatalog(
        [
            new("ok", "正常", 10, null, false, typeof(CountingWidget), "home"),
            new("boom", "壞掉", 20, null, false, typeof(ThrowingWidget), "error"),
            new("hidden", "管理", 30, null, true, typeof(CountingWidget), "settings"),
        ]);

        var grid = Render<DashboardGrid>(p => p.Add(x => x.Widgets, catalog.VisibleTo(false, _ => false)));

        Assert.Equal(1, CountingWidget.Created);
        Assert.Equal(2, grid.FindAll(".dashboard-card").Count);
        Assert.Contains("計數小工具", grid.Find("[data-widget='ok']").TextContent, StringComparison.Ordinal);
        Assert.Contains("這個區塊暫時無法顯示", grid.Find("[data-widget='boom']").TextContent, StringComparison.Ordinal);
        Assert.Empty(grid.FindAll("[data-widget='hidden']"));
    }

    // ---------- 小工具 ----------

    [Fact]
    public void Notifications_ShouldShowUnreadAndLatestFive_UpdateOnSignal_AndUnsubscribeOnDispose()
    {
        for (var i = 1; i <= 6; i++)
        {
            AddNotification($"通知{i}", read: i == 1);
        }

        var widget = Render<MyNotificationsWidget>();
        widget.WaitForAssertion(() => Assert.Equal("5", widget.Find(".dashboard-stat-number").TextContent));
        Assert.Equal(MyNotificationsWidget.ListSize, widget.FindAll(".dashboard-list-item").Count);
        Assert.Contains("通知6", widget.FindAll(".dashboard-list-item")[0].TextContent, StringComparison.Ordinal);

        AddNotification("通知7", read: false);
        var signal = (NotificationSignal)Services.GetRequiredService<INotificationSignal>();
        signal.Publish([userId]);
        widget.WaitForAssertion(() => Assert.Equal("6", widget.Find(".dashboard-stat-number").TextContent));

        Assert.Equal(1, signal.SubscriberCount);
        widget.Instance.Dispose();
        Assert.Equal(0, signal.SubscriberCount);
    }

    [Fact]
    public void Notifications_ShouldPollForChangesFromOtherProcesses_AndStopPollingOnDispose()
    {
        // 0.9.122 起：別的行程寫入的通知不會發訊號，靠每 60 秒輪詢補上（與鈴鐺相同）。
        var clock = new TriggerableTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        AddNotification("通知1", read: false);

        var widget = Render<MyNotificationsWidget>();
        widget.WaitForAssertion(() => Assert.Equal("1", widget.Find(".dashboard-stat-number").TextContent));
        var timer = Assert.Single(clock.Timers);

        AddNotification("別台主機發的", read: false); // 沒有發訊號
        timer.Fire();
        widget.WaitForAssertion(() => Assert.Equal("2", widget.Find(".dashboard-stat-number").TextContent));

        widget.Instance.Dispose();
        widget.WaitForAssertion(() => Assert.True(timer.Disposed));
    }

    [Fact]
    public void Account_ShouldShowThePreviousLogin_NotThisOne_AndTheTwoFactorState()
    {
        AddAudit(AuditActions.Login.Success, DateTime.UtcNow.AddDays(-3));
        AddAudit(AuditActions.Login.SsoSuccess, DateTime.UtcNow.AddDays(-1), clientIp: "198.51.100.7");
        AddAudit(AuditActions.Login.Failed, DateTime.UtcNow.AddHours(-2), success: false);
        AddAudit(AuditActions.Login.Success, DateTime.UtcNow);

        var widget = Render<MyAccountWidget>();

        widget.WaitForAssertion(() => Assert.Contains("Google 登入", widget.Markup, StringComparison.Ordinal));
        Assert.Contains("198.51.100.7", widget.Markup, StringComparison.Ordinal); // 0.9.123 起顯示上一次登入的來源 IP
        Assert.Contains("未啟用", widget.Markup, StringComparison.Ordinal);
        Assert.Contains("不會到期", widget.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousLogin_ShouldSkipTheLatestSuccess_AndIgnoreOthers()
    {
        var service = Services.GetRequiredService<ProfileService>();
        Assert.Null(await service.GetPreviousLoginAsync(userId));

        AddAudit(AuditActions.Login.Success, DateTime.UtcNow.AddDays(-2));
        Assert.Null(await service.GetPreviousLoginAsync(userId));

        AddAudit(AuditActions.Login.Success, DateTime.UtcNow.AddMinutes(-1));
        AddAudit(AuditActions.Login.Failed, DateTime.UtcNow, success: false);
        AddAudit(AuditActions.Login.Success, DateTime.UtcNow, actor: userId + 100);
        var previous = await service.GetPreviousLoginAsync(userId);
        Assert.NotNull(previous);
        Assert.True(previous!.OccurredAtUtc < DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public void ScheduledJobAttention_ShouldListFailedInterruptedOverdueAndDisabledJobs()
    {
        static ScheduledJobOverviewItem Item(string name, bool enabled = true, string? lastStatus = null, bool overdue = false)
            => new(name, name, string.Empty, "0 3 * * *", enabled, null, null,
                lastStatus is null ? null : new JobRunAdapterModel { JobName = name, Status = lastStatus },
                false, overdue ? DateTime.UtcNow.AddHours(-2) : null);

        var overview = new ScheduledJobOverview(true, null,
        [
            Item("正常", lastStatus: JobRunStatuses.Succeeded),
            Item("失敗", lastStatus: JobRunStatuses.Failed),
            Item("中斷", lastStatus: JobRunStatuses.Interrupted),
            Item("逾期", overdue: true),
            Item("停用", enabled: false, lastStatus: JobRunStatuses.Failed),
        ]);

        Assert.Equal(
            [("失敗", ScheduledJobAttention.FailedReason), ("中斷", ScheduledJobAttention.InterruptedReason), ("逾期", ScheduledJobAttention.OverdueReason), ("停用", ScheduledJobAttention.DisabledReason)],
            ScheduledJobAttention.From(overview).Select(x => (x.DisplayName, x.Reason)));
    }

    // ---------- helpers ----------

    private void AddNotification(string title, bool read)
    {
        using var context = factory.CreateDbContext();
        context.Notification.Add(new Notification { RecipientUserId = userId, Category = "Test", Title = title, CreatedAtUtc = DateTime.UtcNow.AddSeconds(context.Notification.Count()), ReadAtUtc = read ? DateTime.UtcNow : null });
        context.SaveChanges();
    }

    private void AddAudit(string action, DateTime occurredAtUtc, bool success = true, int? actor = null, string? clientIp = null)
    {
        using var context = factory.CreateDbContext();
        context.AuditLog.Add(new AuditLog { Action = action, OccurredAt = occurredAtUtc, Success = success, ActorUserId = actor ?? userId, ActorAccount = "alice", ClientIp = clientIp });
        context.SaveChanges();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
        }
    }

    /// <summary>算建立了幾次（確認沒權限的不會被建立）。</summary>
    public sealed class CountingWidget : ComponentBase
    {
        public static int Created { get; set; }

        protected override void OnInitialized() => Created++;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, "計數小工具");
    }

    public sealed class ThrowingWidget : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("widget failed on purpose");
    }
}
