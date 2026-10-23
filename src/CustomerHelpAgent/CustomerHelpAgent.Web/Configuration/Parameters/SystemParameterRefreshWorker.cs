using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Configuration.Parameters;

/// <summary>
/// 每分鐘從資料庫重新套用系統參數（0.9.98 起），讓另一個行程（IIS 重疊回收）或另一台主機的修改在一分鐘內生效。
///
/// ⚠️ 刻意不是 <c>IScheduledJob</c>：排程作業跨行程只跑一次，這裡則是<b>每個行程都要</b>刷新自己的設定；
/// 也不該每分鐘寫一筆執行紀錄。速查表「排程作業」對它是明文的例外。
/// </summary>
public sealed class SystemParameterRefreshWorker : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly SystemParameterRuntime runtime;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemParameterRefreshWorker> logger;

    public SystemParameterRefreshWorker(
        SystemParameterRuntime runtime,
        ExceptionContextAccessor contextAccessor,
        TimeProvider timeProvider,
        ILogger<SystemParameterRefreshWorker> logger)
    {
        this.runtime = runtime;
        this.contextAccessor = contextAccessor;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, "SystemParameterRefresh", null, null));
        using var timer = new PeriodicTimer(Interval, timeProvider);
        var failing = false;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await runtime.RefreshAsync(stoppingToken);
                    failing = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 讀不到資料庫時維持目前的設定，下一分鐘再試；連續失敗只記第一次。
                    if (!failing)
                    {
                        logger.LogError(ex, "Failed to refresh system parameters; keeping the current values.");
                    }

                    failing = true;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關機。
        }
    }
}
