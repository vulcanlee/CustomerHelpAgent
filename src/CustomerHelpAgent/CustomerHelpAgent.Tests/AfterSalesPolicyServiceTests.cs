using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

public sealed class AfterSalesPolicyServiceTests
{
    // UTC 02:00 = 本地（UTC+8）10:00
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);

    private static AfterSalesPolicy Row(string code, int version, string category, DateTime from, DateTime? to = null) => new()
    {
        Code = code,
        Type = AfterSalesType.SizeExchange,
        Category = category,
        Title = code,
        Content = "內容",
        Source = "來源",
        Version = version,
        EffectiveFrom = from,
        EffectiveTo = to,
        WindowDays = 7,
        CreatedAt = from,
    };

    private static async Task<(SqliteConnection Conn, BackendDBContext Ctx)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options);
        await ctx.Database.EnsureCreatedAsync();
        return (conn, ctx);
    }

    private static AfterSalesPolicyService Create(SqliteConnection conn, RecordingAuditLogService audit) => new(
        new TestDbContextFactory(conn),
        audit,
        new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "merchant1" } },
        new ManualTimeProvider(UtcNow),
        NullLogger<AfterSalesPolicyService>.Instance);

    [Fact]
    public async Task GetEffectiveAsync_ShouldReturnFoundNoPolicyConflict()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        ctx.AfterSalesPolicy.AddRange(
            Row("A", 1, ProductCategories.Shoes, LocalNow.AddDays(-30)),
            Row("B", 1, ProductCategories.All, LocalNow.AddDays(-30)));
        var other = Row("C", 1, ProductCategories.Apparel, LocalNow.AddDays(-30));
        other.Type = AfterSalesType.ReturnRefund;
        ctx.AfterSalesPolicy.Add(other);
        await ctx.SaveChangesAsync();
        var service = Create(conn, new RecordingAuditLogService());

        var conflict = await service.GetEffectiveAsync(AfterSalesType.SizeExchange, ProductCategories.Shoes, LocalNow);
        var found = await service.GetEffectiveAsync(AfterSalesType.ReturnRefund, ProductCategories.Apparel, LocalNow);
        var none = await service.GetEffectiveAsync(AfterSalesType.QualityIssue, ProductCategories.Shoes, LocalNow);
        var before = await service.GetEffectiveAsync(AfterSalesType.ReturnRefund, ProductCategories.Apparel, LocalNow.AddDays(-60));

        Assert.Equal(PolicyLookupStatus.Conflict, conflict.Status);
        Assert.Equal(2, conflict.Policies.Count);
        Assert.Equal(PolicyLookupStatus.Found, found.Status);
        Assert.Single(found.Policies);
        Assert.Equal(PolicyLookupStatus.NoPolicy, none.Status);
        Assert.Equal(PolicyLookupStatus.NoPolicy, before.Status);
    }

    [Fact]
    public async Task AddVersionAsync_ShouldCloseOldVersionAndKeepItQueryable()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        ctx.AfterSalesPolicy.Add(Row("SIZE", 1, ProductCategories.Shoes, LocalNow.AddDays(-30)));
        await ctx.SaveChangesAsync();
        var audit = new RecordingAuditLogService();
        var service = Create(conn, audit);

        var result = await service.AddVersionAsync(new AfterSalesPolicyModel
        {
            Code = "SIZE",
            Type = AfterSalesType.SizeExchange,
            Category = ProductCategories.Shoes,
            Title = "新版",
            Content = "新內容",
            Source = "來源",
            WindowDays = 14,
        });

        Assert.True(result.Success);
        var v1 = await ctx.AfterSalesPolicy.AsNoTracking().SingleAsync(x => x.Version == 1);
        Assert.Equal(LocalNow, v1.EffectiveTo);
        var effective = await service.GetEffectiveAsync(AfterSalesType.SizeExchange, ProductCategories.Shoes, LocalNow);
        Assert.Equal(PolicyLookupStatus.Found, effective.Status);
        Assert.Equal(2, effective.Policies[0].Version);
        Assert.Equal(LocalNow, effective.Policies[0].EffectiveFrom);
        Assert.Equal(14, effective.Policies[0].WindowDays);
        var all = await service.GetVersionsAsync(true);
        Assert.Equal([2, 1], all.Select(x => x.Version));
        var current = await service.GetVersionsAsync(false);
        Assert.Single(current);
        Assert.Equal(AuditActions.Shop.PolicyAddVersion, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnThatVersionEvenWhenExpired_OrNullWhenMissing()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        var expired = Row("SIZE", 1, ProductCategories.Shoes, LocalNow.AddDays(-30), LocalNow.AddDays(-1));
        ctx.AfterSalesPolicy.Add(expired);
        await ctx.SaveChangesAsync();
        var service = Create(conn, new RecordingAuditLogService());

        var found = await service.GetByIdAsync(expired.Id);
        var missing = await service.GetByIdAsync(expired.Id + 100);

        Assert.NotNull(found);
        Assert.Equal("SIZE", found.Code);
        Assert.Equal(1, found.Version);
        Assert.Equal(LocalNow.AddDays(-1), found.EffectiveTo);
        Assert.Null(missing);
    }
}
