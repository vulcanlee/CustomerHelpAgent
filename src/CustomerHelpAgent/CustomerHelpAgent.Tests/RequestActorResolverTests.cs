using System.Security.Claims;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 例外情境的帳號解析（LOG-01）。claim 對應在 Cookie 與 JWT 中相反，
/// 而 Cookie 的 <see cref="ClaimTypes.Name"/> 是<b>使用者姓名（個資）</b>，絕不可被當成帳號記下。
/// </summary>
public sealed class RequestActorResolverTests
{
    [Fact]
    public void Resolve_CookiePrincipal_ShouldUseNameIdentifierAndSid()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "王小明"),
            new Claim(ClaimTypes.NameIdentifier, "support"),
            new Claim(ClaimTypes.Sid, "7"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Equal("support", account);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void Resolve_CookiePrincipal_ShouldNeverReturnPersonName()
    {
        // /api/project-files 走 Cookie。0.9.77 之前依路徑判斷成 JWT，會把姓名當帳號記下。
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "王小明"),
            new Claim(ClaimTypes.NameIdentifier, "support"),
            new Claim(ClaimTypes.Sid, "7"));

        var (account, _) = RequestActorResolver.Resolve(principal);

        Assert.NotEqual("王小明", account);
    }

    [Fact]
    public void Resolve_JwtPrincipal_ShouldUseNameAndNameIdentifier()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "support"),
            new Claim(ClaimTypes.NameIdentifier, "7"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Equal("support", account);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void Resolve_Anonymous_ShouldReturnNulls()
    {
        var (account, userId) = RequestActorResolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Null(account);
        Assert.Null(userId);
    }

    [Fact]
    public void Resolve_AuthenticatedWithoutUsableIds_ShouldReturnNulls()
    {
        var principal = Authenticated(new Claim(ClaimTypes.Name, "王小明"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Null(account);
        Assert.Null(userId);
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));
}
