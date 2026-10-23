using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 與 <see cref="ApiTestApplicationFactory"/> 相同，但 <see cref="PasswordResetService"/> 一解析就拋例外，
/// 讓匿名的「忘記密碼」頁在 SSR 階段失敗（<c>/Error</c> 頁不用這個服務，不受影響）。
/// </summary>
public sealed class ApiTestApplicationFactoryWithThrowingPage : ApiTestApplicationFactory
{
    public const string ProbeMessage = "Integration error page probe.";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
            services.AddScoped<PasswordResetService>(_ => throw new InvalidOperationException(ProbeMessage)));
    }
}

/// <summary>
/// 迴歸測試（0.9.115）：Blazor 頁面在 SSR 階段拋例外時，使用者要看到 <c>/Error</c> 錯誤頁與追蹤碼。
/// 少了 <c>createScopeForErrors: true</c> 時，重跑 <c>/Error</c> 沿用同一個 DI scope，
/// NavigationManager 已初始化而拋 "already initialized"，使用者只拿到空白 500。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ErrorPageRenderTests : IClassFixture<ApiTestApplicationFactoryWithThrowingPage>
{
    private readonly ApiTestApplicationFactoryWithThrowingPage factory;

    public ErrorPageRenderTests(ApiTestApplicationFactoryWithThrowingPage factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PageThrowingDuringRender_ShouldShowErrorPageWithTraceCode()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Auths/ForgotPassword");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("系統發生未預期的錯誤", body);
        Assert.Contains("錯誤追蹤碼", body);
    }
}

/// <summary>
/// 迴歸測試（0.9.115）：Host 空白（HTTP/1.0 可以不帶）或組不成網址的請求由 <c>UseRejectInvalidHost</c> 直接回 400，
/// 不進 Blazor —— 否則組 BaseUri 得到 <c>https:///</c> 而拋 UriFormatException，連 /Error 都失敗，一次記 5 筆 ERROR。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class InvalidHostRequestTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public InvalidHostRequestTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    public async Task RequestWithInvalidHost_ShouldReturn400WithoutRecordingException(string host)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/";
            c.Request.Host = new HostString(host);
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        // 例外紀錄由背景寫入器寫入，留一點時間再數。
        await Task.Delay(TimeSpan.FromSeconds(1));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.ExceptionLog.CountAsync(x => x.Message.Contains("hostname could not be parsed")));
    }
}
