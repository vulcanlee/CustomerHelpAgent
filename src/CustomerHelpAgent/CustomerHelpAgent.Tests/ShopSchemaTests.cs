using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

public sealed class ShopSchemaTests
{
    private static async Task<(SqliteConnection, BackendDBContext)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    [Fact]
    public async Task FreshSchema_ShouldEnforceShopUniqueIndexes()
    {
        var (connection, context) = await CreateAsync();
        await using var _ = connection;
        await using var __ = context;

        context.Product.Add(new Product { Code = "P1", Name = "A", Category = ProductCategories.Shoes, Price = 100m });
        context.Product.Add(new Product { Code = "P1", Name = "B", Category = ProductCategories.Shoes, Price = 200m });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        static AfterSalesPolicy NewPolicy() => new()
        {
            Code = "RET",
            Type = AfterSalesType.ReturnRefund,
            Category = ProductCategories.All,
            Title = "t",
            Content = "c",
            Source = "s",
            Version = 1,
            EffectiveFrom = new DateTime(2026, 1, 1),
        };
        context.AfterSalesPolicy.Add(NewPolicy());
        context.AfterSalesPolicy.Add(NewPolicy());
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
