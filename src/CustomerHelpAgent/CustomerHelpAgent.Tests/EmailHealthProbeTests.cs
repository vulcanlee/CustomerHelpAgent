using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Email;

namespace CustomerHelpAgent.Tests;

public sealed class EmailHealthProbeTests
{
    /// <summary>
    /// 連不上的 SMTP 必須回報失敗而不是拋例外，而且要在上限內結束 ——
    /// 否則 SMTP 一掛，整個系統健康頁就跟著出不來。
    /// </summary>
    [Fact]
    public async Task ProbeAsync_WhenServerUnreachable_ShouldFailWithoutThrowing()
    {
        var probe = new EmailHealthProbe(
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings
            {
                Provider = "Smtp",
                Host = "127.0.0.1",
                Port = 1,
                Security = "None",
                FromAddress = "noreply@example.com",
            }),
            NullLogger<EmailHealthProbe>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var result = await probe.ProbeAsync();
        stopwatch.Stop();

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(EmailHealthProbe.TimeoutSeconds + 3),
            $"探測花了 {stopwatch.Elapsed.TotalSeconds:F1} 秒，超過上限。");
    }
}
