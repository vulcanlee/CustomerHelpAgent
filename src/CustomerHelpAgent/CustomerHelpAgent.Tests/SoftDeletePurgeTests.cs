using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 已刪除資料清理（0.9.97 起）：<see cref="SoftDeletePurgeService"/> 與排程作業 <see cref="SoftDeletePurgeJob"/>。
///
/// 中途被還原、刪除當下角色被指派、存檔失敗、取消等競態用 <see cref="SaveChangesInterceptor"/> 在存檔前後注入，
/// 才測得到「每筆新的 context」「追蹤中 Remove 帶版本號」「提交後才刪檔」這些設計。
/// </summary>
public sealed class SoftDeletePurgeTests : IAsyncDisposable
{
    // 門檻固定為本地 2026-07-03 09:00（「現在」= 本地 2026-10-01 09:00，保留 90 天）。
    private static readonly DateTime Cutoff = new(2026, 7, 3, 9, 0, 0);
    private static readonly DateTime Expired = Cutoff.AddSeconds(-1);
    private static readonly DateTime NotYet = Cutoff.AddSeconds(1);

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentPurge", Guid.NewGuid().ToString("N"));
    private readonly List<IInterceptor> interceptors = [];

    // ================================================================== 清除服務

    [Fact]
    public async Task OnlyRowsDeletedBeforeTheCutoff_ShouldBePurged_ForAllFiveTypes()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Project.AddRange(NewProject("過期專案", Expired), NewProject("未到期專案", NotYet), NewProject("有效專案", null), NewProject("沒有刪除時間", null, isDeleted: true));
            db.Category.AddRange(new Category { Name = "過期分類", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "未到期分類", IsDeleted = true, DeletedAt = NotYet });
            db.Team.AddRange(new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired }, new Team { Name = "未到期團隊", IsDeleted = true, DeletedAt = NotYet });
            db.MyUser.AddRange(NewUser("expired", Expired), NewUser("notyet", NotYet));
            db.RoleView.AddRange(NewRole("過期角色", Expired), NewRole("未到期角色", NotYet));
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.False(result.AnyFailed);
        await using var verify = NewContext();
        Assert.Equal(["未到期專案", "有效專案", "沒有刪除時間"], (await All<Project>(verify)).Select(x => x.Title).Order().ToArray());
        Assert.Equal(["未到期分類"], (await All<Category>(verify)).Select(x => x.Name).ToArray());
        Assert.Equal(["未到期團隊"], (await All<Team>(verify)).Select(x => x.Name).ToArray());
        Assert.Equal(["notyet"], (await All<MyUser>(verify)).Select(x => x.Account).ToArray());
        Assert.Equal(["未到期角色"], (await All<RoleView>(verify)).Select(x => x.Name).ToArray());
        Assert.All(result.Types, t => Assert.Single(t.Removed));
    }

    // ================================================================== 資料保留與清理頁的統計（0.9.133 起）

    [Fact]
    public async Task RetentionStats_ExpiredCount_ShouldEqualWhatThePurgeRemoves()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Project.AddRange(NewProject("過期專案", Expired), NewProject("未到期專案", NotYet), NewProject("有效專案", null), NewProject("沒有刪除時間", null, isDeleted: true));
            db.Category.AddRange(new Category { Name = "過期分類", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "未到期分類", IsDeleted = true, DeletedAt = NotYet });
            db.Team.AddRange(new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired }, new Team { Name = "未到期團隊", IsDeleted = true, DeletedAt = NotYet });
            db.MyUser.AddRange(NewUser("expired", Expired), NewUser("notyet", NotYet));
            db.RoleView.AddRange(NewRole("過期角色", Expired), NewRole("未到期角色", NotYet));
            await db.SaveChangesAsync();
        }

        var stats = await Service().GetRetentionStatsAsync(Cutoff);
        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.All(result.Types, t => Assert.Equal(t.Removed.Count, stats.Types[t.EntityType].Expired));
        Assert.Equal(3, stats.Types["Project"].Total);  // 只算已刪除的，「有效專案」不算
        Assert.Equal(Expired, stats.Types["Category"].Oldest);
    }

    [Fact]
    public async Task PurgeExpiredType_ShouldOnlyTouchThatType_AndKeepTheProtectionRules()
    {
        // 「自訂清除」（0.9.134 起）只清按下的那一種；預設角色照樣不刪。
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.Add(new Category { Name = "過期分類", IsDeleted = true, DeletedAt = Expired });
            db.Team.Add(new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired });
            db.RoleView.AddRange(NewRole("過期角色", Expired), NewRole(MagicObjectHelper.預設角色, Expired));
            await db.SaveChangesAsync();
        }

        var categories = await Service().PurgeExpiredTypeAsync("Category", Cutoff, default);
        var roles = await Service().PurgeExpiredTypeAsync("RoleView", Cutoff, default);

        Assert.Equal("過期分類", Assert.Single(Assert.Single(categories.Types).Removed).Name);
        var roleType = Assert.Single(roles.Types);
        Assert.Equal("過期角色", Assert.Single(roleType.Removed).Name);
        Assert.Equal(1, roleType.Protected);
        await using var verify = NewContext();
        Assert.Equal(["過期團隊"], (await All<Team>(verify)).Select(x => x.Name).ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PurgeExpiredTypeAsync("Nope", Cutoff, default));
    }

    [Fact]
    public async Task RetentionStats_WithoutCutoff_ShouldNotCountExpired_AndSumDeletedProjectFiles()
    {
        await InitializeAsync();
        await AddProjectWithFileAsync("已刪除專案", Expired, teams: null);
        await using (var db = NewContext())
        {
            var alive = NewProject("有效專案", null);
            alive.Files.Add(NewFile(Path.Combine("2026", "07", "alive.txt")));
            db.Project.Add(alive);
            await db.SaveChangesAsync();
        }

        var stats = await Service().GetRetentionStatsAsync(null);

        Assert.Null(stats.Types["Project"].Expired);
        Assert.Equal(1, stats.Types["Project"].Total);
        Assert.Equal(1, stats.DeletedProjectFileBytes);  // NewFile 的 FileSize 是 1；有效專案的附件不算
    }

    [Fact]
    public async Task ExpiredProject_ShouldTakeItsAttachmentFiles_AndATeamScopedProjectIsNotExempt()
    {
        // 有團隊的專案也要刪：系統清除不套團隊範圍（重用 ProjectService.PurgeAsync 的話會被擋下）。
        await InitializeAsync();
        var (_, expiredFile) = await AddProjectWithFileAsync("過期專案", Expired, teams: "研發部");
        var (_, freshFile) = await AddProjectWithFileAsync("未到期專案", NotYet, teams: null);

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.Single(Type(result, "Project").Removed);
        Assert.False(File.Exists(expiredFile));
        Assert.True(File.Exists(freshFile));
        await using var verify = NewContext();
        Assert.Equal(1, await verify.ProjectFile.CountAsync());
    }

    [Fact]
    public async Task AttachmentPathEscapingTheRoot_ShouldNotDeleteTheOutsideFile()
    {
        await InitializeAsync();
        var outside = Path.Combine(Path.GetDirectoryName(fileRoot)!, $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "keep me");
        try
        {
            await using (var db = NewContext())
            {
                var project = NewProject("過期專案", Expired);
                project.Files.Add(NewFile($"..{Path.DirectorySeparatorChar}{Path.GetFileName(outside)}"));
                db.Project.Add(project);
                await db.SaveChangesAsync();
            }

            var result = await Service().PurgeExpiredAsync(Cutoff, default);

            Assert.True(File.Exists(outside), "附件路徑跑出根目錄時，外面的檔案被刪掉了。");
            Assert.Single(Type(result, "Project").Removed);
            Assert.Equal(1, Type(result, "Project").FileDeleteFailures);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task WhenTheProjectDeleteFails_TheAttachmentFileShouldStillExist()
    {
        await InitializeAsync();
        var (projectId, file) = await AddProjectWithFileAsync("過期專案", Expired, teams: null);
        interceptors.Add(new SaveHook(before: ctx =>
        {
            if (ctx.ChangeTracker.Entries<Project>().Any(e => e.State == EntityState.Deleted))
            {
                throw new InvalidOperationException("simulated database failure");
            }

            return Task.CompletedTask;
        }));

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.True(Type(result, "Project").Failed);
        Assert.True(File.Exists(file), "資料庫刪除失敗，附件檔卻已經被刪了（應該提交成功後才刪）。");
        await using var verify = NewContext();
        Assert.True(await verify.Project.IgnoreQueryFilters([ISoftDeletable.FilterName]).AnyAsync(x => x.Id == projectId));
    }

    [Fact]
    public async Task RowRestoredJustBeforeTheDelete_ShouldSurvive_AndTheNextRowIsStillPurged()
    {
        await InitializeAsync();
        int first;
        await using (var db = NewContext())
        {
            db.Category.AddRange(new Category { Name = "甲", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "乙", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
            first = await db.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).MinAsync(x => x.Id);
        }

        var fired = false;
        interceptors.Add(new SaveHook(before: async ctx =>
        {
            if (!fired && ctx.ChangeTracker.Entries<Category>().Any(e => e.State == EntityState.Deleted && e.Entity.Id == first))
            {
                fired = true;
                await ctx.Database.ExecuteSqlRawAsync("UPDATE Category SET IsDeleted = 0, DeletedAt = NULL, ConcurrencyStamp = 'restored' WHERE Id = {0}", first);
            }
        }));

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        var category = Type(result, "Category");
        Assert.False(category.Failed);
        Assert.Equal(1, category.Skipped);
        Assert.Single(category.Removed);
        await using var verify = NewContext();
        Assert.Equal("甲", (await verify.Category.SingleAsync()).Name);
    }

    [Fact]
    public async Task RoleStillHeldByANotYetExpiredDeletedUser_ShouldBeKeptAsInUse()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            var role = NewRole("臨時", Expired);
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            var user = NewUser("amy", NotYet);
            user.RoleViewId = role.Id;
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        var roles = Type(result, "RoleView");
        Assert.False(roles.Failed);
        Assert.Equal(1, roles.InUse);
        Assert.Empty(roles.Removed);
    }

    [Fact]
    public async Task ExpiredUserAndTheRoleOnlyTheyHeld_ShouldBothBePurgedInOneRun()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            var role = NewRole("臨時", Expired);
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            var user = NewUser("amy", Expired);
            user.RoleViewId = role.Id;
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.Single(Type(result, "MyUser").Removed);
        Assert.Single(Type(result, "RoleView").Removed);
    }

    [Fact]
    public async Task ExpiredCustomerWithOrdersOrAfterSalesRequests_ShouldBeInUse_AndOtherExpiredUsersAreStillPurged()
    {
        // 訂單與售後申請是業務紀錄（外鍵 Restrict，不連帶刪除）：顧客留到下次，不可讓整批使用者清除中止。
        await InitializeAsync();
        await using (var db = NewContext())
        {
            var buyer = NewUser("buyer", Expired);
            var requester = NewUser("requester", Expired);
            db.MyUser.AddRange(buyer, requester);
            var variant = new ProductVariant { Product = new Product { Code = "P1", Name = "跑鞋", Category = "Shoes", Price = 1m }, Color = "黑", Size = "26" };
            db.ProductVariant.Add(variant);
            await db.SaveChangesAsync();
            var order = new SalesOrder
            {
                OrderNo = "A1",
                CustomerUserId = buyer.Id,
                RecipientName = "王小明",
                ShippingAddress = "台北市",
                RecipientPhone = "0912345678",
                Items = [new SalesOrderItem { ProductVariantId = variant.Id, Quantity = 1, UnitPrice = 1m }],
            };
            db.SalesOrder.Add(order);
            await db.SaveChangesAsync();
            db.AfterSalesRequest.Add(new AfterSalesRequest { RequestNo = "AS1", CustomerUserId = requester.Id, SalesOrderItemId = order.Items[0].Id, Description = "尺寸偏小" });
            db.MyUser.Add(NewUser("plain", Expired));
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        var users = Type(result, "MyUser");
        Assert.False(users.Failed);
        Assert.Equal(2, users.InUse);
        Assert.Equal("plain", Assert.Single(users.Removed).Name);
        await using var verify = NewContext();
        Assert.Equal(["buyer", "requester"], (await All<MyUser>(verify)).Select(x => x.Account).Order().ToArray());
    }

    [Fact]
    public async Task RoleAssignedToSomeoneJustBeforeItsDelete_ShouldBeInUse_AndTheNextRoleIsStillPurged()
    {
        await InitializeAsync();
        int raced, activeUser;
        await using (var db = NewContext())
        {
            var first = NewRole("先", Expired);
            var second = NewRole("後", Expired);
            var other = new RoleView { Name = "其他", TabViewJson = "[]" };
            db.RoleView.AddRange(first, second, other);
            await db.SaveChangesAsync();
            var user = new MyUser { Account = "bob", Name = "bob", Password = "x", RoleViewId = other.Id };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            (raced, activeUser) = (first.Id, user.Id);
        }

        interceptors.Add(new SaveHook(before: async ctx =>
        {
            if (ctx.ChangeTracker.Entries<RoleView>().Any(e => e.State == EntityState.Deleted && e.Entity.Id == raced))
            {
                await ctx.Database.ExecuteSqlRawAsync("UPDATE MyUser SET RoleViewId = {0} WHERE Id = {1}", raced, activeUser);
            }
        }));

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        var roles = Type(result, "RoleView");
        Assert.False(roles.Failed);
        Assert.Equal(1, roles.InUse);
        Assert.Equal("後", Assert.Single(roles.Removed).Name);
    }

    [Fact]
    public async Task Cascades_ShouldOnlyRemoveThePurgedRowsOwnLinks()
    {
        await InitializeAsync();
        int goneUser, keptUser;
        await using (var db = NewContext())
        {
            var team = new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired };
            var keptTeam = new Team { Name = "留下的團隊" };
            db.Team.AddRange(team, keptTeam);
            var gone = NewUser("gone", Expired);
            var kept = new MyUser { Account = "kept", Name = "kept", Password = "x" };
            db.MyUser.AddRange(gone, kept);
            await db.SaveChangesAsync();
            db.UserTeam.AddRange(
                new UserTeam { MyUserId = gone.Id, TeamId = keptTeam.Id },
                new UserTeam { MyUserId = kept.Id, TeamId = team.Id },
                new UserTeam { MyUserId = kept.Id, TeamId = keptTeam.Id });
            db.PasswordResetToken.Add(new PasswordResetToken { MyUserId = gone.Id, TokenHash = "h", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            (goneUser, keptUser) = (gone.Id, kept.Id);
        }

        await Service().PurgeExpiredAsync(Cutoff, default);

        await using var verify = NewContext();
        var links = await verify.UserTeam.Select(x => new { x.MyUserId }).ToListAsync();
        Assert.Single(links);
        Assert.Equal(keptUser, links[0].MyUserId);
        Assert.Equal(0, await verify.PasswordResetToken.CountAsync(x => x.MyUserId == goneUser));
    }

    [Fact]
    public async Task SupportAccountAndTheDefaultRole_ShouldNeverBePurged()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.MyUser.Add(NewUser("SUPPORT", Expired));
            db.RoleView.Add(NewRole(MagicObjectHelper.預設角色, Expired));
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.Equal(1, Type(result, "MyUser").Protected);
        Assert.Equal(1, Type(result, "RoleView").Protected);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
        Assert.Equal(1, await verify.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
    }

    [Fact]
    public async Task AFailureInOneType_ShouldKeepItsPartialResult_AndNotStopTheOthers()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.AddRange(new Category { Name = "甲", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "乙", IsDeleted = true, DeletedAt = Expired });
            db.Team.Add(new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
        }

        var categoryDeletes = 0;
        interceptors.Add(new SaveHook(before: ctx =>
        {
            if (ctx.ChangeTracker.Entries<Category>().Any(e => e.State == EntityState.Deleted) && ++categoryDeletes == 2)
            {
                throw new InvalidOperationException("simulated failure on the second category");
            }

            return Task.CompletedTask;
        }));

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        var category = Type(result, "Category");
        Assert.True(category.Failed);
        Assert.Single(category.Removed);
        Assert.Single(Type(result, "Team").Removed);
    }

    [Fact]
    public async Task CancelledAfterTheFirstRow_ShouldReturnAPartialResult_WithoutThrowing()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.AddRange(new Category { Name = "甲", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "乙", IsDeleted = true, DeletedAt = Expired });
            db.Team.Add(new Team { Name = "過期團隊", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
        }

        using var shutdown = new CancellationTokenSource();
        interceptors.Add(new SaveHook(after: _ =>
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        }));

        var result = await Service().PurgeExpiredAsync(Cutoff, shutdown.Token);

        Assert.True(result.Cancelled);
        Assert.Single(Type(result, "Category").Removed);
        Assert.Empty(Type(result, "Team").Removed);
    }

    // ================================================================== 排程作業

    [Fact]
    public async Task Job_WithZeroDays_ShouldNotPurgeOrAudit()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.Add(new Category { Name = "很久以前", IsDeleted = true, DeletedAt = new DateTime(2000, 1, 1) });
            await db.SaveChangesAsync();
        }

        var (job, audit) = Job(days: 0);
        var outcome = await job.ExecuteAsync(Context(), default);

        Assert.True(outcome.Succeeded);
        Assert.Empty(audit.Entries);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
    }

    [Fact]
    public async Task Job_ShouldUseTheLocalClock()
    {
        // 「現在」UTC 2026-10-01 01:00 = 本地 09:00；90 天前的本地 05:00 刪、10:00 留。誤用 UTC（門檻 01:00）的話 05:00 那筆會留下。
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.AddRange(
                new Category { Name = "本地五點", IsDeleted = true, DeletedAt = new DateTime(2026, 7, 3, 5, 0, 0) },
                new Category { Name = "本地十點", IsDeleted = true, DeletedAt = new DateTime(2026, 7, 3, 10, 0, 0) });
            await db.SaveChangesAsync();
        }

        var (job, _) = Job(days: 90);
        await job.ExecuteAsync(Context(), default);

        await using var verify = NewContext();
        Assert.Equal("本地十點", (await verify.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).SingleAsync()).Name);
    }

    [Fact]
    public async Task Job_ShouldWriteOneSummaryAuditPerTypeThatRemovedRows()
    {
        await InitializeAsync();
        int categoryId;
        await using (var db = NewContext())
        {
            var category = new Category { Name = "過期分類", IsDeleted = true, DeletedAt = Expired };
            db.Category.Add(category);
            db.Team.AddRange(new Team { Name = "甲隊", IsDeleted = true, DeletedAt = Expired }, new Team { Name = "乙隊", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }

        var (job, audit) = Job(days: 90);
        var outcome = await job.ExecuteAsync(Context(), default);

        Assert.True(outcome.Succeeded);
        Assert.Equal([AuditActions.Category.AutoPurge, AuditActions.Team.AutoPurge], audit.Entries.Select(x => x.Action).ToArray());
        Assert.All(audit.Entries, x => Assert.Null(x.ActorUserId));
        var categoryAudit = audit.Entries[0];
        Assert.Equal($"rows=1; days=90; trigger=Schedule; items=#{categoryId} 過期分類", categoryAudit.Detail);
        Assert.StartsWith("rows=2; days=90", audit.Entries[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditDetail_ShouldBeTruncated()
    {
        var type = new SoftDeletePurgeTypeResult("Category");
        for (var index = 1; index <= 200; index++)
        {
            type.Removed.Add(new PurgedRecord(index, new string('名', 30)));
        }

        var detail = SoftDeletePurgeJob.BuildDetail(type, 90, JobRunTriggers.Schedule);

        Assert.True(detail.Length <= SoftDeletePurgeJob.MaxDetailLength, $"Detail 長度 {detail.Length}");
        Assert.StartsWith("rows=200;", detail, StringComparison.Ordinal);
        Assert.Matches(@"…\(\+\d+\)$", detail);
    }

    [Fact]
    public async Task Job_WhenAFailureHappensMidway_ShouldStillAuditWhatWasRemoved_AndReportFailure()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.AddRange(new Category { Name = "甲", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "乙", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
        }

        var categoryDeletes = 0;
        interceptors.Add(new SaveHook(before: ctx =>
        {
            if (ctx.ChangeTracker.Entries<Category>().Any(e => e.State == EntityState.Deleted) && ++categoryDeletes == 2)
            {
                throw new InvalidOperationException("simulated failure");
            }

            return Task.CompletedTask;
        }));

        var (job, audit) = Job(days: 90);
        var outcome = await job.ExecuteAsync(Context(), default);

        Assert.False(outcome.Succeeded);
        Assert.StartsWith("rows=1;", Assert.Single(audit.Entries).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Job_WithOnlySkips_ShouldSucceed_AndMentionThem()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.MyUser.Add(NewUser("support", Expired));
            await db.SaveChangesAsync();
        }

        var (job, audit) = Job(days: 90);
        var outcome = await job.ExecuteAsync(Context(), default);

        Assert.True(outcome.Succeeded);
        Assert.Contains("受保護", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Purge_DeletesMemoryOfPurgedAccount()
    {
        await InitializeAsync();
        int gone, kept;
        await using (var db = NewContext())
        {
            var expired = NewUser("expired", Expired);
            var notYet = NewUser("notyet", NotYet);
            db.MyUser.AddRange(expired, notYet);
            await db.SaveChangesAsync();
            gone = expired.Id;
            kept = notYet.Id;
            db.CustomerMemory.AddRange(
                new CustomerMemory { CustomerUserId = gone, Kind = MemoryKind.ShoeSize, Value = "41", CreatedAt = Cutoff, UpdatedAt = Cutoff },
                new CustomerMemory { CustomerUserId = gone, Kind = MemoryKind.PreferredColor, Value = "黑色", CreatedAt = Cutoff, UpdatedAt = Cutoff },
                new CustomerMemory { CustomerUserId = kept, Kind = MemoryKind.ShoeSize, Value = "38", CreatedAt = Cutoff, UpdatedAt = Cutoff });
            await db.SaveChangesAsync();
        }

        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.False(result.AnyFailed);
        Assert.Single(Type(result, "MyUser").Removed);
        await using var verify = NewContext();
        Assert.Equal(kept, (await verify.CustomerMemory.SingleAsync()).CustomerUserId);
    }

    [Fact]
    public async Task Purge_MemoryAndAccountAreRemovedAtomically_WhenSaveFails()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            var expired = NewUser("expired", Expired);
            db.MyUser.Add(expired);
            await db.SaveChangesAsync();
            db.CustomerMemory.Add(new CustomerMemory { CustomerUserId = expired.Id, Kind = MemoryKind.ShoeSize, Value = "41", CreatedAt = Cutoff, UpdatedAt = Cutoff });
            await db.SaveChangesAsync();
        }

        // 存檔失敗：帳號與記憶必須同進退（兩者在同一次 SaveChanges），不能刪了帳號卻留下記憶。
        interceptors.Add(new SaveHook(before: _ => throw new InvalidOperationException("boom")));
        var result = await Service().PurgeExpiredAsync(Cutoff, default);

        Assert.True(result.AnyFailed);
        await using var verify = NewContext();
        Assert.Single(await All<MyUser>(verify));
        Assert.Single(await verify.CustomerMemory.ToListAsync());
    }

    [Fact]
    public async Task Job_WhenCancelled_ShouldAuditFirst_ThenPropagateTheCancellation()
    {
        await InitializeAsync();
        await using (var db = NewContext())
        {
            db.Category.AddRange(new Category { Name = "甲", IsDeleted = true, DeletedAt = Expired }, new Category { Name = "乙", IsDeleted = true, DeletedAt = Expired });
            await db.SaveChangesAsync();
        }

        using var shutdown = new CancellationTokenSource();
        interceptors.Add(new SaveHook(after: _ =>
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        }));

        var (job, audit) = Job(days: 90);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.ExecuteAsync(Context(), shutdown.Token));

        Assert.StartsWith("rows=1;", Assert.Single(audit.Entries).Detail, StringComparison.Ordinal);
    }

    // ================================================================== 測試基礎

    private static SoftDeletePurgeTypeResult Type(SoftDeletePurgeResult result, string entityType)
        => result.Types.Single(x => x.EntityType == entityType);

    private static ScheduledJobContext Context() => new(1, JobRunTriggers.Schedule, null, null);

    private static Task<List<T>> All<T>(BackendDBContext db)
        where T : class => db.Set<T>().IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().ToListAsync();

    private static Project NewProject(string title, DateTime? deletedAt, bool isDeleted = false, string? teams = null)
        => new() { Title = title, Owner = "owner", Status = "未開始", Priority = "中", Teams = teams, IsDeleted = isDeleted || deletedAt is not null, DeletedAt = deletedAt };

    private static MyUser NewUser(string account, DateTime deletedAt)
        => new() { Account = account, Name = account, Password = "x", IsDeleted = true, DeletedAt = deletedAt };

    private static RoleView NewRole(string name, DateTime deletedAt)
        => new() { Name = name, TabViewJson = "[]", IsDeleted = true, DeletedAt = deletedAt };

    private static ProjectFile NewFile(string relativePath)
        => new() { OriginalFileName = "a.txt", StoredFileName = Path.GetFileName(relativePath), RelativePath = relativePath, ContentType = "text/plain", FileSize = 1 };

    private async Task<(int ProjectId, string FullPath)> AddProjectWithFileAsync(string title, DateTime deletedAt, string? teams)
    {
        var relative = Path.Combine("2026", "07", $"{Guid.NewGuid():N}.txt");
        var fullPath = Path.Combine(fileRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "attachment");

        await using var db = NewContext();
        var project = NewProject(title, deletedAt, teams: teams);
        project.Files.Add(NewFile(relative));
        db.Project.Add(project);
        await db.SaveChangesAsync();
        return (project.Id, fullPath);
    }

    private SoftDeletePurgeService Service()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.ProjectFilePath = fileRoot;
        return new SoftDeletePurgeService(
            new HookedFactory(connection, interceptors),
            new ProjectFileStore(Options.Create(settings), NullLogger<ProjectFileStore>.Instance),
            Options.Create(new BootstrapSettings { SupportAccount = "support" }),
            NullLogger<SoftDeletePurgeService>.Instance);
    }

    private (SoftDeletePurgeJob Job, RecordingAuditLogService Audit) Job(int days)
    {
        var audit = new RecordingAuditLogService();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        var job = new SoftDeletePurgeJob(
            Service(), audit, new StaticOptionsMonitor<SoftDeleteSettings>(new SoftDeleteSettings { PurgeAfterDays = days }), clock, NullLogger<SoftDeletePurgeJob>.Instance);
        return (job, audit);
    }

    private BackendDBContext NewContext() => new TestDbContextFactory(connection).CreateDbContext();

    private async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        Directory.CreateDirectory(fileRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        try
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // 暫存檔清不掉不影響測試結果。
        }
    }

    /// <summary>每次建立的 context 都掛上目前的攔截器（清除服務每筆都用新的 context）。</summary>
    private sealed class HookedFactory(SqliteConnection connection, List<IInterceptor> interceptors) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);
    }

    /// <summary>在存檔前或存檔成功後執行一段動作，用來注入競態與失敗。</summary>
    private sealed class SaveHook(Func<DbContext, Task>? before = null, Func<DbContext, Task>? after = null) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (before is not null)
            {
                await before(eventData.Context!);
            }

            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (after is not null)
            {
                await after(eventData.Context!);
            }

            return result;
        }
    }
}
