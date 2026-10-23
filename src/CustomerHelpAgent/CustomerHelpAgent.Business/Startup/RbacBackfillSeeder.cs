using Microsoft.Extensions.Logging;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// RBAC 回填（把權限資料補進 Permission／RolePermissionMap／UserRole／UserTeam；冪等）。
///
/// 失敗不中止啟動（與 0.9.90 之前相同）：回填只會「補」，既有權限資料仍可用；
/// 讓整個系統因為回填失敗而起不來，代價比少補幾列大得多。
/// </summary>
public sealed class RbacBackfillSeeder : IDatabaseSeeder
{
    private readonly IRbacBackfillService rbacBackfillService;
    private readonly ILogger<RbacBackfillSeeder> logger;

    public RbacBackfillSeeder(IRbacBackfillService rbacBackfillService, ILogger<RbacBackfillSeeder> logger)
    {
        this.rbacBackfillService = rbacBackfillService;
        this.logger = logger;
    }

    public int Order => 30;

    public string Name => "RBAC 回填";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await rbacBackfillService.RunAsync();
        }
        catch (Exception ex)
        {
            // 殘留的追蹤狀態由初始化器在本 seeder 結束後清空。
            logger.LogError(ex, "RBAC backfill failed at startup.");
        }
    }
}
