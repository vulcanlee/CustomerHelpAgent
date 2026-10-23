using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 顧客商店版面（售後展示 M1）。它和 MainLayout 一樣必須在登入檢查完成前什麼都不渲染，
/// 登出也必須經過 <c>LogoutConfirm</c> 的二次確認；它刻意沒有側邊欄、操作說明與 OverlayStyles。
/// </summary>
public sealed class ShopLayoutTests : ComponentTestBase
{
    [Fact]
    public void ShopLayout_BeforeAuthCheck_ShouldRenderNothing()
    {
        // 登入狀態永遠不回來：Check 卡在第一個 await，版面必須維持空白（含 Body）。
        Services.AddSingleton<AuthenticationStateProvider>(new PendingAuthenticationStateProvider());
        Services.AddSingleton(new CurrentUserService());
        Services.AddSingleton(CreateHelperThatOnlyReachesTheFirstAwait());

        var cut = Render<ShopLayout>(parameters => parameters
            .Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p class=\"body-marker\">頁面內容</p>"))));

        Assert.Empty(cut.FindAll(".body-marker"));
        Assert.DoesNotContain("示範鞋服商店", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("@if (isAuthenticated)", ReadLayoutMarkup(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShopLayout_ShouldNotLinkDirectlyToLogout()
    {
        var markup = ReadLayoutMarkup();
        var code = File.ReadAllText(LayoutPath("ShopLayout.razor.cs"));

        Assert.DoesNotContain("auths/logout", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LogoutConfirm.RequestAsync", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ShopLayout_ShouldNotContainSidebarHelpOrOverlayStyles()
    {
        var markup = ReadLayoutMarkup();

        // OverlayStyles 只能在 Routes.razor 渲染一次（速查表 §6.8）；顧客版面沒有側欄與操作說明。
        Assert.DoesNotContain("<OverlayStyles", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavMenu", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<PageHelpDialog", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check() 在讀到登入狀態之前不會碰其他相依服務，所以這裡只給 logger；
    /// 若日後 Check() 在第一個 await 之前就用到其他服務，這個測試會以 NullReferenceException 提醒你。
    /// </summary>
    private static AuthenticationStateHelper CreateHelperThatOnlyReachesTheFirstAwait() => new(
        NullLogger<AuthenticationStateHelper>.Instance,
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static string ReadLayoutMarkup() => File.ReadAllText(LayoutPath("ShopLayout.razor"));

    private static string LayoutPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web", "Components", "Layout", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Components", "Layout", fileName);
            if (File.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 Components/Layout/{fileName}。");
    }

    private sealed class PendingAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => new TaskCompletionSource<AuthenticationState>().Task;
    }
}

/// <summary>
/// 展示身分切換的提示列（1.0.5 起）：Cookie 帶展示者 claim 時，顧客版面頂端顯示「展示模式」與「回到展示者」；
/// 按下去取一張切回票券、整頁導向 <c>/Auths/DemoSwitch</c>。跑完真的登入檢查（<see cref="ShopViewTestBase"/>）。
/// </summary>
public sealed class ShopLayoutDemoBannerTests : ShopViewTestBase
{
    private readonly DemoSwitchTicketService tickets = new(TimeProvider.System, NullLogger<DemoSwitchTicketService>.Instance);

    public ShopLayoutDemoBannerTests()
    {
        Services.AddSingleton(TestDemoControl.Create(Factory, enabled: true));
        Services.AddSingleton(tickets);
    }

    [Fact]
    public void ShopLayout_PresenterClaim_ShowsBackButton()
    {
        UsePrincipal(presenterId: 4242);

        var cut = Render<ShopLayout>();
        cut.WaitForAssertion(() => Assert.Contains("展示模式：目前以 顧客一 身分檢視", cut.Markup, StringComparison.Ordinal));

        cut.Find("button.demo-banner-back").Click();

        var uri = new Uri(Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("/Auths/DemoSwitch", uri.AbsolutePath);
        Assert.StartsWith("?ticket=", uri.Query, StringComparison.Ordinal);
        var ticket = Uri.UnescapeDataString(uri.Query["?ticket=".Length..]);
        Assert.True(tickets.TryRedeem(ticket, out var redeemed));
        Assert.Equal(new DemoSwitchTicket(CustomerId, DemoSwitchPurpose.SwitchBack, null), redeemed);
    }

    [Fact]
    public void ShopLayout_WithoutPresenterClaim_HidesBanner()
    {
        UsePrincipal(presenterId: null);

        var cut = Render<ShopLayout>();
        cut.WaitForAssertion(() => Assert.Contains("示範鞋服商店", cut.Markup, StringComparison.Ordinal));

        Assert.Empty(cut.FindAll("button.demo-banner-back"));
        Assert.DoesNotContain("展示模式", cut.Markup, StringComparison.Ordinal);
    }

    private void UsePrincipal(int? presenterId)
    {
        using var context = Factory.CreateDbContext();
        var stamp = context.MyUser.AsNoTracking().Single(x => x.Id == CustomerId).SecurityStamp;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Sid, CustomerId.ToString(CultureInfo.InvariantCulture)),
            new(MagicObjectHelper.SecurityStampClaimType, stamp),
        };
        if (presenterId is { } id)
        {
            claims.Add(new(DemoClaims.PresenterId, id.ToString(CultureInfo.InvariantCulture)));
            claims.Add(new(DemoClaims.PresenterStamp, "presenter-stamp"));
        }

        Services.AddSingleton<AuthenticationStateProvider>(new StaticAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))));
    }

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
