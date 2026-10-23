using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 「API 回應是否夾帶例外細節（訊息、堆疊追蹤）」的**單一判斷來源**。
///
/// `Security:ReturnExceptionDetails` 未設定（null）時依環境決定：只有 Development 會回傳。
///
/// 沿革：0.4.34 之前只有 <see cref="Filters.ApiExceptionFilterAttribute"/> 做這個判斷，
/// 但 Controller 的 catch 區塊呼叫的 <c>ApiServerError(...)</c> 直接把
/// <c>exception.ToString()</c> 與堆疊追蹤塞進回應，完全繞過這個開關 ——
/// Production 明明設了 <c>ReturnExceptionDetails: false</c> 仍會外洩。
/// 兩條路徑現在都走這裡，不要再各自判斷。
/// </summary>
internal static class ExceptionDetailPolicy
{
    public static bool ShouldReturnDetails(SecuritySettings settings, IWebHostEnvironment environment)
    {
        return settings.ReturnExceptionDetails ?? environment.IsDevelopment();
    }

    public static bool ShouldReturnDetails(IServiceProvider services)
    {
        var settings = services.GetRequiredService<IOptions<SecuritySettings>>().Value;
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        return ShouldReturnDetails(settings, environment);
    }
}
