using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// <see cref="IAiUsageCostCalculator"/> 的實作。
///
/// 比對與計算都拆成 static 純函式（<see cref="Resolve"/> / <see cref="Compute"/>），
/// 測試可以直接呼叫，不必架 DI 與資料庫。
/// </summary>
public class AiUsageCostCalculator : IAiUsageCostCalculator
{
    private const double Million = 1_000_000d;

    /// <summary>
    /// 前綴比對允許的後綴：連字號接數字，其後只有數字與連字號。
    ///
    /// ⚠️ 這個限制是刻意的，不能放寬成「任何後綴」。純前綴比對會讓 gpt-4o-mini 命中
    /// gpt-4o（價差約 30 倍）、gpt-4.1 命中 gpt-4（價差約 15 倍），而且完全靜默、
    /// 錯誤金額還會被寫成永久快照。寧可顯示「未定價」讓人看得到沒算到。
    ///
    /// 命中：-2024-08-06、-0125、-1106。不命中：-mini、-preview、-latest、.1。
    /// </summary>
    private static readonly Regex VersionSuffixPattern =
        new(@"^-\d[\d-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IOptionsMonitor<AiPricingSettings> pricingOptions;

    public AiUsageCostCalculator(IOptionsMonitor<AiPricingSettings> pricingOptions)
    {
        this.pricingOptions = pricingOptions;
    }

    public AiUsageCost? Calculate(TokenUsageEntry entry)
    {
        // ⚠️ CurrentValue 只讀一次並在整個計算中沿用同一份。Blazor Server 的 DI scope
        // 是 SignalR circuit，可以活好幾小時，設定隨時可能熱更新；中途重讀會產生
        // 單價與匯率對不起來的快照。
        var settings = pricingOptions.CurrentValue;
        if (settings is null)
        {
            return null;
        }

        var matched = Resolve(settings, entry.Model);
        if (matched is null)
        {
            return null;
        }

        return Compute(settings, matched.Value.Key, matched.Value.Value, entry);
    }

    /// <summary>
    /// 找出模型對應的費率設定：完全比對優先，否則取後綴合法的最長前綴，都沒有就回 null。
    /// 長度相同時取字典序小的，確保結果不受設定列舉順序影響（帳目要可重現）。
    /// </summary>
    public static KeyValuePair<string, AiModelPricing>? Resolve(AiPricingSettings settings, string? model)
    {
        if (settings?.Models is null || settings.Models.Count == 0)
        {
            return null;
        }

        var name = model?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        KeyValuePair<string, AiModelPricing>? best = null;

        foreach (var pair in settings.Models)
        {
            var key = pair.Key?.Trim();
            if (string.IsNullOrEmpty(key) || pair.Value is null)
            {
                continue;
            }

            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return new KeyValuePair<string, AiModelPricing>(key, pair.Value);
            }

            if (IsVersionSuffixMatch(name, key) == false)
            {
                continue;
            }

            if (best is null
                || key.Length > best.Value.Key.Length
                || (key.Length == best.Value.Key.Length && string.CompareOrdinal(key, best.Value.Key) < 0))
            {
                best = new KeyValuePair<string, AiModelPricing>(key, pair.Value);
            }
        }

        return best;
    }

    /// <summary>
    /// 依費率設定算出費用。
    ///
    /// ⚠️ 子集語意：快取是輸入的折扣子集、推理已含在輸出內、圖片是同模態的子集。
    /// 每一項都只能被算一次，重複計算就是假帳。推理數完全不參與計算。
    /// </summary>
    public static AiUsageCost? Compute(
        AiPricingSettings settings,
        string priceKey,
        AiModelPricing pricing,
        TokenUsageEntry entry)
    {
        // 匯率沒設定就整組視為未啟用，比照 AiSettings.ApiKey 留空即停用的既有作法。
        if (settings.UsdToTwd <= 0 || pricing is null)
        {
            return null;
        }

        // 先夾正再拆解：上游偶爾會回出「快取比輸入還多」這種數字，硬算會產生負數金額。
        var input = Math.Max(0, entry.InputCount ?? 0);
        var cached = Math.Clamp(entry.CachedInputCount ?? 0, 0, input);
        var imageIn = Math.Clamp(entry.ImageInputCount ?? 0, 0, input);
        var imageCachedIn = Math.Clamp(entry.ImageCachedInputCount ?? 0, 0, Math.Min(imageIn, cached));

        var imageFreshIn = imageIn - imageCachedIn;
        var textCachedIn = cached - imageCachedIn;
        var textFreshIn = Math.Max(0, input - cached - imageFreshIn);

        var output = Math.Max(0, entry.OutputCount ?? 0);
        var imageOut = Math.Clamp(entry.ImageOutputCount ?? 0, 0, output);
        var textOut = output - imageOut;

        var seconds = Math.Max(0, entry.DurationSeconds ?? 0);
        var characters = Math.Max(0, entry.CharacterCount ?? 0);

        // 以「扣快取之前」的輸入數判斷分級，嚴格大於門檻才觸發。
        var longContext = pricing.LongContextRates is not null
            && pricing.LongContextThresholdTokens > 0
            && input > pricing.LongContextThresholdTokens;

        var rates = longContext ? pricing.LongContextRates! : pricing.Rates;

        // 整組費率是空的就當作未設定。這是設定檔被寫壞（或熱更新讀到半寫入的 JSON）時的
        // 防線 —— 寧可顯示「未定價」，也不要把一整批呼叫以 0 元寫成永久快照。
        if (rates is null || HasAnyRate(rates) == false)
        {
            return null;
        }

        // 快取費率沒設定時退回原價，不是免費：寧可高估也不要靜默少算。
        var textCachedRate = rates.TextCachedInputPerMillion ?? rates.TextInputPerMillion ?? 0;
        var imageCachedRate = rates.ImageCachedInputPerMillion ?? rates.ImageInputPerMillion ?? 0;

        var usd = (textFreshIn / Million * (rates.TextInputPerMillion ?? 0))
            + (textCachedIn / Million * textCachedRate)
            + (textOut / Million * (rates.TextOutputPerMillion ?? 0))
            + (imageFreshIn / Million * (rates.ImageInputPerMillion ?? 0))
            + (imageCachedIn / Million * imageCachedRate)
            + (imageOut / Million * (rates.ImageOutputPerMillion ?? 0))
            + (seconds / 60d * (rates.AudioPerMinute ?? 0))
            + (characters / Million * (rates.SpeechPerMillionCharacters ?? 0));

        // 全程不四捨五入，只在顯示與匯出時格式化。
        return new AiUsageCost
        {
            CostUsd = usd,
            CostTwd = usd * settings.UsdToTwd,
            ExchangeRate = settings.UsdToTwd,
            PriceKey = priceKey,
            LongContext = longContext,
            RateSnapshot = BuildRateSnapshot(rates),
        };
    }

    /// <summary>生效費率組的精簡 JSON，只序列化有設定且非 0 的項目。</summary>
    public static string BuildRateSnapshot(AiModelRates rates)
    {
        var values = new Dictionary<string, double>();

        Add("TextInput", rates.TextInputPerMillion);
        Add("TextCachedInput", rates.TextCachedInputPerMillion);
        Add("TextOutput", rates.TextOutputPerMillion);
        Add("ImageInput", rates.ImageInputPerMillion);
        Add("ImageCachedInput", rates.ImageCachedInputPerMillion);
        Add("ImageOutput", rates.ImageOutputPerMillion);
        Add("AudioPerMinute", rates.AudioPerMinute);
        Add("SpeechPerMillionChars", rates.SpeechPerMillionCharacters);

        return JsonSerializer.Serialize(values);

        void Add(string name, double? value)
        {
            if (value.HasValue && value.Value != 0)
            {
                values[name] = value.Value;
            }
        }
    }

    private static bool HasAnyRate(AiModelRates rates)
        => IsSet(rates.TextInputPerMillion)
        || IsSet(rates.TextCachedInputPerMillion)
        || IsSet(rates.TextOutputPerMillion)
        || IsSet(rates.ImageInputPerMillion)
        || IsSet(rates.ImageCachedInputPerMillion)
        || IsSet(rates.ImageOutputPerMillion)
        || IsSet(rates.AudioPerMinute)
        || IsSet(rates.SpeechPerMillionCharacters);

    private static bool IsSet(double? value) => value.HasValue && value.Value != 0;

    private static bool IsVersionSuffixMatch(string model, string key)
    {
        if (model.Length <= key.Length)
        {
            return false;
        }

        if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        return VersionSuffixPattern.IsMatch(model[key.Length..]);
    }
}
