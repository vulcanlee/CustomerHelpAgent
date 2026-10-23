using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>CacheSettings</c>。Provider 拼錯與 Redis 缺連線字串，<c>AddConfiguredCache</c> 在註冊時就會丟例外（保留）；
/// 這裡補上到期分鐘數，並讓錯誤訊息與其他設定一致。
/// </summary>
public sealed class CacheSettingsValidator : IValidateOptions<CacheSettings>
{
    private const string Section = CacheSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, CacheSettings options)
    {
        var errors = new OptionsErrors();

        var providerIsValid = string.IsNullOrWhiteSpace(options.Provider)
            || Enum.TryParse<CacheProvider>(options.Provider, ignoreCase: true, out _);
        if (!providerIsValid)
        {
            errors.Add($"{Section}:Provider", $"只接受 Memory 或 Redis（留空等同 Memory），目前是「{options.Provider}」。");
        }
        else if (options.GetProvider() == CacheProvider.Redis && string.IsNullOrWhiteSpace(options.RedisConnection))
        {
            errors.Add($"{Section}:RedisConnection", "使用 Redis 時不可留空。");
        }

        errors.RequireRange($"{Section}:DefaultExpirationMinutes", options.DefaultExpirationMinutes, 1, 10_080);
        return errors.ToResult();
    }
}
