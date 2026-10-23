using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 系統只支援繁體中文（0.9.136 起）：英文瀏覽器也拿到 zh-TW。
/// 之前 en-US 也在支援清單裡，<c>Routes.razor</c> 每次渲染都以目前文化呼叫 AntDesign 的 <c>LocaleProvider.SetLocale</c>，
/// 它會改到整個行程的預設 UI 文化 —— 一位英文瀏覽器的使用者連線後，背景執行緒的預設文化就變成 en-US。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class LocalizationTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public LocalizationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void SupportedCultures_ShouldOnlyBeTraditionalChinese()
    {
        var options = factory.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        Assert.Equal(["zh-TW"], options.SupportedCultures!.Select(x => x.Name));
        Assert.Equal(["zh-TW"], options.SupportedUICultures!.Select(x => x.Name));
    }

    [Fact]
    public async Task EnglishBrowser_ShouldStillGetTraditionalChinese_AndNotChangeTheProcessCulture()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        var body = await client.GetStringAsync("/Auths/Login");

        Assert.Contains("<html lang=\"zh-TW\"", body, StringComparison.Ordinal);
        Assert.NotEqual("en-US", CultureInfo.DefaultThreadCurrentUICulture?.Name);
    }
}
