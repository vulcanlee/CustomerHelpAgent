using System.Text.Json;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AiSettings 的 provider 解析與預設值測試。
/// 預設值有測試釘住，是為了避免有人悄悄改動而文件沒跟著更新。
/// </summary>
public sealed class AiSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GetProvider_ShouldDefaultToAzureOpenAI_WhenBlank(string? provider)
    {
        var settings = new AiSettings { Provider = provider! };

        Assert.Equal(AiProvider.AzureOpenAI, settings.GetProvider());
    }

    [Theory]
    [InlineData("AzureOpenAI", AiProvider.AzureOpenAI)]
    [InlineData("azureopenai", AiProvider.AzureOpenAI)]
    [InlineData("AZUREOPENAI", AiProvider.AzureOpenAI)]
    [InlineData("OpenAI", AiProvider.OpenAI)]
    [InlineData("openai", AiProvider.OpenAI)]
    public void GetProvider_ShouldParseCaseInsensitively(string provider, AiProvider expected)
    {
        var settings = new AiSettings { Provider = provider };

        Assert.Equal(expected, settings.GetProvider());
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    [InlineData("azure-openai")]
    public void GetProvider_ShouldThrow_WhenUnsupported(string provider)
    {
        var settings = new AiSettings { Provider = provider };

        var exception = Assert.Throws<InvalidOperationException>(() => settings.GetProvider());
        Assert.Contains(provider, exception.Message);
    }

    /// <summary>
    /// 預設值必須與 docs 的說明一致。
    ///
    /// ⚠️ 0.9.7 起 appsettings.json 只列出 Provider / Endpoint / ApiKey / Model /
    /// TimeoutSeconds 五個鍵，其餘只存在於此處與文件。改動任何預設值時，
    /// <b>務必同步</b> docs/operations/日誌與設定檔說明.md §4.8 的欄位表
    /// 與 docs/features/AI日誌分析.md 的預設值表 —— 那是使用者唯一查得到的地方。
    ///
    /// ApiKey 與 Model 預設留空是刻意的：這兩項就是功能的開關 —— 沒填等於關閉，
    /// 所以腳手架不帶金鑰也能直接跑起來，不需要另一個 Enabled 旗標。
    /// </summary>
    [Fact]
    public void Defaults_ShouldMatchDocumentedValues()
    {
        var settings = new AiSettings();

        Assert.Equal(nameof(AiProvider.AzureOpenAI), settings.Provider);
        Assert.Equal(string.Empty, settings.Endpoint);
        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal(string.Empty, settings.Model);
        Assert.Equal(100, settings.MaxEntries);
        Assert.Equal(10, settings.MaxFollowUpRounds);
        Assert.Equal(600, settings.TimeoutSeconds);
        // 預設不送 max_completion_tokens：這個額度同時涵蓋推論模型的思考 token，
        // 設太小會在產出任何可見文字之前就耗盡，拿到空回應還要付錢。
        Assert.Null(settings.MaxOutputTokens);
        // 預設不送 temperature：推論模型（o 系列、gpt-5 家族）只接受預設值，
        // 送任何數字都會被回 400 unsupported_value。不送就能相容所有模型。
        Assert.Null(settings.Temperature);
    }

    [Fact]
    public void SectionName_ShouldMatchAppSettingsKey()
    {
        Assert.Equal("AiSettings", AiSettings.SectionName);
    }

    /// <summary>
    /// appsettings.json 的 AiSettings 區段不得出現對不到 POCO 屬性的鍵。
    ///
    /// 為什麼需要這支：組態繫結會<b>靜默忽略</b>未知的鍵，所以移除或改名一個設定之後，
    /// JSON 裡殘留的死設定不會有任何地方報錯 —— 讀設定檔的人卻會以為它還有作用。
    ///
    /// 刻意<b>只做單向</b>（JSON 不得有未知鍵），不反過來要求每個屬性都必須出現在 JSON：
    /// SystemSettings.Upload 就是刻意不寫進範本、走程式預設的既有先例。
    /// </summary>
    [Fact]
    public void AppSettingsSection_ShouldOnlyContainKnownKeys()
    {
        var appSettingsPath = Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web", "appsettings.json");
        Assert.True(File.Exists(appSettingsPath), $"找不到 {appSettingsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        Assert.True(
            document.RootElement.TryGetProperty(AiSettings.SectionName, out var section),
            $"appsettings.json 缺少 {AiSettings.SectionName} 區段。");

        var knownKeys = typeof(AiSettings)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unknownKeys = section
            .EnumerateObject()
            .Select(property => property.Name)
            .Where(name => knownKeys.Contains(name) == false)
            .ToList();

        Assert.True(
            unknownKeys.Count == 0,
            $"appsettings.json 的 {AiSettings.SectionName} 有對不到 AiSettings 屬性的鍵："
            + string.Join("、", unknownKeys));
    }

    /// <summary>與 LoggingConventionTests 相同的作法，往上找到 src/CustomerHelpAgent。</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Web")))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }
}
