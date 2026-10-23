using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>AiPricingSettings</c>：費率表。寫錯不會出錯，而是靜默算錯 ——
/// <c>UsdToTwd</c> ≤ 0 時每筆呼叫都變成「未計價」；負的費率會算出負金額；
/// 有長上下文費率卻沒有門檻（0），長上下文計價等於關閉。
/// </summary>
public sealed class AiPricingSettingsValidator : IValidateOptions<AiPricingSettings>
{
    private const string Section = AiPricingSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, AiPricingSettings options)
    {
        var errors = new OptionsErrors();

        if (options.UsdToTwd <= 0)
        {
            errors.Add($"{Section}:UsdToTwd", $"必須大於 0（美元對新台幣匯率），目前是 {options.UsdToTwd}。");
        }

        foreach (var (model, pricing) in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                errors.Add($"{Section}:Models", "模型名稱（鍵）不可空白。");
                continue;
            }

            var key = $"{Section}:Models:{model}";
            if (pricing.LongContextThresholdTokens < 0)
            {
                errors.Add($"{key}:LongContextThresholdTokens", $"不可小於 0，目前是 {pricing.LongContextThresholdTokens}。");
            }

            if (pricing.LongContextRates is not null && pricing.LongContextThresholdTokens <= 0)
            {
                errors.Add($"{key}:LongContextThresholdTokens", "有設定 LongContextRates 時必須大於 0，否則長上下文費率永遠不會套用。");
            }

            RequireNonNegativeRates(errors, $"{key}:Rates", pricing.Rates);
            if (pricing.LongContextRates is not null)
            {
                RequireNonNegativeRates(errors, $"{key}:LongContextRates", pricing.LongContextRates);
            }
        }

        return errors.ToResult();
    }

    private static void RequireNonNegativeRates(OptionsErrors errors, string key, AiModelRates rates)
    {
        (string Field, double? Value)[] fields =
        [
            (nameof(rates.TextInputPerMillion), rates.TextInputPerMillion),
            (nameof(rates.TextCachedInputPerMillion), rates.TextCachedInputPerMillion),
            (nameof(rates.TextOutputPerMillion), rates.TextOutputPerMillion),
            (nameof(rates.ImageInputPerMillion), rates.ImageInputPerMillion),
            (nameof(rates.ImageCachedInputPerMillion), rates.ImageCachedInputPerMillion),
            (nameof(rates.ImageOutputPerMillion), rates.ImageOutputPerMillion),
            (nameof(rates.AudioPerMinute), rates.AudioPerMinute),
            (nameof(rates.SpeechPerMillionCharacters), rates.SpeechPerMillionCharacters),
        ];

        foreach (var (field, value) in fields)
        {
            if (value is < 0)
            {
                errors.Add($"{key}:{field}", $"費率不可為負數，目前是 {value}。");
            }
        }
    }
}
