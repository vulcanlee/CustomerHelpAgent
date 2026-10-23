using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using CustomerHelpAgent.Web.Extensions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 限流必須「依呼叫端分割」。
///
/// 0.4.34 之前用的是 <c>options.AddFixedWindowLimiter("api", ...)</c> ——
/// 那是**全站共用的單一計數器**，任一呼叫端每分鐘打滿 120 次，
/// 其他所有使用者都會拿到 429。等同一行就能癱瘓整站 API。
///
/// 這裡驗證分割鍵會因呼叫端而不同；若有人改回不分割的寫法，
/// 所有呼叫端會拿到同一個鍵，測試就會紅。
/// </summary>
public sealed class RateLimitPartitionTests
{
    [Fact]
    public void DifferentIpAddresses_ShouldGetDifferentPartitions()
    {
        var first = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.1"));
        var second = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.2"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SameIpAddress_ShouldGetSamePartition()
    {
        var first = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.1"));
        var second = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.1"));

        Assert.Equal(first, second);
    }

    /// <summary>已驗證身分優先於 IP：同一個人換 IP 仍受同一份配額。</summary>
    [Fact]
    public void AuthenticatedUser_ShouldPartitionByIdentity_NotIp()
    {
        var first = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.1", userName: "alice"));
        var second = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "198.51.100.9", userName: "alice"));
        var other = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: "203.0.113.1", userName: "bob"));

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void AnonymousWithoutIp_ShouldFallBackToSharedPartition()
    {
        var key = ServiceCollectionExtensions.ResolvePartitionKey(CreateContext(ip: null));

        Assert.Equal("anonymous", key);
    }

    private static HttpContext CreateContext(string? ip, string? userName = null)
    {
        var context = new DefaultHttpContext();

        if (ip is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        }

        if (userName is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], authenticationType: "Test"));
        }

        return context;
    }
}
