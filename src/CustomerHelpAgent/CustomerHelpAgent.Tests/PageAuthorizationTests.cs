using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.Web.Components.Auths;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 守住「未登入的人看不到任何頁面內容」這件事（0.9.41）。
///
/// 0.9.41 之前頁面一律不標 [Authorize]，改在 OnInitializedAsync 內呼叫
/// AuthenticationStateHelper.Check() 命令式檢查。那個檢查要等元件先渲染一次才會導走，
/// 未登入者直接輸入 /App 之類的網址，會瞬間看到側邊欄、頁首與頁面內容。
///
/// 現在的防線分三層，本檔各有測試對應：
/// 1. HTTP 層：Pages/_Imports.razor 對整個 Pages/ 標 [Authorize]，未登入請求直接 302 到登入頁。
/// 2. 匿名頁面白名單：新頁面不小心標了 [AllowAnonymous]（或放在 Pages/ 以外又沒標）會被擋下。
/// 3. 檢查完成前不渲染：呼叫 Check() 的元件必須等 isAccessChecked 為 true 才畫出內容。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class PageAuthorizationTests : IClassFixture<ApiTestApplicationFactory>
{
    /// <summary>
    /// 允許匿名存取的頁面。⚠️ 新增項目前請確認該頁面**不含任何需要登入才能看的內容**。
    /// </summary>
    private static readonly string[] AnonymousPages =
    [
        "Home",     // "/" 啟動頁，只有品牌與載入中動畫
        "Error",    // 例外處理頁，登入頁自己出錯時也要能顯示
        "Login",
        "Logout",
        "Pending",  // Google 登入後等待審核
        "NotFound", // 狀態碼頁的重跑目標，要求登入會把 API 的 401/404 換成 302；內容靠 MainLayout 的閘門擋住
        "ForgotPassword", // 忘記密碼（0.9.60）：只有輸入框，送出後一律顯示同一句話
        "ResetPassword",  // 以信中連結重設密碼（0.9.60）：沒有有效 token 時只顯示「連結無效」
        "RefreshSession", // 換發這台裝置的 Cookie（0.9.103）：沒有原本的 Cookie 與有效 ticket 一律導去登出，不顯示任何內容
        "TwoFactor",      // 兩步驟驗證第二步（0.9.104）：沒有有效的待驗證 Cookie 一律導回登入頁，只有驗證碼輸入框
    ];

    private readonly ApiTestApplicationFactory factory;

    public PageAuthorizationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void EveryRoutablePage_ShouldDeclareItsAccessLevel()
    {
        var pages = GetRoutablePages();
        Assert.NotEmpty(pages);

        var undeclared = pages
            .Where(page => page.GetCustomAttribute<AuthorizeAttribute>() is null
                && page.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .Select(page => page.Name)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "每個可路由頁面都必須標 [Authorize] 或 [AllowAnonymous]（放在 Components/Pages 底下會自動繼承 [Authorize]）："
                + string.Join("、", undeclared));
    }

    [Fact]
    public void AnonymousPages_ShouldMatchTheAllowList()
    {
        var anonymous = GetRoutablePages()
            .Where(page => page.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(page => page.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AnonymousPages.Order(StringComparer.Ordinal), anonymous);
    }

    public static TheoryData<string> ProtectedRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var route in GetRoutablePages()
            .Where(page => page.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => !template.Contains('{'))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(route);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedPage_WithoutLogin_ShouldRedirectToLoginWithoutRenderingAnything(string route)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        AssertRedirectsToLogin(response, route);
        Assert.Equal(string.Empty, body);
    }

    /// <summary>
    /// 不存在的網址維持 404（API 呼叫端要靠它），回應裡也沒有任何頁面內容：
    /// 頁面是 prerender: false，HTTP 回應只有空殼；circuit 起來後 MainLayout 的閘門
    /// 在登入檢查通過前不渲染，並把未登入者導去登入頁。
    /// </summary>
    [Fact]
    public async Task UnknownUrl_WithoutLogin_ShouldKeep404AndRenderNoPageContent()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/no-such-page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Not Found", body);
        Assert.DoesNotContain("sidebar", body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Auths/Login")]
    [InlineData("/Auths/Pending")]
    [InlineData("/Auths/ForgotPassword")]
    [InlineData("/Auths/ResetPassword")]
    public async Task AnonymousPage_WithoutLogin_ShouldBeServed(string route)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// 已登入的人被停用或刪除後，帶著仍然有效的 Cookie 進到 /Auths/Logout，必須被導向登入頁。
    ///
    /// 0.9.95 之前登出頁沒有指定版面而套用 MainLayout；MainLayout 先跑 <c>Check</c>，發現使用者無效又導向
    /// /Auths/Logout，而這個導向發生在登出頁清掉 Cookie 之前 —— 瀏覽器顯示「重新導向太多次」。
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Logout_WithLiveCookieOfDisabledOrDeletedUser_ShouldRedirectToLogin(bool status, bool isDeleted)
    {
        var account = $"gone-{Guid.NewGuid():N}";
        int userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CustomerHelpAgent.AccessDatas.BackendDBContext>();
            var user = new CustomerHelpAgent.AccessDatas.Models.MyUser { Account = account, Name = account, Password = "x", Status = status, IsDeleted = isDeleted };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var options = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(CustomerHelpAgent.Share.Helpers.MagicObjectHelper.CookieScheme);
        var identity = new System.Security.Claims.ClaimsIdentity(
        [
            new(System.Security.Claims.ClaimTypes.Role, "User"),
            new(System.Security.Claims.ClaimTypes.Name, account),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, account),
            new(System.Security.Claims.ClaimTypes.Sid, userId.ToString()),
        ], CustomerHelpAgent.Share.Helpers.MagicObjectHelper.CookieScheme);
        var cookie = options.TicketDataFormat.Protect(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(identity), CustomerHelpAgent.Share.Helpers.MagicObjectHelper.CookieScheme));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Auths/Logout");
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={cookie}");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"預期導向，實際 {(int)response.StatusCode}");
        var location = response.Headers.Location!;
        var target = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        Assert.Equal("/Auths/Login", target);
    }

    [Fact]
    public async Task LoginPage_WithSsoDeleted_ShouldShowTheFixedMessage()
    {
        // 已刪除的使用者用 Google 登入時導回 /Auths/Login?sso=deleted（0.9.95 起）。
        using var client = factory.CreateClient();

        var body = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Auths/Login?sso=deleted"));

        Assert.Contains("此帳號已被刪除，請洽系統管理員。", body);
    }

    [Fact]
    public async Task LoginPage_WithUnknownSsoCode_ShouldNotEchoIt()
    {
        // 只認得固定代碼；網址上的其他值不可以被當成訊息顯示。
        // （表單的 action 會帶著 URL 編碼後的目前網址，那是框架行為且已編碼，所以比對原始 HTML 裡未編碼的標籤。）
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/Auths/Login?sso=" + Uri.EscapeDataString("<b>injected-marker</b>"));

        Assert.DoesNotContain("<b>injected-marker", body);
        Assert.DoesNotContain("&lt;b&gt;injected-marker", body);
        Assert.DoesNotContain("此帳號已被刪除", System.Net.WebUtility.HtmlDecode(body));
    }

    [Theory]
    [InlineData("/projects", "/projects")]
    [InlineData("/App?tab=1", "/App?tab=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/App")]
    [InlineData("", "/App")]
    [InlineData("https://evil.com", "/App")]
    [InlineData("//evil.com", "/App")]
    [InlineData("/\\evil.com", "/App")]
    [InlineData("/\t/evil.com", "/App")]
    [InlineData("evil.com", "/App")]
    public void ReturnUrlGuard_ShouldOnlyAllowLocalPaths(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrlGuard.Sanitize(input));
    }

    private static readonly Regex AuthenticationCheckCall = new(
        @"AuthenticationStateHelper\s*\.\s*Check\(",
        RegexOptions.Compiled);

    /// <summary>
    /// 不需要 isAccessChecked 閘門的元件：
    /// - SplashView：匿名的啟動頁，Check() 只用來決定要去 /App 還是登入頁，畫面只有品牌。
    /// - ChangePassword：isLoading 初值為 true，檢查期間只顯示 Spin，本身就是閘門。
    /// </summary>
    private static readonly string[] CheckWithoutGateAllowList = ["SplashView", "ChangePassword"];

    [Fact]
    public void ComponentsCallingAuthenticationCheck_ShouldRenderNothingUntilTheCheckCompletes()
    {
        var componentsRoot = FindComponentsRoot();
        var sourceFiles = new[] { "Views", "Pages" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(componentsRoot, folder), "*.razor*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal)
                || file.EndsWith(".razor.cs", StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) != "_Imports.razor")
            .ToList();

        var components = sourceFiles
            .Where(file => AuthenticationCheckCall.IsMatch(File.ReadAllText(file)))
            .Select(file => file.EndsWith(".razor.cs", StringComparison.Ordinal) ? file[..^3] : file)
            .Distinct()
            .Where(markup => !CheckWithoutGateAllowList.Contains(Path.GetFileNameWithoutExtension(markup)))
            .ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.True(components.Count >= 10, $"只掃到 {components.Count} 個呼叫 Check() 的元件，掃描路徑可能失效。");

        var violations = new List<string>();
        foreach (var markup in components)
        {
            var codeBehind = markup + ".cs";
            var code = File.ReadAllText(markup)
                + (File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : string.Empty);

            if (!File.ReadAllText(markup).Contains("!isAccessChecked", StringComparison.Ordinal)
                || !code.Contains("isAccessChecked = true", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(markup));
            }
        }

        Assert.True(
            violations.Count == 0,
            "呼叫 AuthenticationStateHelper.Check() 的元件，在檢查完成前不得渲染任何內容 ——"
                + "否則沒有權限的人會先看到工具列與表格框架，才換成「你沒有權限存取此頁面」。"
                + "請比照既有檢視：宣告 isAccessChecked，權限判定完成（通過或拒絕）後設為 true，"
                + "markup 最外層以 @if (!isAccessChecked) { } 擋住。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void MainLayout_ShouldRenderNothingUntilAuthenticated()
    {
        var markup = File.ReadAllText(Path.Combine(FindComponentsRoot(), "Layout", "MainLayout.razor"));

        Assert.Contains("@if (isAuthenticated)", markup);
    }

    private static void AssertRedirectsToLogin(HttpResponseMessage response, string requestedPath)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location;
        Assert.NotNull(location);

        var absolute = location!.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
        Assert.Equal("/Auths/Login", absolute.AbsolutePath);

        var returnUrl = System.Web.HttpUtility.ParseQueryString(absolute.Query)["ReturnUrl"];
        Assert.Equal(requestedPath, returnUrl);
    }

    private static List<Type> GetRoutablePages()
    {
        return typeof(ReturnUrlGuard).Assembly
            .GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type)
                && type.GetCustomAttributes<RouteAttribute>().Any())
            .ToList();
    }

    private static string FindComponentsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web/Components。");
    }
}
