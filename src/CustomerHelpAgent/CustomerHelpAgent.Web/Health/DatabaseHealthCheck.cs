using Microsoft.Extensions.Diagnostics.HealthChecks;
using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Web.Health;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly BackendDBContext context;
    private readonly ILogger<DatabaseHealthCheck> logger;

    public DatabaseHealthCheck(BackendDBContext context, ILogger<DatabaseHealthCheck> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        if (await context.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Healthy("Database connection is available.");
        }

        // /health/ready 被負載平衡器輪詢；連不上資料庫是維運人員要看一眼的事（§3.1 Warning）。
        logger.LogWarning("Database readiness check failed: the database cannot be reached.");
        return HealthCheckResult.Unhealthy("Database connection is unavailable.");
    }
}
