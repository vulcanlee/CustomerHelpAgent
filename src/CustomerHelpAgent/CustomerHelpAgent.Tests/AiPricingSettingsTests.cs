using System.Text.Json;
using Microsoft.Extensions.Configuration;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// appsettings.json 的 AiPricingSettings 區段守門。
///
/// 比照 AiSettingsTests 的精神，但檢查必須<b>遞迴</b>：這個區段有三層
/// （區段 → 每個模型 → Rates／LongContextRates），而 AiSettingsTests 那支只掃第一層。
///
/// ⚠️ 為什麼這裡比 AiSettings 更需要守門：組態繫結會靜默忽略未知的鍵，
/// 所以<b>打錯一個費率鍵名，那個計費單位就默默變成 0，帳直接少算而且沒有任何跡象</b>。
/// 範本的費率數字也用測試釘住 —— 它們是程式與文件之間唯一的真相來源。
/// </summary>
public sealed class AiPricingSettingsTests
{
    [Fact]
    public void SectionName_ShouldMatchAppSettingsKey()
    {
        Assert.Equal("AiPricingSettings", AiPricingSettings.SectionName);
    }

    [Fact]
    public void AppSettingsSection_ShouldOnlyContainKnownKeys()
    {
        var section = LoadSection();
        var unknown = new List<string>();

        CollectUnknownKeys(section, typeof(AiPricingSettings), AiPricingSettings.SectionName, unknown, isModelMap: false);

        Assert.True(
            unknown.Count == 0,
            "appsettings.json 的 AiPricingSettings 有對不到 POCO 屬性的鍵："
            + string.Join("、", unknown)
            + "。打錯費率鍵名會讓該計費單位靜默變成 0。");
    }

    [Fact]
    public void AppSettings_ShouldConfigureAPositiveExchangeRate()
    {
        // 匯率 <= 0 會讓所有呼叫變成「未定價」，範本不該是那個狀態。
        Assert.True(Bind().UsdToTwd > 0);
    }

    [Fact]
    public void AppSettings_ModelKeys_ShouldBeTrimmedLowercaseAndUnique()
    {
        var section = LoadSection();
        var keys = section.GetProperty(nameof(AiPricingSettings.Models))
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.Equal(key.Trim(), key);
            // 全小寫讓讀設定的人可以信任「完全比對」那條路徑。
            Assert.Equal(key.ToLowerInvariant(), key);
        }
    }

    /// <summary>
    /// 範本費率必須與供應商定價頁一致（擷取來源與日期見 docs/operations/日誌與設定檔說明.md）。
    /// 這支測試是刻意的重複，用來擋住「有人改了數字但沒改文件」。
    /// </summary>
    [Theory]
    [InlineData("gpt-6-astra", 10.0, 1.0, 50.0)]
    [InlineData("gpt-6.1-sol", 2.0, 0.1, 10.0)]
    [InlineData("gpt-6-sol", 2.0, 0.2, 10.0)]
    [InlineData("gpt-6-luna", 0.1, 0.01, 0.5)]
    [InlineData("gpt-5.6-sol", 4.0, 0.4, 20.0)]
    [InlineData("gpt-5.6-terra", 2.0, 0.2, 12.0)]
    [InlineData("gpt-5.6-luna", 0.2, 0.02, 1.2)]
    public void AppSettings_ShouldPinFlagshipTextRates(
        string key, double input, double cachedInput, double output)
    {
        var rates = Bind().Models[key].Rates;

        Assert.Equal(input, rates.TextInputPerMillion);
        Assert.Equal(cachedInput, rates.TextCachedInputPerMillion);
        Assert.Equal(output, rates.TextOutputPerMillion);
    }

    [Theory]
    [InlineData("gpt-6-astra", 20.0, 2.0, 75.0)]
    [InlineData("gpt-6.1-sol", 4.0, 0.2, 15.0)]
    [InlineData("gpt-6-sol", 4.0, 0.4, 15.0)]
    [InlineData("gpt-6-luna", 0.2, 0.02, 0.75)]
    [InlineData("gpt-5.6-sol", 8.0, 0.8, 30.0)]
    [InlineData("gpt-5.6-terra", 4.0, 0.4, 18.0)]
    [InlineData("gpt-5.6-luna", 0.4, 0.04, 1.8)]
    public void AppSettings_ShouldPinLongContextRates(
        string key, double input, double cachedInput, double output)
    {
        var pricing = Bind().Models[key];

        // 官方定價在輸入 token 嚴格超過 272000 時切換長脈絡費率。
        Assert.Equal(272000, pricing.LongContextThresholdTokens);
        Assert.NotNull(pricing.LongContextRates);
        Assert.Equal(input, pricing.LongContextRates!.TextInputPerMillion);
        Assert.Equal(cachedInput, pricing.LongContextRates.TextCachedInputPerMillion);
        Assert.Equal(output, pricing.LongContextRates.TextOutputPerMillion);
    }

    [Fact]
    public void AppSettings_ShouldPinNonTextBillingUnits()
    {
        var models = Bind().Models;

        Assert.Equal(8.0, models["gpt-image-2.5-flare"].Rates.ImageInputPerMillion);
        Assert.Equal(2.0, models["gpt-image-2.5-flare"].Rates.ImageCachedInputPerMillion);
        Assert.Equal(30.0, models["gpt-image-2.5-flare"].Rates.ImageOutputPerMillion);

        Assert.Equal(8.0, models["gpt-image-2.5-sunburst"].Rates.ImageInputPerMillion);
        Assert.Equal(2.0, models["gpt-image-2.5-sunburst"].Rates.ImageCachedInputPerMillion);
        Assert.Equal(30.0, models["gpt-image-2.5-sunburst"].Rates.ImageOutputPerMillion);

        Assert.Equal(0.0045, models["gpt-transcribe"].Rates.AudioPerMinute);
        Assert.Equal(0.006, models["gpt-4o-transcribe-diarize"].Rates.AudioPerMinute);
        Assert.Equal(15.0, models["tts-1"].Rates.SpeechPerMillionCharacters);
        Assert.Equal(0.13, models["text-embedding-3-large"].Rates.TextInputPerMillion);

        // embedding 沒有輸出費率，留 null 而不是 0 —— 兩者在文件上意義不同。
        Assert.Null(models["text-embedding-3-large"].Rates.TextOutputPerMillion);
    }

    /// <summary>
    /// 用**真實的 appsettings.json** 走一次完整的比對路徑。
    ///
    /// `gpt-5.6-sol-2026-07-09` 是實際被記錄過的模型名稱（Azure OpenAI 回傳帶日期後綴），
    /// 它必須能前綴命中 `gpt-5.6-sol`；而 `gpt-5.6-sol-mini` 這種兄弟模型必須被擋下來。
    /// 這兩條合起來才是「受限前綴比對」在正式設定下真正的行為。
    /// </summary>
    [Theory]
    [InlineData("gpt-5.6-sol-2026-07-09", "gpt-5.6-sol")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-4o-transcribe-diarize", "gpt-4o-transcribe-diarize")]
    [InlineData("gpt-5.6-sol-mini", null)]
    [InlineData("my-azure-deployment", null)]
    public void RealAppSettings_ShouldResolveModelsAsDocumented(string model, string? expectedKey)
    {
        var matched = AiUsageCostCalculator.Resolve(Bind(), model);

        Assert.Equal(expectedKey, matched?.Key);
    }

    /// <summary>
    /// 遞迴比對 JSON 的鍵與 POCO 的屬性。
    /// <paramref name="isModelMap"/> 為 true 時代表這一層是模型字典，鍵是使用者自訂的模型名稱，
    /// 不做名稱檢查，只往下檢查它的值。
    /// </summary>
    private static void CollectUnknownKeys(
        JsonElement element, Type type, string path, List<string> unknown, bool isModelMap)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (isModelMap)
        {
            foreach (var model in element.EnumerateObject())
            {
                CollectUnknownKeys(model.Value, typeof(AiModelPricing), $"{path}.{model.Name}", unknown, isModelMap: false);
            }

            return;
        }

        var properties = type.GetProperties().ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (properties.TryGetValue(property.Name, out var info) == false)
            {
                unknown.Add($"{path}.{property.Name}");
                continue;
            }

            if (info.PropertyType == typeof(AiModelRates))
            {
                CollectUnknownKeys(property.Value, typeof(AiModelRates), $"{path}.{property.Name}", unknown, isModelMap: false);
            }
            else if (info.Name == nameof(AiPricingSettings.Models))
            {
                CollectUnknownKeys(property.Value, typeof(AiModelPricing), $"{path}.{property.Name}", unknown, isModelMap: true);
            }
        }
    }

    private static JsonElement LoadSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));
        Assert.True(
            document.RootElement.TryGetProperty(AiPricingSettings.SectionName, out var section),
            $"appsettings.json 缺少 {AiPricingSettings.SectionName} 區段。");

        // JsonDocument 會被 Dispose，複製一份出來。
        return JsonDocument.Parse(section.GetRawText()).RootElement;
    }

    private static AiPricingSettings Bind()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath())
            .Build();

        var settings = new AiPricingSettings();
        configuration.GetSection(AiPricingSettings.SectionName).Bind(settings);
        return settings;
    }

    private static string AppSettingsPath()
    {
        var path = Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web", "appsettings.json");
        Assert.True(File.Exists(path), $"找不到 {path}");
        return path;
    }

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
