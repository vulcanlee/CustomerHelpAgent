using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>AiSettings</c>。沒填 <c>ApiKey</c> 代表不使用 AI（出貨預設），這時只檢查「值本身合不合理」；
/// 填了 <c>ApiKey</c> 就代表要用，缺少的設定一律擋下，而不是等使用者按下 AI 按鈕才失敗。
///
/// 0.9.92 之前的失敗方式：Provider 拼錯 → AI 功能靜默關閉；Endpoint 少了 https:// → 按下按鈕才丟 <c>UriFormatException</c>。
/// </summary>
public sealed class AiSettingsValidator : IValidateOptions<AiSettings>
{
    private const string Section = AiSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, AiSettings options)
    {
        var errors = new OptionsErrors();

        var providerIsValid = string.IsNullOrWhiteSpace(options.Provider)
            || Enum.TryParse<AiProvider>(options.Provider, ignoreCase: true, out _);
        if (!providerIsValid)
        {
            errors.Add($"{Section}:Provider", $"只接受 AzureOpenAI 或 OpenAI（留空等同 AzureOpenAI），目前是「{options.Provider}」。");
        }

        errors.RequireHttpUrlIfPresent($"{Section}:Endpoint", options.Endpoint);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.RequireText($"{Section}:Model", options.Model, "已填 ApiKey 代表要使用 AI，必須指定模型（Azure 為部署名稱）；不使用 AI 請把 ApiKey 留空。");

            if (providerIsValid && options.GetProvider() == AiProvider.AzureOpenAI && string.IsNullOrWhiteSpace(options.Endpoint))
            {
                errors.Add($"{Section}:Endpoint", "使用 AzureOpenAI 時不可留空，請填 Azure OpenAI 資源的網址；不使用 AI 請把 ApiKey 留空。");
            }
        }

        // HttpClient.Timeout 有上限，填太大會在建立 HttpClient 時才丟例外。
        errors.RequireRange($"{Section}:TimeoutSeconds", options.TimeoutSeconds, 1, 3600);
        errors.RequireRange($"{Section}:MaxEntries", options.MaxEntries, 1, 100_000);
        errors.RequireRange($"{Section}:MaxFollowUpRounds", options.MaxFollowUpRounds, 0, 1000);

        if (options.MaxOutputTokens is < 1)
        {
            errors.Add($"{Section}:MaxOutputTokens", $"有填時必須至少為 1（留空表示不限制），目前是 {options.MaxOutputTokens}。");
        }

        if (options.Temperature is < 0 or > 2)
        {
            errors.Add($"{Section}:Temperature", $"有填時必須介於 0 到 2 之間（留空表示使用模型預設），目前是 {options.Temperature}。");
        }

        return errors.ToResult();
    }
}
