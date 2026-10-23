using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用的 <see cref="DemoControlService"/>。直接建構服務（<c>ShopOrderService</c>、<c>AfterSalesPolicyService</c>…）的 fixture
/// 用 <see cref="Off"/>：示範模式關閉，一律無故障、Live，也不會讀寫資料庫。
/// </summary>
internal static class TestDemoControl
{
    public static DemoControlService Off(IDbContextFactory<BackendDBContext> factory) => Create(factory, enabled: false);

    public static DemoControlService Create(IDbContextFactory<BackendDBContext> factory, bool enabled, TimeProvider? time = null)
        => new(
            factory,
            Options.Create(new DemoSettings { Enabled = enabled, DemoUserPassword = "demo1234" }),
            time ?? TimeProvider.System,
            NullLogger<DemoControlService>.Instance);
}
