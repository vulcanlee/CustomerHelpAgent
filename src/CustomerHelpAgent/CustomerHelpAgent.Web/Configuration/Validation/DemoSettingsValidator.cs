using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary><c>DemoSettings</c>：開啟示範資料時，示範帳號密碼至少 8 個字元；關閉時不檢查。</summary>
public sealed class DemoSettingsValidator : IValidateOptions<DemoSettings>
{
    public const int MinPasswordLength = 8;

    private const string Section = DemoSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, DemoSettings options)
    {
        var errors = new OptionsErrors();

        if (options.Enabled && (options.DemoUserPassword?.Length ?? 0) < MinPasswordLength)
        {
            errors.Add($"{Section}:DemoUserPassword", $"開啟示範資料（Enabled=true）時至少要 {MinPasswordLength} 個字元；不需要示範帳號就設 Enabled=false。");
        }

        return errors.ToResult();
    }
}
