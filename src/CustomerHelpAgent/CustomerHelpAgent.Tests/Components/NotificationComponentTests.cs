using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 鈴鐺與公告橫幅（0.9.100 起）：訊號一到不重新整理就更新數字、全部標為已讀、關閉公告後下一次連線也不再出現。
/// </summary>
public sealed class NotificationComponentTests : ComponentTestBase
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly int userId;

    public NotificationComponentTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
            var user = new MyUser { Account = "alice", Name = "alice", Password = "x", Status = true };
            context.MyUser.Add(user);
            context.SaveChanges();
            userId = user.Id;
        }

        Services.AddLogging();
        Services.AddSingleton<IDbContextFactory<BackendDBContext>>(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<INotificationSignal, NotificationSignal>();
        Services.AddSingleton<NotificationQueryService>();
        Services.AddSingleton<AnnouncementService>();
        Services.AddSingleton<AnnouncementCache>();
        Services.AddSingleton(new CurrentUserService { CurrentUser = { Id = userId, Account = "alice" } });
    }

    [Fact]
    public async Task Bell_ShouldUpdateOnSignal_AndMarkAllRead()
    {
        AddNotification("第一則");
        var bell = Render<NotificationBell>();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find(".notification-bell-badge").TextContent));

        // 別的地方發了通知：只發訊號，元件自己重查，不必重新整理頁面。
        AddNotification("第二則");
        Services.GetRequiredService<INotificationSignal>().Publish([userId]);
        bell.WaitForAssertion(() => Assert.Equal("2", bell.Find(".notification-bell-badge").TextContent));

        await bell.Find(".notification-bell-trigger").ClickAsync(new());
        bell.WaitForAssertion(() => Assert.Equal(2, bell.FindAll(".notification-bell-item-unread").Count));

        await bell.Find(".notification-bell-mark-all").ClickAsync(new());
        bell.WaitForAssertion(() =>
        {
            Assert.Empty(bell.FindAll(".notification-bell-badge"));
            Assert.Empty(bell.FindAll(".notification-bell-item-unread"));
        });
    }

    [Fact]
    public async Task Bell_DeleteOne_AndClearRead_ShouldOnlyRemoveThoseNotifications()
    {
        // 0.9.125 起：× 刪除單則；「清除已讀」只刪已讀的，未讀的留著。
        AddNotification("第一則");
        AddNotification("第二則", read: true);
        AddNotification("第三則", read: true);
        var bell = Render<NotificationBell>();
        await bell.Find(".notification-bell-trigger").ClickAsync(new());
        bell.WaitForAssertion(() => Assert.Equal(3, bell.FindAll(".notification-bell-row").Count));

        await bell.FindAll(".notification-bell-delete")[0].ClickAsync(new());
        bell.WaitForAssertion(() => Assert.Equal(2, bell.FindAll(".notification-bell-row").Count));

        await bell.Find(".notification-bell-clear-read").ClickAsync(new());
        bell.WaitForAssertion(() =>
        {
            var row = Assert.Single(bell.FindAll(".notification-bell-row"));
            Assert.NotNull(row.QuerySelector(".notification-bell-item-unread"));
            Assert.True(bell.Find(".notification-bell-clear-read").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task Bell_ClearRead_ShouldBeClickable_WhenOnlyOlderNotificationsAreRead()
    {
        // 0.9.136 起看「有沒有任何已讀的」，不只看下拉顯示的最新 10 則（之前最新 10 則全未讀時按不了）。
        AddNotification("很久以前、已讀", read: true, createdAtUtc: DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 10; i++)
        {
            AddNotification($"未讀 {i}");
        }

        var bell = Render<NotificationBell>();
        await bell.Find(".notification-bell-trigger").ClickAsync(new());
        bell.WaitForAssertion(() => Assert.Equal(10, bell.FindAll(".notification-bell-item-unread").Count));
        Assert.False(bell.Find(".notification-bell-clear-read").HasAttribute("disabled"));

        await bell.Find(".notification-bell-clear-read").ClickAsync(new());
        bell.WaitForAssertion(() => Assert.True(bell.Find(".notification-bell-clear-read").HasAttribute("disabled")));
        using var check = factory.CreateDbContext();
        Assert.Equal(10, check.Notification.Count(x => x.RecipientUserId == userId));
    }

    [Fact]
    public async Task Banner_Dismiss_ShouldHideIt_AndStayHiddenForTheNextConnection()
    {
        using (var context = factory.CreateDbContext())
        {
            context.Announcement.Add(new Announcement
            {
                Title = "系統維護",
                Content = "今晚 22:00",
                StartAtUtc = DateTime.UtcNow.AddHours(-1),
                TargetKind = AnnouncementTargetKinds.All,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ConcurrencyStamp = "s",
            });
            context.SaveChanges();
        }

        var banner = Render<AnnouncementBanner>();
        banner.WaitForAssertion(() => Assert.Contains("系統維護", banner.Markup, StringComparison.Ordinal));

        await banner.Find(".announcement-banner-dismiss").ClickAsync(new());
        banner.WaitForAssertion(() => Assert.Empty(banner.FindAll(".announcement-banner")));

        var next = Render<AnnouncementBanner>();
        next.WaitForAssertion(() => Assert.Empty(next.FindAll(".announcement-banner")));
        using var check = factory.CreateDbContext();
        Assert.Equal(1, check.AnnouncementDismissal.Count(x => x.MyUserId == userId));
    }

    private void AddNotification(string title, bool read = false, DateTime? createdAtUtc = null)
    {
        using var context = factory.CreateDbContext();
        context.Notification.Add(new Notification { RecipientUserId = userId, Category = "Test", Title = title, CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow, ReadAtUtc = read ? DateTime.UtcNow : null });
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
}
