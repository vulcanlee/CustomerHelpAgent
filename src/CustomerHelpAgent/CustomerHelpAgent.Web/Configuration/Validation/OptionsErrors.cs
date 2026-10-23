using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// 設定驗證器共用的錯誤收集器。一次收集**全部**錯誤再回報 —— 部署時改一條、重啟、再看到下一條，非常折磨人。
/// 每則訊息以設定鍵開頭（例如 <c>RateLimit:ApiRequestsPerMinute</c>），讀的人才知道要去改哪一行。
/// </summary>
internal sealed class OptionsErrors
{
    private readonly List<string> errors = [];

    public void Add(string key, string problem) => errors.Add($"{key} {problem}");

    public ValidateOptionsResult ToResult()
        => errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

    public void RequireText(string key, string? value, string hint)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(key, $"不可留空。{hint}");
        }
    }

    public void RequireRange(string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            Add(key, $"必須介於 {min} 到 {max} 之間，目前是 {value}。");
        }
    }

    public void RequireHttpUrlIfPresent(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsHttpUrl(value))
        {
            Add(key, $"必須是以 http:// 或 https:// 開頭的完整網址，目前是「{value}」。");
        }
    }

    public static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
