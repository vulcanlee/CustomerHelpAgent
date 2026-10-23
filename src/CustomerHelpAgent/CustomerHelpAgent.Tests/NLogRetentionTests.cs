using System.Xml.Linq;
using CustomerHelpAgent.Web.DataRetention;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 「資料保留與清理」頁顯示的日誌保留天數（<see cref="NLogRetention.MaxArchiveDays"/>）是抄自 nlog.config 的一份，
/// 改了 nlog.config 的 <c>maxArchiveDays</c> 卻沒改常數，畫面就會說錯（0.9.133 起）。
/// </summary>
public sealed class NLogRetentionTests
{
    [Fact]
    public void MaxArchiveDays_ShouldMatchNLogConfig()
    {
        var config = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "nlog.config"));
        var values = config.Descendants()
            .Select(x => x.Attribute("maxArchiveDays")?.Value)
            .OfType<string>()
            .Distinct()
            .ToList();

        Assert.Equal([NLogRetention.MaxArchiveDays.ToString()], values);
    }
}
