using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Caching;
using System.Threading.Channels;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Health;

public interface ISystemHealthService
{
    Task<SystemHealthReport> GetReportAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemHealthService : ISystemHealthService
{
    private readonly BackendDBContext context;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly IActionDescriptorCollectionProvider actionDescriptorProvider;
    private readonly IOptions<AuthenticationOptions> authenticationOptions;
    private readonly IOptions<JwtSettings> jwtOptions;
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly IOptions<SwaggerSettings> swaggerOptions;
    private readonly IOptions<CorsSettings> corsOptions;
    private readonly IHealthLogReader logReader;
    private readonly SystemStartupState startupState;
    private readonly IAiHealthProbe aiHealthProbe;
    private readonly ICacheService cacheService;
    private readonly IOptionsMonitor<AiSettings> aiOptions;
    private readonly IOptionsMonitor<AiPricingSettings> aiPricingOptions;
    private readonly IOptions<CacheSettings> cacheOptions;
    private readonly IEmailHealthProbe emailHealthProbe;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ILogger<SystemHealthService> logger;
    private readonly LoggingPipelineMonitor pipelineMonitor;
    private readonly ChannelReader<ExceptionLogEntry> exceptionQueue;
    private readonly ScheduledJobOverviewService scheduledJobOverview;

    public SystemHealthService(
        BackendDBContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IActionDescriptorCollectionProvider actionDescriptorProvider,
        IOptions<AuthenticationOptions> authenticationOptions,
        IOptions<JwtSettings> jwtOptions,
        IOptions<SystemSettings> systemSettingsOptions,
        IOptions<SwaggerSettings> swaggerOptions,
        IOptions<CorsSettings> corsOptions,
        IHealthLogReader logReader,
        SystemStartupState startupState,
        IAiHealthProbe aiHealthProbe,
        ICacheService cacheService,
        IOptionsMonitor<AiSettings> aiOptions,
        IOptionsMonitor<AiPricingSettings> aiPricingOptions,
        IOptions<CacheSettings> cacheOptions,
        IEmailHealthProbe emailHealthProbe,
        IOptionsMonitor<EmailSettings> emailOptions,
        ILogger<SystemHealthService> logger,
        LoggingPipelineMonitor pipelineMonitor,
        ChannelReader<ExceptionLogEntry> exceptionQueue,
        ScheduledJobOverviewService scheduledJobOverview)
    {
        this.scheduledJobOverview = scheduledJobOverview;
        this.pipelineMonitor = pipelineMonitor;
        this.exceptionQueue = exceptionQueue;
        this.context = context;
        this.configuration = configuration;
        this.environment = environment;
        this.actionDescriptorProvider = actionDescriptorProvider;
        this.authenticationOptions = authenticationOptions;
        this.jwtOptions = jwtOptions;
        this.systemSettingsOptions = systemSettingsOptions;
        this.swaggerOptions = swaggerOptions;
        this.corsOptions = corsOptions;
        this.logReader = logReader;
        this.startupState = startupState;
        this.aiHealthProbe = aiHealthProbe;
        this.cacheService = cacheService;
        this.aiOptions = aiOptions;
        this.aiPricingOptions = aiPricingOptions;
        this.cacheOptions = cacheOptions;
        this.emailHealthProbe = emailHealthProbe;
        this.logger = logger;
        this.emailOptions = emailOptions;
    }

    public async Task<SystemHealthReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<SystemHealthItem>
        {
            CheckApplication(),
            CheckApi(),
            await CheckDatabaseAsync(cancellationToken),
            CheckLogging(),
            CheckAuthentication(),
            CheckFileSystem(),
            CheckHostResources(),
            CheckSecuritySettings(),
            await CheckAiAsync(cancellationToken),
            await CheckCacheAsync(cancellationToken),
            CheckAiPricing(),
            await CheckEmailAsync(cancellationToken),
            CheckLoggingPipeline(),
            await CheckScheduledJobsAsync()
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        return new SystemHealthReport
        {
            CheckedAt = DateTimeOffset.Now,
            Score = score,
            Status = SystemHealthScoreCalculator.GetStatus(score),
            Light = SystemHealthScoreCalculator.GetLight(score),
            Items = items
        };
    }

    private SystemHealthItem CheckApplication()
    {
        var systemInfo = systemSettingsOptions.Value.SystemInformation;
        var uptime = DateTimeOffset.Now - startupState.StartedAt;

        return CreateItem(
            "網站 / 應用程式",
            "Application",
            10,
            string.IsNullOrWhiteSpace(systemInfo.SystemVersion)
                ? SystemHealthStatus.Degraded
                : SystemHealthStatus.Healthy,
            $"環境：{environment.EnvironmentName}；版本：{systemInfo.SystemVersion}；啟動時間：{startupState.StartedAt:yyyy/MM/dd HH:mm:ss}；已運作：{uptime:g}。",
            string.IsNullOrWhiteSpace(systemInfo.SystemVersion) ? "SystemVersion 未設定。" : null);
    }

    private SystemHealthItem CheckApi()
    {
        var controllerCount = actionDescriptorProvider.ActionDescriptors.Items
            .Count(action => action.RouteValues.ContainsKey("controller"));
        var swaggerSettings = swaggerOptions.Value;
        var swaggerEvidence = environment.IsDevelopment() || swaggerSettings.EnabledInProduction
            ? "Swagger UI 依目前環境/設定可啟用"
            : "Swagger UI 在非開發環境預設關閉";

        var status = controllerCount > 0 ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "API",
            "API",
            10,
            status,
            $"Controller action 數量：{controllerCount}；{swaggerEvidence}。",
            status == SystemHealthStatus.Healthy ? null : "找不到 Controller action 註冊。");
    }

    private async Task<SystemHealthItem> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var canConnect = await context.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                return CreateItem(
                    "資料庫",
                    "Database",
                    25,
                    SystemHealthStatus.Unhealthy,
                    "Provider：SQLite；無法建立資料庫連線。",
                    "Database.CanConnectAsync 回傳 false。");
            }

            var pendingMigrations = context.Database.GetMigrations().Any()
                ? (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count()
                : 0;

            var status = pendingMigrations == 0
                ? SystemHealthStatus.Healthy
                : SystemHealthStatus.Degraded;

            return CreateItem(
                "資料庫",
                "Database",
                25,
                status,
                $"Provider：SQLite；可連線；待套用 migration：{pendingMigrations}。",
                pendingMigrations == 0 ? null : "仍有尚未套用的 EF Core migration。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "Database");
            return CreateItem(
                "資料庫",
                "Database",
                25,
                SystemHealthStatus.Unhealthy,
                $"Provider：SQLite；檢查時發生 {ex.GetType().Name}。",
                $"資料庫檢查失敗：{ex.GetType().Name}。");
        }
    }

    private SystemHealthItem CheckLogging()
    {
        var logTail = logReader.ReadLatestLines(100);
        var logDirectory = string.IsNullOrWhiteSpace(logTail.FilePath)
            ? string.Empty
            : Path.GetDirectoryName(logTail.FilePath) ?? string.Empty;
        var directoryWritable = DirectoryIsWritable(logDirectory);
        var status = logTail.Status;

        if (!directoryWritable)
        {
            status = SystemHealthStatus.Unhealthy;
        }
        else if (status == SystemHealthStatus.Healthy && logTail.Lines.Count == 0)
        {
            status = SystemHealthStatus.Degraded;
        }

        return CreateItem(
            "日誌",
            "Logging",
            15,
            status,
            $"目錄：{(string.IsNullOrWhiteSpace(logDirectory) ? "未設定" : logDirectory)}；今日檔案：{logTail.FilePath}；最後讀取筆數：{logTail.Lines.Count}。",
            status == SystemHealthStatus.Healthy ? null : logTail.Message);
    }

    private SystemHealthItem CheckAuthentication()
    {
        var jwtSettings = jwtOptions.Value;
        var schemes = authenticationOptions.Value.Schemes.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);
        var hasCookie = schemes.Contains(MagicObjectHelper.CookieScheme);
        var hasJwt = schemes.Contains(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        var hasRequiredJwtSettings = !string.IsNullOrWhiteSpace(jwtSettings.Issuer)
            && !string.IsNullOrWhiteSpace(jwtSettings.Audience)
            && !string.IsNullOrWhiteSpace(jwtSettings.SigningKey)
            && jwtSettings.SigningKey.Length >= 32;
        var usesDevelopmentKeyInProduction = environment.IsProduction()
            && JwtSettings.IsPlaceholderSigningKey(jwtSettings.SigningKey);

        var status = hasCookie && hasJwt && hasRequiredJwtSettings && !usesDevelopmentKeyInProduction
            ? SystemHealthStatus.Healthy
            : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "身分驗證",
            "Authentication",
            15,
            status,
            $"Cookie scheme：{hasCookie}；JWT bearer：{hasJwt}；Issuer：{MaskPresence(jwtSettings.Issuer)}；Audience：{MaskPresence(jwtSettings.Audience)}；SigningKey 長度：{jwtSettings.SigningKey.Length}。",
            status == SystemHealthStatus.Healthy ? null : "Cookie/JWT 設定不完整，或 Production 仍使用開發用 JWT key。");
    }

    private SystemHealthItem CheckFileSystem()
    {
        var paths = new Dictionary<string, string>
        {
            ["Database"] = systemSettingsOptions.Value.ExternalFileSystem.DatabasePath,
            ["Download"] = systemSettingsOptions.Value.ExternalFileSystem.DownloadPath,
            ["Upload"] = systemSettingsOptions.Value.ExternalFileSystem.UploadPath,
            ["ProjectFile"] = systemSettingsOptions.Value.ExternalFileSystem.ProjectFilePath
        };

        var failures = paths
            .Where(path => string.IsNullOrWhiteSpace(path.Value) || !Directory.Exists(path.Value) || !DirectoryIsWritable(path.Value))
            .Select(path => path.Key)
            .ToList();
        var status = failures.Count == 0 ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "檔案系統",
            "FileSystem",
            10,
            status,
            string.Join("；", paths.Select(path => $"{path.Key}：{path.Value}")),
            status == SystemHealthStatus.Healthy ? null : $"目錄不存在或不可寫入：{string.Join(", ", failures)}。");
    }

    /// <summary>
    /// 日誌與例外管線本身（LOG-22）：記錄機制壞了卻沒人知道，是最難發現的一種故障。
    /// 數值都是「本次啟動以來」的累計。
    /// </summary>
    private SystemHealthItem CheckLoggingPipeline()
    {
        double? freeGb = null;
        try
        {
            var basePath = configuration.GetValue<string>("NLog:BasePath");
            var root = string.IsNullOrWhiteSpace(basePath) ? null : Path.GetPathRoot(Path.GetFullPath(basePath));
            if (string.IsNullOrWhiteSpace(root) == false)
            {
                freeGb = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "LogDisk");
        }

        return EvaluateLoggingPipeline(
            pipelineMonitor.Snapshot(),
            exceptionQueue.CanCount ? exceptionQueue.Count : 0,
            ExceptionLogProvider.QueueCapacity,
            ExceptionLogProvider.DroppedCount,
            freeGb);
    }

    /// <summary>
    /// 判斷「日誌管線」的狀態。純函式，方便測試各種組合。
    /// 不健康：日誌磁碟剩不到 200 MB，或例外佇列已滿。
    /// 降級：任何丟棄、寫入失敗、告警失敗、NLog 內部錯誤，佇列使用率達八成，或磁碟剩不到 1 GB。
    /// 前端回報被頻率限制擋下的筆數只列在佐證、不影響狀態：那是限流照設計運作。
    /// </summary>
    internal static SystemHealthItem EvaluateLoggingPipeline(
        LoggingPipelineSnapshot snapshot,
        int queueLength,
        int queueCapacity,
        long droppedEntries,
        double? logDiskFreeGb)
    {
        var problems = new List<string>();
        var status = SystemHealthStatus.Healthy;

        void Degrade(string problem)
        {
            problems.Add(problem);
            if (status == SystemHealthStatus.Healthy)
            {
                status = SystemHealthStatus.Degraded;
            }
        }

        void Fail(string problem)
        {
            problems.Add(problem);
            status = SystemHealthStatus.Unhealthy;
        }

        if (logDiskFreeGb is < 0.2)
        {
            Fail("日誌所在磁碟可用空間低於 200 MB，日誌即將無法寫入。");
        }
        else if (logDiskFreeGb is < 1)
        {
            Degrade("日誌所在磁碟可用空間低於 1 GB。");
        }

        if (queueLength >= queueCapacity)
        {
            Fail("例外佇列已滿，新的例外會被丟棄。");
        }
        else if (queueLength >= queueCapacity * 0.8)
        {
            Degrade("例外佇列使用率已達八成。");
        }

        if (droppedEntries > 0)
        {
            Degrade($"有 {droppedEntries:N0} 筆例外因佇列滿載被丟棄。");
        }

        if (snapshot.WriteFailures > 0)
        {
            Degrade($"有 {snapshot.WriteFailures:N0} 筆例外寫入資料庫失敗。");
        }

        if (snapshot.AlertQueueFailures + snapshot.AlertSendFailures > 0)
        {
            Degrade($"例外告警信有 {snapshot.AlertQueueFailures + snapshot.AlertSendFailures:N0} 封未能送出。");
        }

        if (snapshot.NLogInternalErrors > 0)
        {
            Degrade($"NLog 發生 {snapshot.NLogInternalErrors:N0} 次內部錯誤（請看 NLog 內部日誌）。");
        }

        var lastWriteFailure = snapshot.LastWriteFailureAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "—";
        var disk = logDiskFreeGb is null ? "未設定或無法讀取" : $"{logDiskFreeGb:N2} GB";
        var evidence =
            $"本次啟動以來：例外佇列 {queueLength:N0}/{queueCapacity:N0}；丟棄 {droppedEntries:N0}；"
            + $"寫入失敗 {snapshot.WriteFailures:N0}（最後 {lastWriteFailure}）；"
            + $"前端錯誤回報因限流丟棄 {snapshot.ClientErrorsDropped:N0}；"
            + $"告警入列失敗 {snapshot.AlertQueueFailures:N0}、寄送失敗 {snapshot.AlertSendFailures:N0}；"
            + $"NLog 內部錯誤 {snapshot.NLogInternalErrors:N0}；日誌磁碟可用 {disk}。";

        return CreateItem(
            "日誌管線",
            "LoggingPipeline",
            10,
            status,
            evidence,
            problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private async Task<SystemHealthItem> CheckScheduledJobsAsync()
    {
        try
        {
            return EvaluateScheduledJobs(await scheduledJobOverview.GetOverviewAsync());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "ScheduledJobs");
            return CreateItem(
                "排程作業",
                "ScheduledJobs",
                5,
                SystemHealthStatus.Unhealthy,
                "無法讀取排程作業的狀態。",
                "讀取排程作業狀態時發生錯誤，詳細內容請看系統例外紀錄。");
        }
    }

    /// <summary>
    /// 排程作業（0.9.96 起）：啟用中的作業最近一次執行失敗 → 異常；總開關關閉、作業逾期沒有被任何行程執行、
    /// 或執行中的設定被改壞 → 警示。「逾期」是唯一看得出「應用程式集區閒置停止、排程根本沒跑」的地方。
    /// </summary>
    internal static SystemHealthItem EvaluateScheduledJobs(ScheduledJobOverview overview)
    {
        var problems = new List<string>();
        var status = SystemHealthStatus.Healthy;

        void Degrade(string problem)
        {
            problems.Add(problem);
            if (status == SystemHealthStatus.Healthy)
            {
                status = SystemHealthStatus.Degraded;
            }
        }

        var failed = overview.Items
            .Where(x => x.IsEnabled && x.LastFinishedRun?.Status == JobRunStatuses.Failed)
            .Select(x => x.DisplayName)
            .ToList();
        if (failed.Count > 0)
        {
            problems.Add($"最近一次執行失敗：{string.Join("、", failed)}。");
            status = SystemHealthStatus.Unhealthy;
        }

        var overdue = overview.Items.Where(x => x.OverdueSlotUtc is not null).Select(x => x.DisplayName).ToList();
        if (!overview.SchedulingEnabled)
        {
            Degrade("排程總開關已關閉（ScheduledJobSettings:Enabled），作業不會自動執行。");
        }
        else if (overdue.Count > 0)
        {
            Degrade($"逾期未執行：{string.Join("、", overdue)}（網站可能閒置停止，請確認 IIS 的「永遠執行」與閒置逾時設定）。");
        }

        if (overview.SettingsError is not null)
        {
            Degrade("排程設定有誤，目前沿用上一份正確的設定。");
        }

        var enabledCount = overview.Items.Count(x => x.IsEnabled);
        var evidence = $"{overview.Items.Count} 個作業，啟用 {enabledCount} 個；最近一次失敗 {failed.Count} 個；逾期 {overdue.Count} 個。";

        return CreateItem(
            "排程作業",
            "ScheduledJobs",
            5,
            status,
            evidence,
            problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private SystemHealthItem CheckHostResources()
    {
        var process = Process.GetCurrentProcess();
        var currentDriveRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        var drive = string.IsNullOrWhiteSpace(currentDriveRoot)
            ? null
            : DriveInfo.GetDrives().FirstOrDefault(item => string.Equals(item.Name, currentDriveRoot, StringComparison.OrdinalIgnoreCase));
        var freeGb = drive is null ? 0 : drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var status = drive is null || freeGb >= 1
            ? SystemHealthStatus.Healthy
            : SystemHealthStatus.Degraded;

        return CreateItem(
            "主機資源",
            "Host",
            5,
            status,
            $"Working set：{process.WorkingSet64 / 1024 / 1024} MB；磁碟可用空間：{freeGb:N2} GB。",
            status == SystemHealthStatus.Healthy ? null : "目前磁碟可用空間低於 1 GB。");
    }

    private SystemHealthItem CheckSecuritySettings()
    {
        var swaggerSettings = swaggerOptions.Value;
        var corsSettings = corsOptions.Value;
        var returnExceptionDetails = configuration.GetValue<bool?>("Security:ReturnExceptionDetails");
        var productionRisk = environment.IsProduction()
            && (swaggerSettings.EnabledInProduction || returnExceptionDetails == true);
        var status = productionRisk ? SystemHealthStatus.Degraded : SystemHealthStatus.Healthy;

        return CreateItem(
            "安全設定",
            "Security",
            10,
            status,
            $"Swagger.EnabledInProduction：{swaggerSettings.EnabledInProduction}；CORS origins：{corsSettings.AllowedOrigins.Length}；ReturnExceptionDetails：{returnExceptionDetails?.ToString() ?? "null"}。",
            status == SystemHealthStatus.Healthy ? null : "Production 開啟了診斷或 Swagger 設定，請確認是否符合部署政策。");
    }

    /// <summary>
    /// LLM API 檢測（權重 10）：實際送一句 hello，確認 API 真的有回應。
    ///
    /// ⚠️ 這是唯一一項<b>每次開頁面都會產生費用</b>的檢查，且會記進「Token 用量」
    /// （作業名稱「系統健康檢測」）。逾時由 AiHealthProbe 自己控制在 30 秒，
    /// 刻意不沿用 AiSettings.TimeoutSeconds（預設 600 秒）。
    ///
    /// AI 是選配功能：未設定時回黃燈而非紅燈，否則沒接 AI 的部署會永遠是紅的。
    /// </summary>
    private async Task<SystemHealthItem> CheckAiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probe = await aiHealthProbe.ProbeAsync(cancellationToken);

            if (probe.IsConfigured == false)
            {
                return CreateItem(
                    "LLM API",
                    "Ai",
                    10,
                    SystemHealthStatus.Degraded,
                    "AI 未設定（選配功能），未發出任何請求。",
                    probe.Message);
            }

            var status = probe.Success ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

            return CreateItem(
                "LLM API",
                "Ai",
                10,
                status,
                $"端點：{probe.EndpointHost}；模型：{probe.ModelName}；耗時：{probe.ElapsedMilliseconds} ms。",
                status == SystemHealthStatus.Healthy ? null : probe.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "Ai");
            // AiHealthProbe 內部已全程吞例外，這裡是最後一道保險 ——
            // 單一項目絕不能讓整份報告掛掉（其他檢查項目沒有這層保護）。
            return CreateItem(
                "LLM API",
                "Ai",
                10,
                SystemHealthStatus.Unhealthy,
                $"檢查時發生 {ex.GetType().Name}。",
                $"LLM 檢查失敗：{ex.GetType().Name}。");
        }
    }

    /// <summary>
    /// 快取服務實測（權重 10）：寫一筆 sentinel 再讀回比對，確認快取真的能用。
    ///
    /// 只看設定不夠 —— Provider 設成 Redis 但連不上時，設定看起來完全正常。
    /// ⚠️ DistributedCacheService 不吞例外，Redis 掛掉會直接往外拋，所以整段必須包住。
    /// </summary>
    private async Task<SystemHealthItem> CheckCacheAsync(CancellationToken cancellationToken)
    {
        var settings = cacheOptions.Value;

        // GetProvider() 在 provider 打錯字時會拋例外，先擋下來翻成可讀訊息。
        string providerName;
        try
        {
            providerName = settings.GetProvider().ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "CacheProvider");
            return CreateItem(
                "快取服務",
                "Cache",
                10,
                SystemHealthStatus.Unhealthy,
                $"Provider 設定值無法解析：{settings.Provider}。",
                $"快取 provider 設定錯誤：{ex.GetType().Name}。");
        }

        var evidence =
            $"Provider：{providerName}；InstanceName：{settings.InstanceName}；"
            + $"Redis 連線字串：{MaskPresence(settings.RedisConnection)}";

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));

            var key = $"health:probe:{Guid.NewGuid():N}";
            var expected = Guid.NewGuid().ToString("N");

            var stopwatch = Stopwatch.StartNew();
            await cacheService.SetAsync(key, expected, TimeSpan.FromMinutes(1), timeoutSource.Token);
            var actual = await cacheService.GetAsync<string>(key, timeoutSource.Token);
            stopwatch.Stop();

            await cacheService.RemoveAsync(key, timeoutSource.Token);

            var matched = string.Equals(actual, expected, StringComparison.Ordinal);
            var status = matched ? SystemHealthStatus.Healthy : SystemHealthStatus.Degraded;

            return CreateItem(
                "快取服務",
                "Cache",
                10,
                status,
                $"{evidence}；往返耗時：{stopwatch.ElapsedMilliseconds} ms。",
                matched ? null : "寫入後讀回的值與寫入值不符，快取可能未真正生效。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed. Check={Check}", "Cache");
            return CreateItem(
                "快取服務",
                "Cache",
                10,
                SystemHealthStatus.Unhealthy,
                $"{evidence}；讀寫測試發生 {ex.GetType().Name}。",
                $"快取讀寫失敗：{ex.GetType().Name}。");
        }
    }

    /// <summary>
    /// AI 計費表涵蓋率（權重 5）：確認目前使用的模型在 AiPricingSettings 裡查得到價格。
    ///
    /// 查不到不影響系統運作，但「Token 用量」頁的費用會靜默記成未定價（估算為 0），
    /// 對帳時才會發現少算 —— 所以值得一個黃燈。
    ///
    /// 比對邏輯直接重用 AiUsageCostCalculator.Resolve，不自己實作：
    /// 它是完全比對優先、再取後綴合法的最長前綴，自己寫一份必然會和實際計費行為不一致。
    /// </summary>
    private SystemHealthItem CheckAiPricing()
    {
        var aiSettings = aiOptions.CurrentValue;
        var pricing = aiPricingOptions.CurrentValue;

        if (AiChatEndpoint.Validate(aiSettings) is not null)
        {
            return CreateItem(
                "AI 計費表",
                "AiPricing",
                5,
                SystemHealthStatus.Healthy,
                "AI 未啟用，無需計費表。",
                null);
        }

        if (pricing.UsdToTwd <= 0)
        {
            return CreateItem(
                "AI 計費表",
                "AiPricing",
                5,
                SystemHealthStatus.Degraded,
                $"模型：{aiSettings.Model}；匯率 UsdToTwd：{pricing.UsdToTwd}。",
                "UsdToTwd 未設定（小於等於 0），所有呼叫都會記為未定價。");
        }

        var resolved = AiUsageCostCalculator.Resolve(pricing, aiSettings.Model);
        var status = resolved is null ? SystemHealthStatus.Degraded : SystemHealthStatus.Healthy;

        return CreateItem(
            "AI 計費表",
            "AiPricing",
            5,
            status,
            $"模型：{aiSettings.Model}；計費表筆數：{pricing.Models.Count}；"
            + $"命中價格鍵：{resolved?.Key ?? "無"}；匯率：{pricing.UsdToTwd}。",
            resolved is null
                ? "目前模型在 AiPricingSettings.Models 查無對應價格，費用會估算為 0（記為未定價）。"
                : null);
    }

    /// <summary>
    /// 寄信服務（權重 10）。
    ///
    /// 寄信是選配功能：None（未啟用）與 Pickup（開發用，信不會真的寄出）回黃燈而非紅燈，
    /// 比照 LLM 的做法。Smtp 才實測：5 秒內完成連線＋加密＋登入為綠燈，不實際寄信。
    /// </summary>
    private async Task<SystemHealthItem> CheckEmailAsync(CancellationToken cancellationToken)
    {
        var settings = emailOptions.CurrentValue;

        if (!settings.TryGetProvider(out var provider))
        {
            return CreateItem(
                "寄信服務",
                "Email",
                10,
                SystemHealthStatus.Unhealthy,
                $"Provider 設定值無法解析：{settings.Provider}。",
                "寄信 provider 設定錯誤，只接受 None、Pickup 或 Smtp。");
        }

        if (provider == EmailProvider.None)
        {
            return CreateItem(
                "寄信服務",
                "Email",
                10,
                SystemHealthStatus.Degraded,
                "Provider：None；寄信功能未啟用（選配功能），系統不會寄出任何信件，登入頁也不提供忘記密碼。",
                "尚未設定寄信服務。");
        }

        if (provider == EmailProvider.Pickup)
        {
            return CreateItem(
                "寄信服務",
                "Email",
                10,
                SystemHealthStatus.Degraded,
                $"Provider：Pickup；信件寫入 {settings.PickupDirectory}，不會真正寄出。",
                "Pickup 僅供開發使用，正式環境請改用 Smtp。");
        }

        var evidence =
            $"Provider：Smtp；主機：{settings.Host}:{settings.Port}；加密：{settings.Security}；"
            + $"登入帳號：{MaskPresence(settings.UserName)}；寄件者：{MaskPresence(settings.FromAddress)}";

        // EmailHealthProbe 內部已吞下所有例外並自帶 5 秒上限。
        var probe = await emailHealthProbe.ProbeAsync(cancellationToken);

        return CreateItem(
            "寄信服務",
            "Email",
            10,
            probe.Success ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy,
            $"{evidence}；耗時：{probe.ElapsedMilliseconds} ms。",
            probe.Success ? null : probe.Message);
    }

    private static SystemHealthItem CreateItem(
        string name,
        string category,
        int weight,
        SystemHealthStatus status,
        string evidence,
        string? failureMessage)
    {
        return new SystemHealthItem
        {
            Name = name,
            Category = category,
            Weight = weight,
            Status = status,
            Light = SystemHealthScoreCalculator.GetLight(status),
            Evidence = evidence,
            FailureMessage = failureMessage
        };
    }

    private static string MaskPresence(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未設定" : "已設定";
    }

    private static bool DirectoryIsWritable(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        try
        {
            var testFile = Path.Combine(directoryPath, $".health-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
