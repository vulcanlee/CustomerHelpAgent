using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 軟刪除（0.9.95 起：使用者、角色）。
///
/// 這兩張表牽涉登入與權限，釘住的重點是「已刪除的東西真的失效、又不會因此把別人鎖在門外」：
/// 已刪除的角色不再給權限（關聯刻意保留，所以權限判斷必須經 RoleView 過濾）、仍是主要角色的角色不能刪、
/// support 與自己不能刪、已刪除的使用者無法登入、Google 登入不會替已刪除的人建出重複帳號。
/// </summary>
public sealed class SoftDeleteUserRoleTests
{
    // ------------------------------------------------------------------ 已刪除的角色不再給權限

    [Fact]
    public async Task DeletedAdditionalRole_ShouldStopGrantingPermissions_AndRestoreBringsThemBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var primary = await fixture.AddRoleAsync("一般", "PermA");
        var extra = await fixture.AddRoleAsync("加值", "PermX");
        var userId = await fixture.AddUserAsync("amy", primary, extra);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));

        Assert.True((await fixture.RoleService().DeleteAsync(extra)).Success);
        var afterDelete = await fixture.PermissionKeysAsync(userId);
        Assert.Contains("PermA", afterDelete);
        Assert.DoesNotContain("PermX", afterDelete);

        Assert.True((await fixture.RoleService().RestoreAsync(extra)).Success);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));
    }

    [Fact]
    public async Task DeletedRoleReachedOnlyThroughLegacyRoleViewId_ShouldGrantNothing()
    {
        // 沒有 UserRole、只有 MyUser.RoleViewId（舊資料）也要被過濾；只過濾 UserRole 那條查詢會漏掉這個來源。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("舊角色", "PermLegacy");
        fixture.Context.MyUser.Add(new MyUser { Account = "legacy", Name = "legacy", Password = "x", RoleViewId = role });
        await fixture.Context.SaveChangesAsync();
        var userId = await fixture.Context.MyUser.Where(x => x.Account == "legacy").Select(x => x.Id).SingleAsync();
        Assert.Contains("PermLegacy", await fixture.PermissionKeysAsync(userId));

        await fixture.Context.RoleView.Where(x => x.Id == role).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsDeleted, true));

        Assert.Empty(await fixture.PermissionKeysAsync(userId));
    }

    // ------------------------------------------------------------------ 刪除角色的限制

    [Fact]
    public async Task RoleDelete_WhileActiveOrDisabledUsersUseItAsPrimary_ShouldBeRejectedWithTheirAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("業務", "PermS");
        var amy = await fixture.AddUserAsync("amy", role);
        var ben = await fixture.AddUserAsync("ben", role, status: false);
        var stampBefore = await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.ConcurrencyStamp).SingleAsync();

        var rejected = await fixture.RoleService().DeleteAsync(role);

        Assert.False(rejected.Success);
        Assert.Contains("2 位", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("amy", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("ben", rejected.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Id == role).Select(x => x.IsDeleted).SingleAsync());

        // 事前就要擋下、不可以「先寫入再靠存檔後複查還原」：那樣版本號會被換掉，正在編輯這個角色的人存檔時會被誤判為衝突。
        Assert.Equal(stampBefore, await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.ConcurrencyStamp).SingleAsync());

        // 只剩已刪除的使用者以它為主要角色時就可以刪（他們不會再登入，不會被登出迴圈卡住）。
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.UserService().DeleteAsync(ben)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);
    }

    [Fact]
    public async Task RoleDelete_WhenSomeoneIsAssignedItMeanwhile_ShouldRollBack()
    {
        // 「刪除角色的檢查」與「使用者存檔時驗證角色」各在不同的 DbContext。模擬刪除剛存檔、
        // 另一個人恰好把某位使用者設成這個主要角色：存檔後的複查必須把角色還原並擋下。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("業務", "PermS");
        var other = await fixture.AddRoleAsync("其他", "PermO");
        var amy = await fixture.AddUserAsync("amy", other);
        var interceptor = new AssignPrimaryRoleAfterSaveInterceptor(amy, role);

        var result = await fixture.RoleService(interceptor).DeleteAsync(role);

        Assert.True(interceptor.Fired, "攔截器沒有觸發，這個測試沒有測到競態。");
        Assert.False(result.Success);
        Assert.Contains("amy", result.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Id == role).Select(x => x.IsDeleted).SingleAsync());
    }

    [Fact]
    public async Task DefaultRole_ShouldNotBeDeletable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");

        var result = await fixture.RoleService().DeleteAsync(role);

        Assert.False(result.Success);
        Assert.Contains(MagicObjectHelper.預設角色, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleRestore_WithSameNameActiveRole_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.AddRoleAsync("稽核", "PermA");
        Assert.True((await fixture.RoleService().DeleteAsync(old)).Success);
        await fixture.AddRoleAsync("稽核", "PermB");

        var result = await fixture.RoleService().RestoreAsync(old);

        Assert.False(result.Success);
        Assert.Contains("同名", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolePurge_WhenADeletedUserStillHasItAsPrimary_ShouldBeRejectedWithAClearMessage()
    {
        // 主要角色是 Restrict 外鍵，連已刪除的使用者都算；用有過濾的集合計算會讓資料庫丟出籠統的錯誤。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("臨時", "PermT");
        var amy = await fixture.AddUserAsync("amy", role);
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);

        var rejected = await fixture.RoleService().PurgeAsync(role);

        Assert.False(rejected.Success);
        Assert.Contains("amy（已刪除）", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == role));

        Assert.True((await fixture.UserService().PurgeAsync(amy)).Success);
        Assert.True((await fixture.RoleService().PurgeAsync(role)).Success);
        Assert.Equal(0, await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == role));
    }

    [Fact]
    public async Task RoleEditedThroughClone_ShouldSave()
    {
        // 0.9.93～0.9.94 的缺陷：RoleViewAdapterModel.Clone() 漏了版本號，從畫面修改任何角色都被當成並行衝突。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("編輯測試", "PermE");
        var service = fixture.RoleService();
        var editing = (await service.GetAsync(role)).Clone();
        editing.Name = "編輯測試（改名）";

        var result = await service.UpdateAsync(editing);

        Assert.True(result.Success, result.Message);
        Assert.Equal("編輯測試（改名）", await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.Name).SingleAsync());
    }

    // ------------------------------------------------------------------ 刪除使用者的限制

    [Fact]
    public async Task SupportAccount_ShouldNotBeDeletable_EvenWhenRenamedOrDifferentCase()
    {
        await using var fixture = await Fixture.CreateAsync(supportAccount: "ops");
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var ops = await fixture.AddUserAsync("OPS", role);
        var service = fixture.UserService();

        var result = await service.DeleteAsync(ops);

        Assert.False(result.Success);
        Assert.Contains("OPS", result.Message, StringComparison.Ordinal);
        Assert.False(service.CanDelete(new MyUserAdapterModel { Id = ops, Account = "OPS" }));
    }

    [Fact]
    public async Task CurrentUser_ShouldNotDeleteThemselves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var me = await fixture.AddUserAsync("me", role);
        fixture.SignInAs(me, "me");

        var result = await fixture.UserService().DeleteAsync(me);

        Assert.False(result.Success);
        Assert.Contains("自己", result.Message, StringComparison.Ordinal);

        // 沒有登入者（背景作業，Id = 0）時不套用「不可刪除自己」。
        fixture.SignInAs(0, string.Empty);
        Assert.True((await fixture.UserService().DeleteAsync(me)).Success);
    }

    // ------------------------------------------------------------------ 使用者與已刪除角色

    [Fact]
    public async Task EditingUser_ShouldKeepLinkToDeletedExtraRole_SoRestoringTheRoleRestoresIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var primary = await fixture.AddRoleAsync("一般", "PermA");
        var extra = await fixture.AddRoleAsync("加值", "PermX");
        var userId = await fixture.AddUserAsync("amy", primary, extra);
        var users = fixture.UserService();
        Assert.True((await fixture.RoleService().DeleteAsync(extra)).Success);

        var (additional, teams) = await users.GetUserAssignmentsAsync(userId);
        Assert.DoesNotContain(extra, additional);

        var stamp = await fixture.Context.MyUser.Where(x => x.Id == userId).Select(x => x.ConcurrencyStamp).SingleAsync();
        var save = await users.UpdateAsync(new MyUserAdapterModel
        {
            Id = userId,
            Account = "amy",
            Name = "Amy",
            Status = true,
            RoleViewId = primary,
            AdditionalRoleIds = additional,
            TeamNames = teams,
            ConcurrencyStamp = stamp,
        });
        Assert.True(save.Success, save.Message);
        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId && x.RoleViewId == extra));

        Assert.True((await fixture.RoleService().RestoreAsync(extra)).Success);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));
    }

    [Fact]
    public async Task SavingUser_WithDeletedRole_ShouldBeRejected_ButNoPrimaryRoleIsAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var active = await fixture.AddRoleAsync("一般", "PermA");
        var deleted = await fixture.AddRoleAsync("停用中", "PermD");
        Assert.True((await fixture.RoleService().DeleteAsync(deleted)).Success);
        var users = fixture.UserService();

        var deletedPrimary = await users.AddAsync(new MyUserAdapterModel { Account = "p", Name = "p", Password = "Passw0rd", RoleViewId = deleted });
        var deletedExtra = await users.AddAsync(new MyUserAdapterModel { Account = "e", Name = "e", Password = "Passw0rd", RoleViewId = active, AdditionalRoleIds = [deleted] });
        var noRole = await users.AddAsync(new MyUserAdapterModel { Account = "n", Name = "n", Password = "Passw0rd", RoleViewId = null });

        Assert.False(deletedPrimary.Success);
        Assert.Contains("已被刪除", deletedPrimary.Message, StringComparison.Ordinal);
        Assert.False(deletedExtra.Success);
        Assert.True(noRole.Success, noRole.Message);
    }

    // ------------------------------------------------------------------ 還原與永久刪除使用者

    [Fact]
    public async Task UserRestore_ShouldCheckAccountGoogleIdAndPrimaryRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var users = fixture.UserService();

        // 帳號衝突（完全比對：大小寫不同不算衝突，與新增時的檢查一致）
        var alice = await fixture.AddUserAsync("alice", role);
        Assert.True((await users.DeleteAsync(alice)).Success);
        var newAlice = await fixture.AddUserAsync("alice", role);
        var accountConflict = await users.RestoreAsync(alice);
        Assert.False(accountConflict.Success);
        Assert.Contains("alice", accountConflict.Message, StringComparison.Ordinal);
        await fixture.Context.MyUser.Where(x => x.Id == newAlice).ExecuteUpdateAsync(x => x.SetProperty(u => u.Account, "Alice"));
        Assert.True((await users.RestoreAsync(alice)).Success);

        // GoogleId 衝突
        var g1 = await fixture.AddUserAsync("g1", role, googleId: "sub-1");
        Assert.True((await users.DeleteAsync(g1)).Success);
        await fixture.AddUserAsync("g2", role, googleId: "sub-1");
        var googleConflict = await users.RestoreAsync(g1);
        Assert.False(googleConflict.Success);
        Assert.Contains("g2", googleConflict.Message, StringComparison.Ordinal);

        // 主要角色已刪除 → 擋下；沒有主要角色 → 允許
        var temp = await fixture.AddRoleAsync("臨時", "PermT");
        var tom = await fixture.AddUserAsync("tom", temp);
        Assert.True((await users.DeleteAsync(tom)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(temp)).Success);
        var roleDeleted = await users.RestoreAsync(tom);
        Assert.False(roleDeleted.Success);
        Assert.Contains("臨時", roleDeleted.Message, StringComparison.Ordinal);

        var nobody = await fixture.AddUserAsync("nobody", roleId: null);
        Assert.True((await users.DeleteAsync(nobody)).Success);
        Assert.True((await users.RestoreAsync(nobody)).Success);
    }

    [Fact]
    public async Task UserPurge_ShouldOnlyWorkOnDeletedUsers_AndRemoveTheirLinks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var team = new Team { Name = "研發部" };
        fixture.Context.Team.Add(team);
        await fixture.Context.SaveChangesAsync();
        var userId = await fixture.AddUserAsync("amy", role);
        fixture.Context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = team.Id });
        await fixture.Context.SaveChangesAsync();
        var users = fixture.UserService();

        Assert.False((await users.PurgeAsync(userId)).Success, "未刪除的使用者不可直接永久刪除。");

        Assert.True((await users.DeleteAsync(userId)).Success);
        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId));
        Assert.True((await users.PurgeAsync(userId)).Success);

        Assert.Equal(0, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == userId));
        Assert.Equal(0, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId));
        Assert.Equal(0, await fixture.Context.UserTeam.CountAsync(x => x.MyUserId == userId));
    }

    [Fact]
    public async Task DeletedUsersList_ShouldShowTheirDeletedPrimaryRoleName()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("臨時", "PermT");
        var amy = await fixture.AddUserAsync("amy", role);
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);

        var deleted = (await fixture.UserService().GetDeletedAsync(AllRows())).Result.Single();

        Assert.Equal("amy", deleted.Account);
        Assert.Equal("臨時", deleted.RoleViewName);
        Assert.Equal(string.Empty, deleted.Password);
        Assert.Equal("alice", deleted.DeletedBy);
    }

    // ------------------------------------------------------------------ 已刪除的使用者無法登入

    [Fact]
    public async Task DeletedUser_ShouldNotLogIn_RefreshOrKeepPasswordResetTokens()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var users = fixture.UserService();
        Assert.True((await users.AddAsync(new MyUserAdapterModel { Account = "amy", Name = "amy", Password = "pw-123456", Status = true, RoleViewId = role })).Success);
        var userId = await fixture.Context.MyUser.Where(x => x.Account == "amy").Select(x => x.Id).SingleAsync();
        fixture.Context.PasswordResetToken.Add(new PasswordResetToken { MyUserId = userId, TokenHash = "hash", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await fixture.Context.SaveChangesAsync();
        Assert.NotNull((await fixture.LoginService().LoginAsync("amy", "pw-123456")).User);

        Assert.True((await users.DeleteAsync(userId)).Success);

        var (message, user) = await fixture.LoginService().LoginAsync("amy", "pw-123456");
        Assert.Null(user);
        Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, message);
        Assert.Null(await fixture.LoginService().GetActiveUserAsync(userId));
        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync(x => x.MyUserId == userId));
        Assert.Equal(0, (await users.GetAsync(userId)).Id);
    }

    // ------------------------------------------------------------------ Google 登入

    [Fact]
    public async Task GoogleLogin_WithDeletedUsersGoogleId_ShouldRefuseWithoutCreatingAnAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, googleId: "sub-9", email: "gina@example.com");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);

        var result = await fixture.ExternalLogin("sub-9", "other@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(deleted, result.User.Id);
        Assert.Equal(1, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
    }

    [Fact]
    public async Task GoogleLogin_WithDeletedUsersEmail_ShouldRefuse_IgnoringCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, email: "Gina@Example.com");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);

        var result = await fixture.ExternalLogin("sub-new", "gina@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(1, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
        Assert.Null(await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).Select(x => x.GoogleId).SingleAsync());
    }

    [Fact]
    public async Task GoogleLogin_DeletedGoogleIdShouldWinOverActiveEmail()
    {
        // 使用者決定的順序：GoogleId 優先。否則已刪除者的 Google 帳號會被連結到同 Email 的另一人，之後他就無法還原。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, googleId: "sub-9");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);
        var active = await fixture.AddUserAsync("eve", role, email: "shared@example.com");

        var result = await fixture.ExternalLogin("sub-9", "shared@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(deleted, result.User.Id);
        Assert.Null(await fixture.Context.MyUser.Where(x => x.Id == active).Select(x => x.GoogleId).SingleAsync());
    }

    [Fact]
    public async Task GoogleLogin_WithNoMatch_ShouldStillCreateADisabledAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");

        var result = await fixture.ExternalLogin("sub-1", "new@example.com");

        Assert.False(result.IsDeleted);
        Assert.False(result.User.Status);
        Assert.Equal("new@example.com", result.User.Account);
    }

    // ------------------------------------------------------------------ 模型與慣例

    [Fact]
    public void SoftDeletableEntities_ShouldCarryTheNamedFilter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

        var softDeletable = context.Model.GetEntityTypes()
            .Where(x => typeof(ISoftDeletable).IsAssignableFrom(x.ClrType))
            .ToList();

        Assert.Equal(
            new[] { nameof(Category), nameof(MyUser), nameof(Project), nameof(RoleView), nameof(Team) },
            softDeletable.Select(x => x.ClrType.Name).Order().ToArray());
        Assert.All(softDeletable, entityType =>
            Assert.Contains(entityType.GetDeclaredQueryFilters(), f => f.Key == ISoftDeletable.FilterName));
    }

    [Fact]
    public void EveryConcurrencyStampApply_ShouldBeFollowedByProtectFlags()
    {
        // 整筆覆蓋的存檔會把 IsDeleted 寫回 false。行為測試摸不到這個競態（存檔前的查詢已擋掉大部分情況），
        // 所以用原始碼守門：呼叫 ConcurrencyStampHelper.Apply 的檔案，Apply 與 ProtectFlags 的次數要一樣。
        var root = FindSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains("CustomerHelpAgent.Tests", StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(f => CountOf(f.Text, "ConcurrencyStampHelper.Apply(") != CountOf(f.Text, "SoftDeleteHelper.ProtectFlags("))
            .Select(f => Path.GetRelativePath(root, f.Path))
            .ToList();

        Assert.True(offenders.Count == 0, $"這些檔案呼叫 ConcurrencyStampHelper.Apply 但沒有對應的 ProtectFlags：{string.Join("、", offenders)}");
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Business")))
            {
                return dir.FullName;
            }

            var src = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(src, "CustomerHelpAgent.Business")))
            {
                return src;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent 原始碼目錄。");
    }

    private static DataRequest AllRows() => new() { CurrentPage = 1, PageSize = 100, Take = 0 };

    /// <summary>角色剛被標記為已刪除並存檔後，模擬另一個人把某位使用者設成這個主要角色（只觸發一次）。</summary>
    private sealed class AssignPrimaryRoleAfterSaveInterceptor(int userId, int roleId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            var roleJustDeleted = context.ChangeTracker.Entries<RoleView>().Any(e => e.Entity.Id == roleId && e.Entity.IsDeleted);
            if (!Fired && roleJustDeleted)
            {
                Fired = true;
                await context.Database.ExecuteSqlRawAsync("UPDATE MyUser SET RoleViewId = {0} WHERE Id = {1}", [roleId, userId], cancellationToken);
            }

            return result;
        }
    }

    private sealed class InterceptingFactory(SqliteConnection connection, IInterceptor interceptor) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly CurrentUserService currentUser = new();
        private readonly BootstrapSettings bootstrapSettings;

        private Fixture(SqliteConnection connection, BackendDBContext context, string supportAccount)
        {
            this.connection = connection;
            Context = context;
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
            bootstrapSettings = new BootstrapSettings { SupportAccount = supportAccount };

            // Id 刻意設很大，避免和測試建立的使用者撞號而觸發「不可刪除自己」。
            currentUser.CurrentUser = new CurrentUser { Id = 999_999, Account = "alice" };
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync(string supportAccount = "support")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context, supportAccount);
        }

        public void SignInAs(int id, string account) => currentUser.CurrentUser = new CurrentUser { Id = id, Account = account };

        public MyUserService UserService()
            => new(new TestDbContextFactory(connection), mapper, NullLogger<MyUserService>.Instance,
                new RbacWriteService(NewContext(), NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser,
                Options.Create(bootstrapSettings), PasswordTestDefaults.Policy(supportAccount: bootstrapSettings.SupportAccount));

        public RoleViewService RoleService(IInterceptor? interceptor = null)
            => new(interceptor is null ? new TestDbContextFactory(connection) : new InterceptingFactory(connection, interceptor),
                mapper, NullLogger<RoleViewService>.Instance, new RolePermissionService(),
                new RbacWriteService(NewContext(), NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser);

        public MyUserServiceLogin LoginService()
            => new(NewContext(), mapper, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
                new RolePermissionService(), new RecordingAuditLogService(), PasswordTestDefaults.Lockout(), new RecordingNotificationSender(), TimeProvider.System,
                TwoFactorTestDefaults.Service(new TestDbContextFactory(connection), supportAccount: bootstrapSettings.SupportAccount));

        public Task<ExternalLoginResult> ExternalLogin(string subject, string email)
            => new ExternalLoginService(NewContext(), NullLogger<ExternalLoginService>.Instance, new RecordingAuditLogService(), new RecordingNotificationSender())
                .FindOrCreateAsync("Google", subject, email, "Google User", MagicObjectHelper.預設角色);

        public async Task<IReadOnlyCollection<string>> PermissionKeysAsync(int userId)
        {
            await using var context = NewContext();
            return await new PermissionChecker(context, NullLogger<PermissionChecker>.Instance).GetEffectivePermissionKeysAsync(userId);
        }

        /// <summary>建立角色並寫入它的權限對應（與角色管理存檔相同的雙寫）。</summary>
        public async Task<int> AddRoleAsync(string name, string permissionKey)
        {
            var role = new RoleView { Name = name, TabViewJson = $"""["{permissionKey}"]""" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncRolePermissionsAsync(role.Id, [permissionKey]);
            Context.ChangeTracker.Clear();
            return role.Id;
        }

        /// <summary>直接寫入使用者與 UserRole（主要角色 + 額外角色），略過畫面驗證。</summary>
        public async Task<int> AddUserAsync(string account, int? roleId, int? extraRoleId = null, bool status = true, string? googleId = null, string? email = null)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = status, RoleViewId = roleId, GoogleId = googleId, Email = email };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            var roleIds = new[] { roleId, extraRoleId }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (roleIds.Count > 0)
            {
                await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncUserRolesAsync(user.Id, roleIds);
            }

            Context.ChangeTracker.Clear();
            return user.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }

        private BackendDBContext NewContext() => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
    }
}
