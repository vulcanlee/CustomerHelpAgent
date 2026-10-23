using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.Other;
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
