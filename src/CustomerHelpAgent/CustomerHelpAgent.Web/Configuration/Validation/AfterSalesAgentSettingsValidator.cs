using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary><c>AfterSalesAgentSettings</c>（1.0.4 起）：步數、連續失敗次數、工具逾時、API 形式、推理強度、溫度與看圖模型名稱都要在合理範圍內，寫錯就拒絕啟動。</summary>
public sealed class AfterSalesAgentSettingsValidator : IValidateOptions<AfterSalesAgentSettings>
{
    private const string Section = AfterSalesAgentSettings.SectionName;

    private static readonly string[] ReasoningEfforts = ["none", "minimal", "low", "medium", "high"];

    public ValidateOptionsResult Validate(string? name, AfterSalesAgentSettings options)
    {
        var errors = new OptionsErrors();
        errors.RequireRange($"{Section}:{nameof(options.MaxSteps)}", options.MaxSteps, 1, 20);
        errors.RequireRange($"{Section}:{nameof(options.MaxConsecutiveToolFailures)}", options.MaxConsecutiveToolFailures, 1, 5);
        errors.RequireRange($"{Section}:{nameof(options.ToolTimeoutSeconds)}", options.ToolTimeoutSeconds, 1, 120);

        // 只比對名稱：Enum.TryParse 也會接受 "1" 這種數字字串。
        if (!Enum.GetNames<AgentApiStyle>().Contains(options.ApiStyle, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"{Section}:{nameof(options.ApiStyle)}", $"只接受 Responses 或 ChatCompletions，目前是「{options.ApiStyle}」。");
        }

        if (!string.IsNullOrEmpty(options.ReasoningEffort) && !ReasoningEfforts.Contains(options.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"{Section}:{nameof(options.ReasoningEffort)}",
                $"只接受空字串（不送）、{string.Join("、", ReasoningEfforts)}，目前是「{options.ReasoningEffort}」。");
        }

        if (options.Temperature is < 0 or > 2)
        {
            errors.Add($"{Section}:{nameof(options.Temperature)}", $"有填時必須介於 0 到 2 之間（留空表示不送），目前是 {options.Temperature}。");
        }

        // 部署名稱前後有空白多半是手誤（送出去會 404）；空字串＝沿用 AiSettings:Model。
        if (!string.IsNullOrEmpty(options.VisionModel) && options.VisionModel != options.VisionModel.Trim())
        {
            errors.Add($"{Section}:{nameof(options.VisionModel)}", $"部署名稱前後不可有空白（留空字串表示沿用 AiSettings:Model），目前是「{options.VisionModel}」。");
        }

        return errors.ToResult();
    }
}
