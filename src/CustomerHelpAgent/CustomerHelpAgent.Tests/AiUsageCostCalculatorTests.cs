using System.Text.Json;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// LLM 呼叫的費用計算。
///
/// 重點在四件事：
/// 1. <b>子集語意</b>：快取是輸入的折扣子集、推理已含在輸出內、圖片是同模態的子集。
///    每一項只能被算一次，重複計算就是假帳。
/// 2. <b>前綴比對必須限定日期／版本後綴</b>：純前綴會讓 gpt-4o-mini 命中 gpt-4o
///    （價差 30 倍）且完全靜默，錯誤金額還會被寫成永久快照。
/// 3. 算不出來要回 null（未定價），不是回 0 —— 兩者在畫面上意義完全不同。
/// 4. 四種計費單位（文字 token、圖片 token、每分鐘、每字元）可以並存相加。
/// </summary>
public class AiUsageCostCalculatorTests
{
    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("GPT-5.6-SOL")]
    [InlineData("  gpt-5.6-sol  ")]
    public void Resolve_ShouldMatchExactKeyIgnoringCaseAndWhitespace(string model)
    {
        var matched = AiUsageCostCalculator.Resolve(Settings(), model);

        Assert.NotNull(matched);
        Assert.Equal("gpt-5.6-sol", matched!.Value.Key);
    }

    [Theory]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o")]
    [InlineData("gpt-4o-2024-05-13", "gpt-4o")]
    [InlineData("gpt-3.5-turbo-0125", "gpt-3.5-turbo")]
    public void Resolve_ShouldAcceptDateOrVersionSuffix(string model, string expectedKey)
    {
        var matched = AiUsageCostCalculator.Resolve(Settings(), model);

        Assert.NotNull(matched);
        Assert.Equal(expectedKey, matched!.Value.Key);
    }

    [Theory]
    [InlineData("gpt-4o-mini")]      // 兄弟模型，價差約 30 倍
    [InlineData("gpt-4o-preview")]
    [InlineData("gpt-4o-latest")]
    [InlineData("gpt-4.1")]          // 點號後綴是不同模型家族，價差約 15 倍
    [InlineData("gpt-5.6-sol-mini")]
    public void Resolve_ShouldRejectNonVersionSuffix(string model)
    {
        // ⚠️ 這幾筆一旦被放行，帳就會默默算錯而且沒有任何跡象。
        Assert.Null(AiUsageCostCalculator.Resolve(Settings(), model));
    }

    [Fact]
    public void Resolve_ShouldPreferLongestPrefix()
    {
        var settings = Settings();
        settings.Models["gpt-4o-audio"] = Priced(textInput: 99);

        var matched = AiUsageCostCalculator.Resolve(settings, "gpt-4o-audio-2024-10-01");

        Assert.Equal("gpt-4o-audio", matched!.Value.Key);
    }

    [Fact]
    public void Resolve_ShouldBeDeterministicRegardlessOfConfigurationOrder()
    {
        // 同長度的候選鍵取字典序小的 —— 帳目不可以隨字典列舉順序而變。
        var forward = new AiPricingSettings { UsdToTwd = 1 };
        forward.Models["aa-bb"] = Priced(textInput: 1);
        forward.Models["aa-cc"] = Priced(textInput: 2);

        var reversed = new AiPricingSettings { UsdToTwd = 1 };
        reversed.Models["aa-cc"] = Priced(textInput: 2);
        reversed.Models["aa-bb"] = Priced(textInput: 1);

        // 兩個鍵都不是 "aa-bb-1" 的合法前綴以外的干擾，只有 aa-bb 會命中。
        Assert.Equal("aa-bb", AiUsageCostCalculator.Resolve(forward, "aa-bb-1")!.Value.Key);
        Assert.Equal("aa-bb", AiUsageCostCalculator.Resolve(reversed, "aa-bb-1")!.Value.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("totally-unknown-model")]
    public void Resolve_ShouldReturnNull_WhenNothingMatches(string model)
    {
        Assert.Null(AiUsageCostCalculator.Resolve(Settings(), model));
    }

    [Fact]
    public void Compute_ShouldNotChargeCachedTokensAtFullRate()
    {
        // 輸入 1000 其中快取 400 → 600 顆算原價、400 顆算快取價，不是 1000 顆算原價。
        var cost = Compute(Priced(textInput: 1_000_000, textCached: 100_000), new TokenUsageEntry
        {
            InputCount = 1000,
            CachedInputCount = 400,
        });

        Assert.Equal((600 * 1d) + (400 * 0.1d), cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldIgnoreReasoningCount()
    {
        // 推理已經含在 completion_tokens 裡，再算一次就是重複計費。
        var rates = Priced(textOutput: 1_000_000);

        var withReasoning = Compute(rates, new TokenUsageEntry { OutputCount = 500, ReasoningCount = 300 });
        var without = Compute(rates, new TokenUsageEntry { OutputCount = 500 });

        Assert.Equal(without!.CostUsd, withReasoning!.CostUsd, 9);
        Assert.Equal(500d, withReasoning.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldSplitInputIntoFourDisjointBuckets()
    {
        // 輸入 1000、快取 400、圖片輸入 300、其中圖片快取 100
        // → 文字原價 400、文字快取 300、圖片原價 200、圖片快取 100，四者相加正好是 1000。
        var rates = Priced(textInput: 1_000_000, textCached: 100_000, imageInput: 10_000_000, imageCached: 1_000_000);

        var cost = Compute(rates, new TokenUsageEntry
        {
            InputCount = 1000,
            CachedInputCount = 400,
            ImageInputCount = 300,
            ImageCachedInputCount = 100,
        });

        Assert.Equal(1000, 400 + 300 + 200 + 100);
        var expected = (400 * 1d) + (300 * 0.1d) + (200 * 10d) + (100 * 1d);
        Assert.Equal(expected, cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldTreatImageOutputAsSubsetOfOutput()
    {
        var rates = Priced(textOutput: 1_000_000, imageOutput: 5_000_000);

        var cost = Compute(rates, new TokenUsageEntry { OutputCount = 500, ImageOutputCount = 200 });

        Assert.Equal((300 * 1d) + (200 * 5d), cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldUseBaseRates_AtExactlyTheThreshold()
    {
        var cost = Compute(LongContextPricing(), new TokenUsageEntry { InputCount = 1000 });

        Assert.False(cost!.LongContext);
        Assert.Equal(1000 * 1d, cost.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldUseLongContextRates_AboveTheThreshold()
    {
        var cost = Compute(LongContextPricing(), new TokenUsageEntry { InputCount = 1001 });

        Assert.True(cost!.LongContext);
        Assert.Equal(1001 * 2d, cost.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldStayOnBaseRates_WhenModelHasNoLongContextTier()
    {
        var pricing = new AiModelPricing
        {
            LongContextThresholdTokens = 1000,
            Rates = new AiModelRates { TextInputPerMillion = 1_000_000 },
        };

        var cost = Compute(pricing, new TokenUsageEntry { InputCount = 10_000 });

        Assert.False(cost!.LongContext);
        Assert.Equal(10_000 * 1d, cost.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldBillAudioByMinute()
    {
        var cost = Compute(new AiModelPricing { Rates = new AiModelRates { AudioPerMinute = 0.006 } },
            new TokenUsageEntry { DurationSeconds = 90 });

        Assert.Equal(1.5 * 0.006, cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldBillSpeechByCharacter()
    {
        var cost = Compute(new AiModelPricing { Rates = new AiModelRates { SpeechPerMillionCharacters = 15 } },
            new TokenUsageEntry { CharacterCount = 1_000_000 });

        Assert.Equal(15d, cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldCombineBillingUnits()
    {
        // gpt-4o-transcribe-diarize 同時有每分鐘與文字費率，兩者相加。
        var pricing = new AiModelPricing
        {
            Rates = new AiModelRates
            {
                AudioPerMinute = 0.006,
                TextInputPerMillion = 2.5,
                TextOutputPerMillion = 10,
            },
        };

        var cost = Compute(pricing, new TokenUsageEntry
        {
            DurationSeconds = 60,
            InputCount = 1_000_000,
            OutputCount = 1_000_000,
        });

        Assert.Equal(0.006 + 2.5 + 10, cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldFallBackToInputRate_WhenCachedRateIsNotConfigured()
    {
        // 沒設快取費率代表「沒這個折扣」，不是免費 —— 寧可高估也不要靜默少算。
        var cost = Compute(Priced(textInput: 1_000_000), new TokenUsageEntry
        {
            InputCount = 1000,
            CachedInputCount = 400,
        });

        Assert.Equal(1000 * 1d, cost!.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldConvertToTwdAndRecordTheRate()
    {
        var settings = new AiPricingSettings { UsdToTwd = 31.5 };
        settings.Models["m"] = Priced(textInput: 1_000_000);

        var cost = AiUsageCostCalculator.Compute(settings, "m", settings.Models["m"],
            new TokenUsageEntry { InputCount = 100 });

        Assert.Equal(100d, cost!.CostUsd, 9);
        Assert.Equal(100d * 31.5, cost.CostTwd, 9);
        Assert.Equal(31.5, cost.ExchangeRate);
        Assert.Equal("m", cost.PriceKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Compute_ShouldReturnNull_WhenExchangeRateIsNotConfigured(double rate)
    {
        var settings = new AiPricingSettings { UsdToTwd = rate };
        settings.Models["m"] = Priced(textInput: 1_000_000);

        Assert.Null(AiUsageCostCalculator.Compute(settings, "m", settings.Models["m"],
            new TokenUsageEntry { InputCount = 100 }));
    }

    [Fact]
    public void Compute_ShouldReturnNull_WhenEveryRateIsEmpty()
    {
        // 設定檔被寫壞、或熱更新讀到半寫入的 JSON 時的防線：
        // 寧可顯示「未定價」，也不要把一整批呼叫以 0 元寫成永久快照。
        var pricing = new AiModelPricing { Rates = new AiModelRates { TextInputPerMillion = 0 } };

        Assert.Null(Compute(pricing, new TokenUsageEntry { InputCount = 1000 }));
    }

    [Fact]
    public void Compute_ShouldNotProduceNegativeCost_WhenUpstreamDataIsInconsistent()
    {
        // 上游偶爾會回出「快取比輸入還多」這種數字。
        var cost = Compute(Priced(textInput: 1_000_000, textCached: 100_000), new TokenUsageEntry
        {
            InputCount = 100,
            CachedInputCount = 500,
        });

        Assert.True(cost!.CostUsd >= 0);
        Assert.Equal(100 * 0.1d, cost.CostUsd, 9);
    }

    [Fact]
    public void Compute_ShouldReturnZero_WhenModelIsPricedButNothingWasUsed()
    {
        // ⚠️ 0 不是 null：有費率、只是這次用量是 0，畫面上不該顯示「未定價」。
        var cost = Compute(Priced(textInput: 1_000_000), new TokenUsageEntry());

        Assert.NotNull(cost);
        Assert.Equal(0d, cost!.CostUsd);
    }

    [Fact]
    public void BuildRateSnapshot_ShouldContainOnlyConfiguredNonZeroRates()
    {
        var snapshot = AiUsageCostCalculator.BuildRateSnapshot(new AiModelRates
        {
            TextInputPerMillion = 4,
            TextCachedInputPerMillion = 0.4,
            TextOutputPerMillion = 20,
            ImageInputPerMillion = 0,
        });

        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(4, root.GetProperty("TextInput").GetDouble());
        Assert.Equal(0.4, root.GetProperty("TextCachedInput").GetDouble());
        Assert.Equal(20, root.GetProperty("TextOutput").GetDouble());
        Assert.False(root.TryGetProperty("ImageInput", out _));
    }

    [Fact]
    public void AiModelRates_ShouldStillHaveEightBillingUnits()
    {
        // 守門：新增第九種計費單位時，Compute、BuildRateSnapshot、HasAnyRate 與本測試檔
        // 都必須一起更新，否則新單位會靜默不計費。
        Assert.Equal(8, typeof(AiModelRates).GetProperties().Length);
    }

    private static AiUsageCost? Compute(AiModelPricing pricing, TokenUsageEntry entry)
    {
        var settings = new AiPricingSettings { UsdToTwd = 1 };
        settings.Models["m"] = pricing;
        return AiUsageCostCalculator.Compute(settings, "m", pricing, entry);
    }

    private static AiModelPricing Priced(
        double? textInput = null,
        double? textCached = null,
        double? textOutput = null,
        double? imageInput = null,
        double? imageCached = null,
        double? imageOutput = null)
        => new()
        {
            Rates = new AiModelRates
            {
                TextInputPerMillion = textInput,
                TextCachedInputPerMillion = textCached,
                TextOutputPerMillion = textOutput,
                ImageInputPerMillion = imageInput,
                ImageCachedInputPerMillion = imageCached,
                ImageOutputPerMillion = imageOutput,
            },
        };

    /// <summary>門檻 1000 顆，超過就從 1 美金一顆變成 2 美金一顆。</summary>
    private static AiModelPricing LongContextPricing()
        => new()
        {
            LongContextThresholdTokens = 1000,
            Rates = new AiModelRates { TextInputPerMillion = 1_000_000 },
            LongContextRates = new AiModelRates { TextInputPerMillion = 2_000_000 },
        };

    private static AiPricingSettings Settings()
    {
        var settings = new AiPricingSettings { UsdToTwd = 31.5 };
        settings.Models["gpt-5.6-sol"] = Priced(textInput: 4, textCached: 0.4, textOutput: 20);
        settings.Models["gpt-4o"] = Priced(textInput: 2.5, textCached: 1.25, textOutput: 10);
        settings.Models["gpt-3.5-turbo"] = Priced(textInput: 0.5, textOutput: 1.5);
        return settings;
    }
}
