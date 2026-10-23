using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 忘記密碼的核心規則。畫面只顯示同一句話（內建帳號除外），所以「有沒有寄、為什麼沒寄」全靠這裡驗證。
/// </summary>
public sealed class PasswordResetServiceTests
{
    private const string ResetPageUrl = "https://erp.example.com/Auths/ResetPassword";

    #region 申請

    [Fact]
    public async Task RequestAsync_ByAccount_ShouldQueueOneMessageAndStoreOnlyTheHash()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "alice@example.com");

        await fixture.Service.RequestAsync("alice", ResetPageUrl);

        var message = Assert.Single(fixture.Queue.Enqueued);
        Assert.Equal("alice@example.com", message.To);
        Assert.Equal(EmailKinds.PasswordReset, message.Kind);

        var token = ExtractToken(message);
        var row = await fixture.Context.PasswordResetToken.AsNoTracking().SingleAsync();
        Assert.Equal(Sha256Hex(token), row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash);
        Assert.Contains(fixture.Audit.Entries, e => e.Action == "Password.ResetRequested" && e.Success);
    }

    [Fact]
    public async Task RequestAsync_ByEmail_ShouldMatchCaseInsensitively()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "Alice@Example.com");

        await fixture.Service.RequestAsync("ALICE@example.COM", ResetPageUrl);

        Assert.Equal("Alice@Example.com", Assert.Single(fixture.Queue.Enqueued).To);
    }

    /// <summary>輸入值同時是某人的帳號與另一人的 Email 時，以帳號為準，不寄給 Email 那一位。</summary>
    [Fact]
    public async Task RequestAsync_WhenInputMatchesAccount_ShouldNotFallBackToEmail()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("shared@example.com", email: "owner@example.com");
        await fixture.AddUserAsync("bob", email: "shared@example.com");

        await fixture.Service.RequestAsync("shared@example.com", ResetPageUrl);

        Assert.Equal("owner@example.com", Assert.Single(fixture.Queue.Enqueued).To);
    }

    [Fact]
    public async Task RequestAsync_WhenEmailIsShared_ShouldSendOneMessagePerAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "team@example.com");
        await fixture.AddUserAsync("bob", email: "team@example.com");

        await fixture.Service.RequestAsync("team@example.com", ResetPageUrl);

        Assert.Equal(2, fixture.Queue.Enqueued.Count);
        Assert.Contains(fixture.Queue.Enqueued, m => m.TextBody.Contains("「alice」"));
        Assert.Contains(fixture.Queue.Enqueued, m => m.TextBody.Contains("「bob」"));
        Assert.Equal(2, await fixture.Context.PasswordResetToken.CountAsync());
        Assert.Equal(2, fixture.Queue.Enqueued.Select(ExtractToken).Distinct().Count());
    }

    [Fact]
    public async Task RequestAsync_WhenNothingMatches_ShouldOnlyAudit()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Service.RequestAsync("ghost", ResetPageUrl);

        Assert.Empty(fixture.Queue.Enqueued);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("Password.ResetRequested", entry.Action);
        Assert.False(entry.Success);
        Assert.Equal("reason=NotFound", entry.Detail);
    }

    public static TheoryData<string, string?, bool, string, string?> IneligibleAccounts => new()
    {
        { MagicObjectHelper.開發者帳號, "support@example.com", true, "local", "Support" },
        { "disabled", "disabled@example.com", false, "local", "Disabled" },
        { "google-only", "google@example.com", true, "", "NoLocalPassword" },
        { "no-email", null, true, "local", "InvalidEmail" },
        { "bad-email", "support", true, "local", "InvalidEmail" },
    };

    /// <summary>不符資格的帳號：不產生 token、不寄信，但稽核要留下原因。</summary>
    [Theory]
    [MemberData(nameof(IneligibleAccounts))]
    public async Task RequestAsync_WithIneligibleAccount_ShouldNotSend(
        string account, string? email, bool status, string password, string? expectedReason)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync(account, email: email, status: status, rawPassword: password);

        await fixture.Service.RequestAsync(account, ResetPageUrl);

        Assert.Empty(fixture.Queue.Enqueued);
        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync());
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.False(entry.Success);
        Assert.Equal($"reason={expectedReason}", entry.Detail);
    }

    /// <summary>內建帳號名稱是唯一可以明說的情況（名稱公開）；稽核仍以 UserId 記下 reason=Support。</summary>
    [Theory]
    [InlineData("support")]
    [InlineData(" SUPPORT ")]
    public async Task RequestAsync_WithSupportAccountName_ShouldSayNotAllowed(string input)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync(MagicObjectHelper.開發者帳號, email: "support@example.com");

        var result = await fixture.Service.RequestAsync(input, ResetPageUrl);

        Assert.Equal(PasswordResetRequestResult.SupportAccountNotAllowed, result);
        Assert.Empty(fixture.Queue.Enqueued);
        Assert.Contains(fixture.Audit.Entries, e => e.Detail == "reason=Support");
    }

    /// <summary>防列舉：除了內建帳號名稱，寄出、找不到、不符資格都必須回同一個結果。</summary>
    [Theory]
    [InlineData("alice")]
    [InlineData("ghost")]
    [InlineData("bad-email")]
    [InlineData("disabled")]
    public async Task RequestAsync_ForAnyOtherInput_ShouldReturnAccepted(string input)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "alice@example.com");
        await fixture.AddUserAsync("bad-email", email: "support");
        await fixture.AddUserAsync("disabled", email: "disabled@example.com", status: false);

        var result = await fixture.Service.RequestAsync(input, ResetPageUrl);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
    }

    /// <summary>Google 帳號設過本地密碼就有密碼可忘，允許重設。</summary>
    [Fact]
    public async Task RequestAsync_GoogleAccountWithLocalPassword_ShouldBeAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("g@example.com", email: "g@example.com", oauthProvider: "Google");

        await fixture.Service.RequestAsync("g@example.com", ResetPageUrl);

        Assert.Single(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task RequestAsync_WithinCooldown_ShouldNotSendAgain()
    {
        await using var fixture = await Fixture.CreateAsync(cooldownSeconds: 60);
        await fixture.AddUserAsync("alice", email: "alice@example.com");

        await fixture.Service.RequestAsync("alice", ResetPageUrl);
        await fixture.Service.RequestAsync("alice", ResetPageUrl);

        Assert.Single(fixture.Queue.Enqueued);
        Assert.Contains(fixture.Audit.Entries, e => e.Detail == "reason=Cooldown");
    }

    /// <summary>新申請作廢舊 token：同一帳號永遠只有最新一封信的連結有效。</summary>
    [Fact]
    public async Task RequestAsync_WithoutCooldown_ShouldInvalidatePreviousLink()
    {
        await using var fixture = await Fixture.CreateAsync(cooldownSeconds: 0);
        await fixture.AddUserAsync("alice", email: "alice@example.com");

        await fixture.Service.RequestAsync("alice", ResetPageUrl);
        await fixture.Service.RequestAsync("alice", ResetPageUrl);

        Assert.Equal(2, fixture.Queue.Enqueued.Count);
        Assert.Equal(1, await fixture.Context.PasswordResetToken.CountAsync());
        var (oldValid, _) = await fixture.Service.ValidateTokenAsync(ExtractToken(fixture.Queue.Enqueued[0]));
        var (newValid, _) = await fixture.Service.ValidateTokenAsync(ExtractToken(fixture.Queue.Enqueued[1]));
        Assert.False(oldValid);
        Assert.True(newValid);
    }

    [Fact]
    public async Task RequestAsync_LinkShouldPointAtTheGivenResetPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "alice@example.com");

        await fixture.Service.RequestAsync("alice", ResetPageUrl);

        Assert.Contains($"{ResetPageUrl}?token=", Assert.Single(fixture.Queue.Enqueued).TextBody);
    }

    #endregion

    #region 檢查連結

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task ValidateTokenAsync_WithUnknownToken_ShouldBeInvalid(string? token)
    {
        await using var fixture = await Fixture.CreateAsync();

        var (valid, account) = await fixture.Service.ValidateTokenAsync(token);

        Assert.False(valid);
        Assert.Null(account);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithFreshToken_ShouldReturnAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = await fixture.RequestTokenAsync("alice");

        var (valid, account) = await fixture.Service.ValidateTokenAsync(token);

        Assert.True(valid);
        Assert.Equal("alice", account);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithExpiredToken_ShouldBeInvalid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = await fixture.RequestTokenAsync("alice");
        await fixture.Context.PasswordResetToken.ExecuteUpdateAsync(
            s => s.SetProperty(t => t.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));

        var (valid, _) = await fixture.Service.ValidateTokenAsync(token);

        Assert.False(valid);
    }

    #endregion

    #region 重設

    [Fact]
    public async Task ResetAsync_WithValidToken_ShouldChangePasswordUnlockAndConsumeToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", email: "alice@example.com", locked: true);
        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true));
        var token = await fixture.RequestTokenAsync("alice");
        fixture.Queue.Enqueued.Clear();

        var result = await fixture.Service.ResetAsync(token, "NewPass#2026", "NewPass#2026");

        Assert.True(result.Success);
        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(
            PasswordVerificationOutcome.Success,
            SecurePasswordHasher.VerifyPassword("NewPass#2026", saved.Password, saved.Salt));
        Assert.Equal(0, saved.AccessFailedCount);
        Assert.Null(saved.LockoutEndUtc);
        Assert.False(saved.MustChangePassword);   // 0.9.101 起經密碼原則套用：清旗標、記時間、寫歷史
        Assert.NotNull(saved.PasswordChangedAtUtc);
        Assert.Equal(1, await fixture.Context.PasswordHistory.CountAsync(x => x.MyUserId == user.Id));
        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync());
        Assert.Contains(fixture.Audit.Entries, e => e.Action == "Password.ResetCompleted" && e.Success);
        Assert.Equal(EmailKinds.PasswordChanged, Assert.Single(fixture.Queue.Enqueued).Kind);
    }

    /// <summary>單次使用：同一個連結第二次送出一律失敗。</summary>
    [Fact]
    public async Task ResetAsync_SameTokenTwice_ShouldFailTheSecondTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "alice@example.com");
        var token = await fixture.RequestTokenAsync("alice");

        var first = await fixture.Service.ResetAsync(token, "NewPass#2026", "NewPass#2026");
        var second = await fixture.Service.ResetAsync(token, "Another#2026", "Another#2026");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(PasswordResetService.InvalidLinkMessage, second.Message);
    }

    /// <summary>密碼規則或歷史不過時不可消耗 token，否則使用者打錯一次就得重新申請（0.9.101 起含「與目前密碼相同」）。</summary>
    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("OldPass#2025", "OldPass#2025")]
    [InlineData("abcdef", "abcdeg")]
    [InlineData("123456", "123456")]
    [InlineData("      ", "      ")]
    public async Task ResetAsync_WithRejectedPassword_ShouldFailAndKeepToken(string newPassword, string confirmPassword)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("alice", email: "alice@example.com");
        var token = await fixture.RequestTokenAsync("alice");

        var result = await fixture.Service.ResetAsync(token, newPassword, confirmPassword);

        Assert.False(result.Success);
        Assert.NotEqual(PasswordResetService.InvalidLinkMessage, result.Message);
        var (stillValid, _) = await fixture.Service.ValidateTokenAsync(token);
        Assert.True(stillValid);
    }

    [Fact]
    public async Task ResetAsync_WhenAccountDisabledAfterRequest_ShouldFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", email: "alice@example.com");
        var token = await fixture.RequestTokenAsync("alice");
        await fixture.Context.MyUser.Where(x => x.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, false));

        var result = await fixture.Service.ResetAsync(token, "NewPass#2026", "NewPass#2026");

        Assert.False(result.Success);
        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync());
        Assert.Contains(fixture.Audit.Entries, e => e.Action == "Password.ResetFailed" && e.Detail == "reason=Ineligible");
    }

    [Fact]
    public async Task ResetAsync_WithUnknownToken_ShouldFailAndAudit()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.ResetAsync("forged", "NewPass#2026", "NewPass#2026");

        Assert.False(result.Success);
        Assert.Contains(fixture.Audit.Entries, e => e.Action == "Password.ResetFailed" && e.Detail == "reason=InvalidToken");
    }

    /// <summary>
    /// 刪除有未用 token 的使用者必須成功：<c>MyUserService.DeleteAsync</c> 不先刪相依資料，
    /// 全靠資料庫的 ON DELETE CASCADE。FK 若被全域 Restrict 迴圈蓋掉，這裡會失敗。
    /// </summary>
    [Fact]
    public async Task DeletingUserWithPendingToken_ShouldCascade()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", email: "alice@example.com");
        await fixture.RequestTokenAsync("alice");

        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync());
    }

    #endregion

    [Theory]
    [InlineData("Abcdef12", "Abcdef12", true)]
    [InlineData("Abcde12", "Abcde12", false)]
    [InlineData("abcdefgh", "abcdefgh", false)]
    [InlineData(null, null, false)]
    [InlineData("Abcdef12", "ABCDEF12", false)]
    [InlineData("123456", "123456", false)]
    public async Task ValidateNewPassword_ShouldApplyThePolicyAndConfirmation(string? newPassword, string? confirmPassword, bool expectedValid)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(expectedValid, fixture.Service.ValidateNewPassword(newPassword, confirmPassword) is null);
    }

    private static readonly Regex TokenInLink = new(@"token=([A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    private static string ExtractToken(EmailMessage message) => TokenInLink.Match(message.TextBody).Groups[1].Value;

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(SqliteConnection connection, BackendDBContext context, int cooldownSeconds)
        {
            this.connection = connection;
            Context = context;

            var systemSettings = new SystemSettings();
            systemSettings.SystemInformation.SystemName = "測試系統";

            Service = new PasswordResetService(
                new TestDbContextFactory(connection),
                Queue,
                Audit,
                new StaticOptionsMonitor<PasswordResetSettings>(new PasswordResetSettings { TokenLifetimeMinutes = 30, RequestCooldownSeconds = cooldownSeconds }),
                new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(systemSettings)),
                PasswordTestDefaults.Policy(),
                NullLogger<PasswordResetService>.Instance);
        }

        public BackendDBContext Context { get; }

        public CapturingEmailQueue Queue { get; } = new();

        public RecordingAuditLogService Audit { get; } = new();

        public PasswordResetService Service { get; }

        public static async Task<Fixture> CreateAsync(int cooldownSeconds = 60)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            return new Fixture(connection, context, cooldownSeconds);
        }

        public async Task<MyUser> AddUserAsync(
            string account,
            string? email,
            bool status = true,
            string rawPassword = "local",
            string? oauthProvider = null,
            bool locked = false)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Email = email,
                Status = status,
                Salt = Guid.NewGuid().ToString(),
                Password = rawPassword.Length == 0 ? string.Empty : SecurePasswordHasher.HashPassword("OldPass#2025"),
                OAuthProvider = oauthProvider,
                AccessFailedCount = locked ? 5 : 0,
                LockoutEndUtc = locked ? DateTime.UtcNow.AddMinutes(15) : null,
            };

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        /// <summary>申請一次並從信中取回原始 token（資料庫裡只有雜湊）。</summary>
        public async Task<string> RequestTokenAsync(string account)
        {
            if (!await Context.MyUser.AnyAsync(x => x.Account == account))
            {
                await AddUserAsync(account, email: $"{account}@example.com");
            }

            await Service.RequestAsync(account, ResetPageUrl);
            return ExtractToken(Queue.Enqueued.Last());
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
