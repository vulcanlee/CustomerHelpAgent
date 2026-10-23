using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>ForwardedHeaders</c>：信任的反向代理。0.9.92 之前寫錯的項目會被<b>靜默略過</b>
/// （<c>UseConfiguredForwardedHeaders</c> 用 TryParse 過濾），結果是代理沒被信任、
/// 所有請求的來源 IP 都變成代理本身 —— 以 IP 分割的限流會把所有使用者算成同一個人。
/// 前綴長度超過位址位元數（例如 10.0.0.0/40）則會在建立管線時丟例外。
/// </summary>
public sealed class ForwardedHeadersSettingsValidator : IValidateOptions<ForwardedHeadersSettings>
{
    private const string Section = ForwardedHeadersSettings.SectionName;

    public ValidateOptionsResult Validate(string? name, ForwardedHeadersSettings options)
    {
        var errors = new OptionsErrors();

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"{Section}:KnownProxies", $"「{proxy}」不是有效的 IP 位址。");
            }
        }

        foreach (var network in options.KnownNetworks)
        {
            if (!IsValidNetwork(network))
            {
                errors.Add($"{Section}:KnownNetworks", $"「{network}」不是有效的網段：格式為「IP/前綴長度」，IPv4 的長度為 0～32、IPv6 為 0～128（例如 10.0.0.0/8）。");
            }
        }

        return errors.ToResult();
    }

    private static bool IsValidNetwork(string network)
    {
        var parts = network.Split('/', 2);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var prefix)
            || !int.TryParse(parts[1], out var length))
        {
            return false;
        }

        var maxLength = prefix.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return length >= 0 && length <= maxLength;
    }
}
