using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>BootstrapSettings</c>：內建管理員帳號。
///
/// 空白密碼會被雜湊成管理員密碼（每次啟動都重新套用），空白帳號會建出帳號為空的管理員 ——
/// 兩者都不會有任何錯誤訊息。Production 另由 <c>StartupSafetyValidator</c> 擋下範本預設密碼。
/// </summary>
public sealed class BootstrapSettingsValidator : IValidateOptions<BootstrapSettings>
{
    public ValidateOptionsResult Validate(string? name, BootstrapSettings options)
    {
        var errors = new OptionsErrors();
        errors.RequireText("BootstrapSettings:SupportAccount", options.SupportAccount, "它是內建管理員的登入帳號。");
        errors.RequireText(
            "BootstrapSettings:SupportPassword",
            options.SupportPassword,
            "留空會讓內建管理員變成空密碼；正式環境請用環境變數 BootstrapSettings__SupportPassword 提供。");
        return errors.ToResult();
    }
}
