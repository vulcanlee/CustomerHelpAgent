using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Layout;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 站內通知與公告（0.9.100 起）：收件人展開（角色含主要角色、排除已刪除角色、團隊與列級權控同一個定義、只算啟用帳號）、去重、即時訊號、
/// 寄信規則、不丟例外、標為已讀只動自己的、公告的時間與對象、個人關閉、並行衝突、刪除使用者連帶刪除。
/// </summary>
public sealed class NotificationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    public NotificationTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    // ---------- 收件人 ----------

    [Fact]
    public async Task Users_And_Admins_ShouldOnlyIncludeActiveAccounts()
    {
        var active = AddUser("active");
        var disabled = AddUser("disabled", status: false);
        var deleted = AddUser("deleted", isDeleted: true);
        var admin = AddUser("admin", isAdmin: true);
        AddUser("disabled-admin", isAdmin: true, status: false);
        var (sender, signal, _) = NewSender();

        await sender.SendAsync(Request(NotificationTarget.Users(active, disabled, deleted)));
        await sender.SendAsync(Request(NotificationTarget.AllAdmins()));

        Assert.Equal(new[] { active, admin }.Order(), Recipients().Order());
        Assert.Equal(new[] { active, admin }.Order(), signal.Published.Order());
    }

    [Fact]
    public async Task Role_ShouldIncludePrimaryAndAdditionalMembers_ButNotADeletedRole()
    {
        var role = AddRole("業務");
        var deletedRole = AddRole("舊角色", isDeleted: true);
        var primary = AddUser("primary", roleId: role);
        var additional = AddUser("additional");
        AddUserRole(additional, role);
        var outsider = AddUser("outsider");
        AddUserRole(outsider, deletedRole);
        var (sender, _, _) = NewSender();

        await sender.SendAsync(Request(NotificationTarget.Role(role)));
        await sender.SendAsync(Request(NotificationTarget.Role(deletedRole)));

        Assert.Equal(new[] { primary, additional }.Order(), Recipients().Order());
    }

    [Fact]
    public async Task Team_ShouldIncludeDirectMembersAndRoleDefaultTeams()
    {
        var team = AddTeam("北區");
        var direct = AddUser("direct");
        AddUserTeam(direct, team);
        var role = AddRole("北區主管", defaultTeams: ["北區"]);
        var viaRole = AddUser("via-role", roleId: role);
        AddUser("other");
        var (sender, _, _) = NewSender();

        var result = await sender.SendAsync(Request(NotificationTarget.Team(team)));

        Assert.Equal(2, result.Recipients);
        Assert.Equal(new[] { direct, viaRole }.Order(), Recipients().Order());
    }

    [Fact]
    public async Task TeamReverseLookup_ShouldAgreeWithEffectiveTeams_ForEveryUserAndTeam()
    {
        // 「發給團隊」與「列級權控」必須是同一個定義：u 在 GetUserIdsInTeamAsync(t) ⇔ t 在 u 的有效團隊。
        var north = AddTeam("北區");
        var south = AddTeam("南區");
        var removed = AddTeam("撤銷", isDeleted: true);
        var managerRole = AddRole("北區主管", defaultTeams: ["北區", "南區"]);
        var oldRole = AddRole("舊角色", defaultTeams: ["北區"], isDeleted: true);
        var users = new[]
        {
            AddUser("u1"), AddUser("u2", roleId: managerRole), AddUser("u3"), AddUser("u4", roleId: oldRole), AddUser("u5"),
        };
        AddUserTeam(users[0], north);
        AddUserTeam(users[2], south);
        AddUserTeam(users[2], removed);
        AddUserRole(users[4], managerRole);

        await using var context = factory.CreateDbContext();
        var resolver = new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance);
        foreach (var teamName in new[] { "北區", "南區", "撤銷" })
        {
            var members = await resolver.GetUserIdsInTeamAsync(teamName);
            foreach (var user in users)
            {
                var effective = await resolver.GetEffectiveTeamNamesAsync(user);
                Assert.True(
                    members.Contains(user) == effective.Contains(teamName, StringComparer.OrdinalIgnoreCase),
                    $"使用者 {user} 與團隊 {teamName}：反向查詢 {members.Contains(user)}、有效團隊 {string.Join(",", effective)}");
            }
        }
    }

    [Fact]
    public async Task SourceKey_ShouldPreventDuplicates_PerRecipient()
    {
        var admin = AddUser("admin", isAdmin: true);
        var (sender, _, _) = NewSender();

        await sender.SendAsync(Request(NotificationTarget.AllAdmins(), sourceKey: "AccountPending:9"));
        var second = await sender.SendAsync(Request(NotificationTarget.AllAdmins(), sourceKey: "AccountPending:9"));
        var newAdmin = AddUser("admin2", isAdmin: true);
        var third = await sender.SendAsync(Request(NotificationTarget.AllAdmins(), sourceKey: "AccountPending:9"));

        Assert.Equal(0, second.Recipients);
        Assert.Equal(1, third.Recipients);
        Assert.Equal(new[] { admin, newAdmin }.Order(), Recipients().Order());
    }

    [Fact]
    public async Task SourceKey_ShouldStillPreventDuplicates_AfterTheNotificationIsDeleted()
    {
        // 0.9.136 起去重看 NotificationDispatch：使用者按 ×、清除已讀，或已讀的被提早清除，同一個鍵都不會再發（之前會重發，AI 用量 100% 還會重寄信）。
        var admin = AddUser("admin", isAdmin: true);
        var (sender, _, _) = NewSender();
        await sender.SendAsync(Request(NotificationTarget.AllAdmins(), sourceKey: "AiQuota:global:monthly:2026-10:100"));

        await using (var context = factory.CreateDbContext())
        {
            context.Notification.RemoveRange(context.Notification);
            await context.SaveChangesAsync();
        }

        var again = await sender.SendAsync(Request(NotificationTarget.AllAdmins(), sourceKey: "AiQuota:global:monthly:2026-10:100"));

        Assert.Equal(0, again.Recipients);
        Assert.Equal(0, CountNotifications());
        Assert.NotEqual(0, admin);
    }

    [Fact]
    public async Task RetentionJob_ShouldPurgeDispatchRecordsByCreationRetentionOnly()
    {
        var user = AddUser("u");
        await using (var context = factory.CreateDbContext())
        {
            context.NotificationDispatch.Add(new NotificationDispatch { SourceKey = "old", RecipientUserId = user, SentAtUtc = DateTime.UtcNow.AddDays(-91) });
            context.NotificationDispatch.Add(new NotificationDispatch { SourceKey = "recent", RecipientUserId = user, SentAtUtc = DateTime.UtcNow.AddDays(-10) });
            await context.SaveChangesAsync();
        }

        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var context0 = new ScheduledJobContext(1, JobRunTriggers.Schedule, DateTime.UtcNow, null);
        await new NotificationRetentionJob(query, new RecordingAuditLogService(), new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 90, ReadRetentionDays = 7 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(context0, CancellationToken.None);

        await using var check = factory.CreateDbContext();
        Assert.Equal(["recent"], await check.NotificationDispatch.Select(x => x.SourceKey).ToListAsync());
    }

    [Fact]
    public async Task AlsoEmail_ShouldPassRecipientEmailsToTheMailer_OnlyWhenRequested()
    {
        var withEmail = AddUser("a", email: "a@example.com");
        var withoutEmail = AddUser("b");
        var (sender, _, mailer) = NewSender();

        await sender.SendAsync(Request(NotificationTarget.Users(withEmail, withoutEmail)));
        Assert.Empty(mailer.Sent);

        await sender.SendAsync(Request(NotificationTarget.Users(withEmail, withoutEmail)) with { AlsoEmail = true });
        Assert.Equal(["a@example.com"], Assert.Single(mailer.Sent));
    }

    [Fact]
    public async Task SendFailure_ShouldNotThrow()
    {
        AddUser("a");
        var (sender, _, _) = NewSender();
        connection.Close();

        var result = await sender.SendAsync(Request(NotificationTarget.AllAdmins()));

        Assert.True(result.Failed);
    }

    // ---------- 鈴鐺 ----------

    [Fact]
    public async Task MarkRead_ShouldOnlyAffectTheCurrentUsersNotifications()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var (sender, _, _) = NewSender();
        await sender.SendAsync(Request(NotificationTarget.Users(alice, bob)));
        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var bobsNotification = (await query.GetLatestAsync(bob)).Single();

        await query.MarkReadAsync(alice, bobsNotification.Id);
        Assert.Equal(1, await query.GetUnreadCountAsync(bob));

        await query.MarkAllReadAsync(alice);
        Assert.Equal(0, await query.GetUnreadCountAsync(alice));
        Assert.Equal(1, await query.GetUnreadCountAsync(bob));
    }

    [Fact]
    public async Task MarkRead_ShouldSignalTheUser_OnlyWhenSomethingChanged()
    {
        // 0.9.122 起：標為已讀會通知同一個人的鈴鐺、首頁小工具與其他分頁一起更新；沒改到資料就不發。
        var alice = AddUser("alice");
        var (sender, _, _) = NewSender();
        await sender.SendAsync(Request(NotificationTarget.Users(alice)));
        await sender.SendAsync(Request(NotificationTarget.Users(alice)));
        var signal = new NotificationSignal(NullLogger<NotificationSignal>.Instance);
        var hits = 0;
        using var subscription = signal.Subscribe(alice, () => { Interlocked.Increment(ref hits); return Task.CompletedTask; });
        var query = new NotificationQueryService(factory, signal, TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var first = (await query.GetLatestAsync(alice))[0];

        await query.MarkReadAsync(alice, first.Id);
        await WaitUntilAsync(() => hits == 1);
        await query.MarkReadAsync(alice, first.Id); // 已讀的再標一次：不發
        Assert.Equal(1, await query.MarkAllReadAsync(alice));
        await WaitUntilAsync(() => hits == 2);
        Assert.Equal(0, await query.MarkAllReadAsync(alice)); // 全部已讀：不發

        await Task.Delay(100);
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task Delete_ShouldOnlyAffectTheCurrentUsersNotifications_AndSignalOnlyWhenSomethingChanged()
    {
        // 0.9.125 起：刪除單則、清除已讀都只動自己的；清除已讀不碰未讀的；有刪到才發訊號。
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var (sender, _, _) = NewSender();
        await sender.SendAsync(Request(NotificationTarget.Users(alice, bob)));
        await sender.SendAsync(Request(NotificationTarget.Users(alice)));
        await sender.SendAsync(Request(NotificationTarget.Users(alice)));
        var signal = new NotificationSignal(NullLogger<NotificationSignal>.Instance);
        var hits = 0;
        using var subscription = signal.Subscribe(alice, () => { Interlocked.Increment(ref hits); return Task.CompletedTask; });
        var query = new NotificationQueryService(factory, signal, TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var bobsNotification = (await query.GetLatestAsync(bob)).Single();

        Assert.False(await query.DeleteAsync(alice, bobsNotification.Id));
        Assert.Single(await query.GetLatestAsync(bob));

        var alices = await query.GetLatestAsync(alice);
        Assert.True(await query.DeleteAsync(alice, alices[0].Id));
        await WaitUntilAsync(() => hits == 1);
        Assert.False(await query.DeleteAsync(alice, alices[0].Id)); // 已刪除：不發

        await query.MarkReadAsync(alice, alices[1].Id);
        await WaitUntilAsync(() => hits == 2);
        await query.MarkAllReadAsync(bob);
        Assert.Equal(1, await query.DeleteReadAsync(alice));
        await WaitUntilAsync(() => hits == 3);
        Assert.Equal(0, await query.DeleteReadAsync(alice)); // 沒有已讀：不發

        var remaining = Assert.Single(await query.GetLatestAsync(alice));
        Assert.Equal(alices[2].Id, remaining.Id);
        Assert.Null(remaining.ReadAtUtc);
        Assert.Single(await query.GetLatestAsync(bob)); // bob 已讀的不受 alice 清除影響

        await Task.Delay(100);
        Assert.Equal(3, hits);
    }

    [Fact]
    public async Task Latest_ShouldBeNewestFirst_AndPurgeShouldRemoveOldOnes()
    {
        var user = AddUser("u");
        await using (var context = factory.CreateDbContext())
        {
            for (var i = 0; i < 12; i++)
            {
                context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = $"n{i}", CreatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i) });
            }

            await context.SaveChangesAsync();
        }

        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var latest = await query.GetLatestAsync(user, 10);
        Assert.Equal(10, latest.Count);
        Assert.Equal("n11", latest[0].Title);

        var removed = await query.PurgeBeforeAsync(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal(4, removed);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void Badge_ShouldCapAt99(int count, string expected) => Assert.Equal(expected, NotificationBell.BadgeText(count));

    // ---------- 即時訊號 ----------

    [Fact]
    public async Task Signal_ShouldOnlyReachTheTargetUser_AndUnsubscribeCompletely()
    {
        var signal = new NotificationSignal(NullLogger<NotificationSignal>.Instance);
        var aliceHits = 0;
        var bobHits = 0;
        var alice = signal.Subscribe(1, () => { Interlocked.Increment(ref aliceHits); return Task.CompletedTask; });
        var bob = signal.Subscribe(2, () => { Interlocked.Increment(ref bobHits); return Task.CompletedTask; });

        signal.Publish([1]);
        await WaitUntilAsync(() => aliceHits == 1);
        await Task.Delay(50);
        Assert.Equal(0, bobHits);

        alice.Dispose();
        bob.Dispose();
        alice.Dispose();
        Assert.Equal(0, signal.KeyCount);
        Assert.Equal(0, signal.SubscriberCount);
    }

    [Fact]
    public async Task Signal_AThrowingHandlerShouldNotBlockOthers_AndHandlersShouldNotInheritTheCallersContext()
    {
        var signal = new NotificationSignal(NullLogger<NotificationSignal>.Instance);
        var ambient = new AsyncLocal<string?>();
        string? seen = "not-run";
        using var bad = signal.Subscribe(1, () => throw new InvalidOperationException("broken circuit"));
        using var good = signal.Subscribe(1, () => { seen = ambient.Value; return Task.CompletedTask; });

        ambient.Value = "caller";
        signal.Publish([1]);

        await WaitUntilAsync(() => seen != "not-run");
        Assert.Null(seen);
    }

    [Fact]
    public async Task Signal_ConcurrentSubscribeAndDispose_ShouldNotLeak()
    {
        var signal = new NotificationSignal(NullLogger<NotificationSignal>.Instance);
        await Parallel.ForAsync(0, 2000, (i, _) =>
        {
            var subscription = signal.Subscribe(i % 5, () => Task.CompletedTask);
            signal.Publish([i % 5]);
            subscription.Dispose();
            return ValueTask.CompletedTask;
        });

        Assert.Equal(0, signal.SubscriberCount);
        Assert.Equal(0, signal.KeyCount);
    }

    // ---------- 寄信 ----------

    [Fact]
    public void Mailer_ShouldSkipWhenDisabled_SkipInvalidAddresses_AndStopWhenTheQueueIsFull()
    {
        var identity = new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(new SystemSettings { SystemInformation = { SystemName = "測試" } }));
        var disabledQueue = new LimitedQueue(10);
        var disabled = new NotificationMailer(new StaticOptionsMonitor<EmailSettings>(new EmailSettings { Provider = "None" }), disabledQueue, identity, NullLogger<NotificationMailer>.Instance);
        Assert.Equal(0, disabled.Send(["a@example.com"], "t", null, "/x"));
        Assert.Empty(disabledQueue.Messages);

        var queue = new LimitedQueue(2);
        var mailer = new NotificationMailer(
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings { Provider = "Pickup", PublicBaseUrl = "https://erp.example.com/" }), queue, identity, NullLogger<NotificationMailer>.Instance);

        var queued = mailer.Send(["support", "a@example.com", "b@example.com", "c@example.com", "d@example.com"], "標題", "內容", "/scheduled-jobs");

        Assert.Equal(2, queued);
        Assert.Equal(3, queue.Attempts); // c 被拒之後就停止，不再嘗試 d
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, queue.Messages.Select(x => x.To));
        Assert.Contains("https://erp.example.com/scheduled-jobs", queue.Messages[0].TextBody, StringComparison.Ordinal);
        Assert.Equal(EmailKinds.Notification, queue.Messages[0].Kind);
    }

    // ---------- 公告 ----------

    [Fact]
    public void Announcement_ShouldShowFromStartUntilJustBeforeEnd()
    {
        var start = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var item = new ActiveAnnouncement(1, "t", "c", start, start.AddHours(1), AnnouncementTargetKinds.All, null, null);

        Assert.False(item.IsShowingAt(start.AddTicks(-1)));
        Assert.True(item.IsShowingAt(start));
        Assert.True(item.IsShowingAt(start.AddHours(1).AddTicks(-1)));
        Assert.False(item.IsShowingAt(start.AddHours(1)));
        Assert.True(item with { EndAtUtc = null } is var open && open.IsShowingAt(start.AddYears(10)));
    }

    [Fact]
    public void Banner_ShouldPickByTargetAndHideDismissed()
    {
        var now = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var all = new[]
        {
            new ActiveAnnouncement(1, "全體", "c", now.AddHours(-1), null, AnnouncementTargetKinds.All, null, null),
            new ActiveAnnouncement(2, "業務角色", "c", now.AddHours(-1), null, AnnouncementTargetKinds.Role, 7, null),
            new ActiveAnnouncement(3, "別的角色", "c", now.AddHours(-1), null, AnnouncementTargetKinds.Role, 8, null),
            new ActiveAnnouncement(4, "北區", "c", now.AddHours(-1), null, AnnouncementTargetKinds.Team, 1, "北區"),
            new ActiveAnnouncement(5, "還沒開始", "c", now.AddHours(1), null, AnnouncementTargetKinds.All, null, null),
            new ActiveAnnouncement(6, "已關閉", "c", now.AddHours(-1), null, AnnouncementTargetKinds.All, null, null),
        };

        var shown = AnnouncementBanner.Select(all, now, [7], ["北區"], new HashSet<int> { 6 });

        Assert.Equal(new[] { 1, 2, 4 }, shown.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task AnnouncementService_ShouldValidate_DetectConflicts_AndRecordDismissalsPerUser()
    {
        var role = AddRole("業務");
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var service = new AnnouncementService(factory, TimeProvider.System, NullLogger<AnnouncementService>.Instance);
        var start = DateTime.Now.AddMinutes(-5);

        Assert.False((await service.AddAsync(new AnnouncementAdapterModel { Title = "", Content = "c", StartAt = start }, "admin")).Success);
        Assert.False((await service.AddAsync(new AnnouncementAdapterModel { Title = "t", Content = "c", StartAt = start, EndAt = start }, "admin")).Success);
        Assert.False((await service.AddAsync(new AnnouncementAdapterModel { Title = "t", Content = "c", StartAt = start, TargetKind = AnnouncementTargetKinds.Role, TargetId = 999 }, "admin")).Success);

        var model = new AnnouncementAdapterModel { Title = "維護", Content = "今晚維護", StartAt = start, TargetKind = AnnouncementTargetKinds.Role, TargetId = role };
        Assert.True((await service.AddAsync(model, "admin")).Success);
        var stamp = (await service.GetAllAsync()).Single().ConcurrencyStamp;

        model.ConcurrencyStamp = stamp;
        model.Title = "維護（更新）";
        Assert.True((await service.UpdateAsync(model)).Success);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await service.UpdateAsync(new AnnouncementAdapterModel
        {
            Id = model.Id,
            Title = "x",
            Content = "c",
            StartAt = start,
            ConcurrencyStamp = stamp,
        })).Message);

        await service.DismissAsync(alice, model.Id);
        await service.DismissAsync(alice, model.Id);
        Assert.Equal([model.Id], await service.GetDismissedIdsAsync(alice));
        Assert.Empty(await service.GetDismissedIdsAsync(bob));

        var current = Assert.Single(await service.GetCurrentAndUpcomingAsync());
        Assert.Equal("維護（更新）", current.Title);
    }

    [Fact]
    public async Task EndedAnnouncements_ShouldNotBeCached_AndDeletedRolesShouldNotCount()
    {
        var role = AddRole("業務");
        var deletedRole = AddRole("舊", isDeleted: true);
        var user = AddUser("u", roleId: role);
        AddUserRole(user, deletedRole);
        await using (var context = factory.CreateDbContext())
        {
            context.Announcement.Add(new Announcement { Title = "ended", Content = "c", StartAtUtc = DateTime.UtcNow.AddDays(-2), EndAtUtc = DateTime.UtcNow.AddDays(-1), ConcurrencyStamp = "s" });
            await context.SaveChangesAsync();
        }

        var service = new AnnouncementService(factory, TimeProvider.System, NullLogger<AnnouncementService>.Instance);

        Assert.Empty(await service.GetCurrentAndUpcomingAsync());
        Assert.Equal([role], await service.GetUserRoleIdsAsync(user));
    }

    // ---------- 內建事件與資料完整性 ----------

    [Fact]
    public async Task GooglePendingAccount_ShouldNotifyAdminsOnce()
    {
        var notifications = new RecordingNotificationSender();
        await using var context = factory.CreateDbContext();
        var service = new ExternalLoginService(context, NullLogger<ExternalLoginService>.Instance, new RecordingAuditLogService(), notifications);

        await service.FindOrCreateAsync("Google", "sub-1", "new.person@example.com", "新同事", MagicObjectHelper.預設角色);

        var request = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationCategories.AccountPending, request.Category);
        Assert.True(request.Target.Admins);
        Assert.StartsWith("AccountPending:", request.SourceKey, StringComparison.Ordinal);
        Assert.Equal("/myusers", request.Link);
    }

    [Fact]
    public async Task PurgingAUser_ShouldDeleteTheirNotificationsAndDismissals()
    {
        var user = AddUser("leaving");
        await using (var context = factory.CreateDbContext())
        {
            var announcement = new Announcement { Title = "t", Content = "c", StartAtUtc = DateTime.UtcNow, ConcurrencyStamp = "s" };
            context.Announcement.Add(announcement);
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "t", CreatedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
            context.AnnouncementDismissal.Add(new AnnouncementDismissal { AnnouncementId = announcement.Id, MyUserId = user, DismissedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        await using (var context = factory.CreateDbContext())
        {
            context.MyUser.Remove(await context.MyUser.IgnoreQueryFilters().SingleAsync(x => x.Id == user));
            await context.SaveChangesAsync();
        }

        await using var check = factory.CreateDbContext();
        Assert.Equal(0, await check.Notification.CountAsync());
        Assert.Equal(0, await check.AnnouncementDismissal.CountAsync());
    }

    [Fact]
    public async Task RetentionJob_ShouldDoNothingAtZero_AndPurgeAndAuditOtherwise()
    {
        var user = AddUser("u");
        await using (var context = factory.CreateDbContext())
        {
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "old", CreatedAtUtc = DateTime.UtcNow.AddDays(-100) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "new", CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await context.SaveChangesAsync();
        }

        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var audit = new RecordingAuditLogService();
        var context0 = new ScheduledJobContext(1, JobRunTriggers.Schedule, DateTime.UtcNow, null);

        await new NotificationRetentionJob(query, audit, new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 0, ReadRetentionDays = 0 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(context0, CancellationToken.None);
        Assert.Equal(2, CountNotifications());

        await new NotificationRetentionJob(query, audit, new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 90 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(context0, CancellationToken.None);
        Assert.Equal(1, CountNotifications());
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Notification.AutoPurge);
    }

    [Fact]
    public async Task RetentionJob_ShouldPurgeReadOnesByReadTime_AndKeepUnreadOnes()
    {
        // 0.9.125 起：已讀的從標為已讀的時間起算 ReadRetentionDays；未讀的不受影響；0＝不提早刪除。
        var user = AddUser("u");
        await using (var context = factory.CreateDbContext())
        {
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "read-old", CreatedAtUtc = DateTime.UtcNow.AddDays(-9), ReadAtUtc = DateTime.UtcNow.AddDays(-8) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "read-new", CreatedAtUtc = DateTime.UtcNow.AddDays(-30), ReadAtUtc = DateTime.UtcNow.AddDays(-1) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "unread", CreatedAtUtc = DateTime.UtcNow.AddDays(-30) });
            await context.SaveChangesAsync();
        }

        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var audit = new RecordingAuditLogService();
        var context0 = new ScheduledJobContext(1, JobRunTriggers.Schedule, DateTime.UtcNow, null);

        await new NotificationRetentionJob(query, audit, new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 90, ReadRetentionDays = 0 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(context0, CancellationToken.None);
        Assert.Equal(3, CountNotifications());

        await new NotificationRetentionJob(query, audit, new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 90, ReadRetentionDays = 7 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(context0, CancellationToken.None);
        await using var check = factory.CreateDbContext();
        Assert.Equal(["read-new", "unread"], await check.Notification.OrderBy(x => x.Title).Select(x => x.Title).ToListAsync());
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Notification.AutoPurge);
    }

    [Fact]
    public async Task RetentionStats_ShouldCountBothRulesOnce_AndMatchWhatTheJobRemoves()
    {
        // 資料保留與清理頁（0.9.133 起）：「建立超過」與「已讀超過」可能命中同一則，只能算一次。
        var user = AddUser("u");
        await using (var context = factory.CreateDbContext())
        {
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "both", CreatedAtUtc = DateTime.UtcNow.AddDays(-100), ReadAtUtc = DateTime.UtcNow.AddDays(-50) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "created-only", CreatedAtUtc = DateTime.UtcNow.AddDays(-100) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "read-only", CreatedAtUtc = DateTime.UtcNow.AddDays(-30), ReadAtUtc = DateTime.UtcNow.AddDays(-8) });
            context.Notification.Add(new Notification { RecipientUserId = user, Category = "T", Title = "keep", CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await context.SaveChangesAsync();
        }

        var query = new NotificationQueryService(factory, new NotificationSignal(NullLogger<NotificationSignal>.Instance), TimeProvider.System, NullLogger<NotificationQueryService>.Instance);
        var stats = await query.GetRetentionStatsAsync(
            RetentionCutoffs.Notification(90, TimeProvider.System), RetentionCutoffs.Notification(7, TimeProvider.System));
        Assert.Equal(4, stats.Total);
        Assert.Equal(3, stats.Expired);
        Assert.Null((await query.GetRetentionStatsAsync(null, null)).Expired);

        await new NotificationRetentionJob(query, new RecordingAuditLogService(), new StaticOptionsMonitor<NotificationSettings>(new NotificationSettings { RetentionDays = 90, ReadRetentionDays = 7 }), TimeProvider.System, NullLogger<NotificationRetentionJob>.Instance)
            .ExecuteAsync(new ScheduledJobContext(1, JobRunTriggers.Schedule, DateTime.UtcNow, null), CancellationToken.None);
        Assert.Equal(stats.Total - stats.Expired, CountNotifications());
    }

    // ---------- 測試工具 ----------

    private static NotificationRequest Request(NotificationTarget target, string? sourceKey = null)
        => new("Test", "標題", "內容", "/x", target, SourceKey: sourceKey);

    private (NotificationSender Sender, RecordingSignal Signal, RecordingMailer Mailer) NewSender()
    {
        var signal = new RecordingSignal();
        var mailer = new RecordingMailer();
        var context = factory.CreateDbContext();
        disposables.Add(context);
        var sender = new NotificationSender(factory, new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance), signal, mailer, TimeProvider.System, NullLogger<NotificationSender>.Instance);
        return (sender, signal, mailer);
    }

    private readonly List<IDisposable> disposables = [];

    private int[] Recipients()
    {
        using var context = factory.CreateDbContext();
        return context.Notification.Select(x => x.RecipientUserId).ToArray();
    }

    private int CountNotifications()
    {
        using var context = factory.CreateDbContext();
        return context.Notification.Count();
    }

    private int AddUser(string account, bool status = true, bool isDeleted = false, bool isAdmin = false, int? roleId = null, string? email = null)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = account, Name = account, Password = "x", Status = status, IsDeleted = isDeleted, IsAdmin = isAdmin, RoleViewId = roleId, Email = email };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private int AddRole(string name, string[]? defaultTeams = null, bool isDeleted = false)
    {
        using var context = factory.CreateDbContext();
        var role = new RoleView { Name = name, TabViewJson = "[]", DefaultTeamsJson = JsonSerializer.Serialize(defaultTeams ?? []), IsDeleted = isDeleted };
        context.RoleView.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    private int AddTeam(string name, bool isDeleted = false)
    {
        using var context = factory.CreateDbContext();
        var team = new Team { Name = name, IsDeleted = isDeleted };
        context.Team.Add(team);
        context.SaveChanges();
        return team.Id;
    }

    private void AddUserRole(int userId, int roleId)
    {
        using var context = factory.CreateDbContext();
        context.UserRole.Add(new UserRole { MyUserId = userId, RoleViewId = roleId });
        context.SaveChanges();
    }

    private void AddUserTeam(int userId, int teamId)
    {
        using var context = factory.CreateDbContext();
        context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = teamId });
        context.SaveChanges();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "等待逾時。");
    }

    public void Dispose()
    {
        foreach (var item in disposables)
        {
            item.Dispose();
        }

        connection.Dispose();
    }

    private sealed class RecordingSignal : INotificationSignal
    {
        public List<int> Published { get; } = [];

        public IDisposable Subscribe(int userId, Func<Task> handler) => throw new NotSupportedException();

        public IDisposable SubscribeAnnouncements(Func<Task> handler) => throw new NotSupportedException();

        public void Publish(IEnumerable<int> userIds) => Published.AddRange(userIds);

        public void PublishAnnouncementsChanged()
        {
        }
    }

    private sealed class RecordingMailer : INotificationMailer
    {
        public List<IReadOnlyList<string>> Sent { get; } = [];

        public int Send(IReadOnlyList<string> emails, string title, string? body, string? link)
        {
            Sent.Add(emails);
            return emails.Count;
        }
    }

    private sealed class LimitedQueue(int capacity) : IEmailQueue
    {
        public List<EmailMessage> Messages { get; } = [];

        public int Attempts { get; private set; }

        public bool TryEnqueue(EmailMessage message)
        {
            Attempts++;
            if (Messages.Count >= capacity)
            {
                return false;
            }

            Messages.Add(message);
            return true;
        }
    }
}
