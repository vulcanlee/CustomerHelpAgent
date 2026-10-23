using System.Net;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 稽核紀錄的來源 IP（0.9.117 起）。Scoped，與 <see cref="AuditLogService"/> 在同一個 scope。
///
/// - SSR 頁面（登入、登出）與 Web API：取 <c>HttpContext.Connection.RemoteIpAddress</c>。
/// - Blazor 互動 circuit：circuit 內沒有可靠的 HttpContext，由 <c>ApplicationCircuitHandler</c>
///   在連線建立（與重連）時呼叫 <see cref="SetCircuitClientIp"/> 寫入，取用時優先。
/// - 排程作業：兩者皆無，回 null。
///
/// ⚠️ IP 會受 <c>UseForwardedHeaders</c> 影響：若部署在反向代理之後卻沒設定 <c>ForwardedHeaders</c>，
/// 這裡拿到的會是代理的 IP。
/// </summary>
public sealed class ClientIpProvider : IClientIpProvider
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private string? circuitClientIp;

    public ClientIpProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public void SetCircuitClientIp(IPAddress? address) => circuitClientIp = Format(address);

    public string? GetClientIp()
        => circuitClientIp ?? Format(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);

    /// <summary>IPv4-mapped IPv6（<c>::ffff:192.0.2.1</c>）轉回 IPv4，畫面與關鍵字搜尋才對得上一般人認得的寫法。</summary>
    internal static string? Format(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
