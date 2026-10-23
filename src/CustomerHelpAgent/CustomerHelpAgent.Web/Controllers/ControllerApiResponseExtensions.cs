using Microsoft.AspNetCore.Mvc;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// Controller 在自己的 catch 區塊回 500 時使用。
///
/// ⚠️ 例外細節是否回傳給呼叫端，一律交由 <see cref="ExceptionDetailPolicy"/> 決定 ——
/// 不要在這裡直接把 exception 塞進 <c>ApiResult</c>，Production 會外洩堆疊追蹤。
/// 例外本身請由呼叫端自行 <c>logger.LogError(ex, ...)</c> 留在伺服器日誌。
///
/// 回應一律帶 TraceId（0.9.78，LOG-10）＝錯誤追蹤碼，與日誌檔、系統例外紀錄相同，呼叫端回報時附上即可。
/// </summary>
public static class ControllerApiResponseExtensions
{
    public static ObjectResult ApiServerError<T>(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        var result = ShouldReturnDetails(controller)
            ? ApiResult<T>.ServerErrorResult(message, exception)
            : ApiResult<T>.ServerErrorResult(message);
        result.TraceId = controller.HttpContext.TraceIdentifier;

        return controller.StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    public static ObjectResult ApiServerError(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        var result = ShouldReturnDetails(controller)
            ? ApiResult.ServerErrorResult(message, exception)
            : ApiResult.ServerErrorResult(message);
        result.TraceId = controller.HttpContext.TraceIdentifier;

        return controller.StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    private static bool ShouldReturnDetails(ControllerBase controller)
    {
        return ExceptionDetailPolicy.ShouldReturnDetails(controller.HttpContext.RequestServices);
    }
}
