using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>RateLimit</c>：每分鐘允許的請求數。0 或負數會讓限流器在<b>每個 API 請求</b>時丟 <c>ArgumentException</c>。
/// </summary>
public sealed class RateLimitSettingsValidator : IValidateOptions<RateLimitSettings>
{
    public ValidateOptionsResult Validate(string? name, RateLimitSettings options)
    {
        var errors = new OptionsErrors();
        errors.RequireRange($"{RateLimitSettings.SectionName}:{nameof(options.ApiRequestsPerMinute)}", options.ApiRequestsPerMinute, 1, 100_000);
        errors.RequireRange($"{RateLimitSettings.SectionName}:{nameof(options.LoginRequestsPerMinute)}", options.LoginRequestsPerMinute, 1, 100_000);
        return errors.ToResult();
    }
}
