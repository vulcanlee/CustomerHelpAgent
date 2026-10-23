using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerHelpAgent.Tests;

public sealed class MyUserServicePasswordTests
{
    [Fact]
    public async Task ChangeOwnPasswordAsync_WithCorrectCurrentPassword_ShouldUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", "new-password-1", "new-password-1");

        Assert.True(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.StartsWith("PBKDF2", savedUser.Password);
        Assert.Equal(
            PasswordVerificationOutcome.Success,
            SecurePasswordHasher.VerifyPassword("new-password-1", savedUser.Password, savedUser.Salt));
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithWrongCurrentPassword_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "wrong-password", "new-password-1", "new-password-1");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithBlankNewPassword_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", " ", " ");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithMismatchedConfirmation_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", "new-password-1", "different-password");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_ForSupportAccount_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync(MagicObjectHelper.開發者帳號, "support-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "support-password", "new-password-1", "new-password-1");

        Assert.False(result.Success);
        Assert.Contains("support", result.Message, StringComparison.OrdinalIgnoreCase);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    /// <summary>Email 選填，有填就必須合法 —— 否則忘記密碼的信寄不出去，畫面也不會提醒。</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("user01@example.com", true)]
    [InlineData(" user01@example.com ", true)]
    [InlineData("support", false)]
    [InlineData("user01@", false)]
    public async Task BeforeSaveChecks_ShouldValidateEmailFormat(string? email, bool expectedValid)
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var existing = await fixture.AddUserAsync("alice", "password");
        var service = fixture.CreateService();

        var addResult = await service.BeforeAddCheckAsync(new MyUserAdapterModel { Account = "bob", Name = "bob", Email = email });
        var updateResult = await service.BeforeUpdateCheckAsync(
            new MyUserAdapterModel { Id = existing.Id, Account = "alice", Name = "alice", Email = email });

        Assert.Equal(expectedValid, addResult.Success);
        Assert.Equal(expectedValid, updateResult.Success);
        if (!expectedValid)
        {
            Assert.Contains("Email", addResult.Message);
        }
    }

    private sealed class MyUserServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private MyUserServiceFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<MyUserServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new MyUserServiceFixture(connection, context);
        }

        public MyUserService CreateService()
        {
            return new MyUserService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance),
                new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()),
                new CurrentUserService(),
                Options.Create(new BootstrapSettings()),
                PasswordTestDefaults.Policy());
        }

        public async Task<MyUser> AddUserAsync(string account, string password)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Salt = Guid.NewGuid().ToString(),
                Status = true
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
