namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 取得目前操作的來源 IP，供稽核紀錄使用。
/// 實作在 Web 層（本專案不相依 ASP.NET Core）；排程作業等沒有 HTTP 請求的情境回 null。
/// </summary>
public interface IClientIpProvider
{
    string? GetClientIp();
}
