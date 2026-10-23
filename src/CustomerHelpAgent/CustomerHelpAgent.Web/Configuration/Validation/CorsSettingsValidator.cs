using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>Cors:AllowedOrigins</c>。瀏覽器送來的 Origin 只有 scheme + host（+ port），帶路徑或結尾斜線的來源永遠比對不到，
/// 而且不會有任何錯誤 —— 前端只會看到 CORS 被擋。<c>*</c> 等於開放所有來源，與「只開放指定前端」的用意相反。
/// </summary>
public sealed class CorsSettingsValidator : IValidateOptions<CorsSettings>
{
    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        var errors = new OptionsErrors();

        foreach (var origin in options.AllowedOrigins)
        {
            var key = $"{CorsSettings.SectionName}:AllowedOrigins";
            if (origin.Trim() == "*")
            {
                errors.Add(key, "不接受「*」：它會開放所有來源。請逐一列出前端網址，例如 https://app.example.com。");
            }
            else if (!OptionsErrors.IsHttpUrl(origin)
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.PathAndQuery != "/"
                || origin.TrimEnd().EndsWith('/'))
            {
                errors.Add(key, $"「{origin}」不是有效的來源：必須是 http/https 網址，只能有 scheme、主機與連接埠，不能有路徑或結尾的斜線（例如 https://app.example.com:8443）。");
            }
        }

        return errors.ToResult();
    }
}
