using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>與 <see cref="ApiTestApplicationFactory"/> 相同，但開啟 Pickup 寄信（信寫到測試自己的暫存資料夾）。</summary>
public sealed class ApiTestApplicationFactoryWithPickupEmail : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，覆寫得掉 base 的 Provider=None；PickupDirectory 沿用 base 指到的暫存目錄。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:Provider"] = "Pickup",
            });
        });
    }
}

/// <summary>寄信啟用時：登入頁有「忘記密碼？」、兩個頁面可用、重設頁帶上防外洩的回應標頭。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailEnabledPagesTests : IClassFixture<ApiTestApplicationFactoryWithPickupEmail>
{
    private readonly ApiTestApplicationFactoryWithPickupEmail factory;

    public EmailEnabledPagesTests(ApiTestApplicationFactoryWithPickupEmail factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginPage_ShouldLinkToForgotPassword()
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/Auths/Login"));

        Assert.Contains("href=\"/Auths/ForgotPassword\"", body);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldRenderTheForm()
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/Auths/ForgotPassword"));

        Assert.Contains("name=\"Input.Identifier\"", body);
        Assert.DoesNotContain("未啟用寄信功能", body);
    }

    /// <summary>內建帳號：留在表單並明說不提供服務。</summary>
    [Fact]
    public async Task ForgotPasswordPage_WithSupportAccount_ShouldStayOnFormAndSayNotAllowed()
    {
        using var client = factory.CreateClient();

        var body = await PostForgotPasswordAsync(client, "support");

        Assert.Contains("不提供忘記密碼服務", body);
        Assert.Contains("name=\"Input.Identifier\"", body);
    }

    /// <summary>其他輸入（例如不存在的帳號）：中性說明，不可寫成「已經寄出」。</summary>
    [Fact]
    public async Task ForgotPasswordPage_WithUnknownAccount_ShouldShowNeutralNotice()
    {
        using var client = factory.CreateClient();

        var body = await PostForgotPasswordAsync(client, "ghost-account");

        Assert.Contains("申請已送出", body);
        Assert.Contains("class=\"info-message\"", body);
        Assert.DoesNotContain("已經寄出", body);
        Assert.DoesNotContain("name=\"Input.Identifier\"", body);
    }

    /// <summary>以瀏覽器的方式送出靜態 SSR 表單：帶上頁面給的 antiforgery token 與驗證碼。</summary>
    private static async Task<string> PostForgotPasswordAsync(HttpClient client, string identifier)
    {
        var page = await client.GetStringAsync("/Auths/ForgotPassword");
        var antiforgery = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var captcha = Regex.Match(page, "name=\"Input.CaptchaCode\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(antiforgery);
        Assert.NotEmpty(captcha);

        var response = await client.PostAsync("/Auths/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "forgot-password",
            ["__RequestVerificationToken"] = antiforgery,
            ["Input.CaptchaCode"] = captcha,
            ["Input.CaptchaInput"] = captcha,
            ["Input.Identifier"] = identifier,
        }));
        response.EnsureSuccessStatusCode();
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// token 在網址裡：no-referrer 讓頁面載入的外部資源拿不到網址，no-store 讓瀏覽器與代理不留副本。
    /// 無效 token 只顯示「連結無效」，不給表單。
    /// </summary>
    [Fact]
    public async Task ResetPasswordPage_WithBadToken_ShouldShowInvalidLinkWithProtectiveHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Auths/ResetPassword?token=forged");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("重設連結無效或已過期", body);
        Assert.DoesNotContain("name=\"Input.NewPassword\"", body);
    }
}

/// <summary>寄信未啟用（出貨預設 None）時：沒有連結，兩頁只顯示「未啟用」。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailDisabledPagesTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public EmailDisabledPagesTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginPage_ShouldNotLinkToForgotPassword()
    {
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/Auths/Login");

        Assert.DoesNotContain("/Auths/ForgotPassword", body);
    }

    [Theory]
    [InlineData("/Auths/ForgotPassword")]
    [InlineData("/Auths/ResetPassword?token=anything")]
    public async Task ResetPages_ShouldSayTheFeatureIsDisabled(string route)
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync(route));

        Assert.Contains("未啟用寄信功能", body);
        Assert.DoesNotContain("<form", body);
    }
}
