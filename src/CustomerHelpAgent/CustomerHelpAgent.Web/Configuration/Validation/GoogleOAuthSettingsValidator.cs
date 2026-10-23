using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>GoogleOAuthSettings</c>。0.9.92 之前 <c>Enabled=true</c> 卻沒填 ClientId／ClientSecret 時，
/// Google 登入只是靜默地沒有註冊，登入頁少了 Google 按鈕，沒有任何訊息說明原因。
/// 不使用 Google 登入請設 <c>Enabled=false</c>。（<c>DefaultRoleName</c> 是否對得到角色要查資料庫，不在這裡檢查。）
/// </summary>
public sealed class GoogleOAuthSettingsValidator : IValidateOptions<GoogleOAuthSettings>
{
    private const string Section = GoogleOAuthSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, GoogleOAuthSettings options)
    {
        var errors = new OptionsErrors();
        if (!options.Enabled)
        {
            return errors.ToResult();
        }

        const string hint = "Enabled=true 代表要啟用 Google 登入；不使用請設 Enabled=false。";
        errors.RequireText($"{Section}:ClientId", options.ClientId, hint);
        errors.RequireText($"{Section}:ClientSecret", options.ClientSecret, hint);
        errors.RequireText($"{Section}:DefaultRoleName", options.DefaultRoleName, "Google 首次登入自動建立的帳號會套用這個角色。");
        return errors.ToResult();
    }
}
