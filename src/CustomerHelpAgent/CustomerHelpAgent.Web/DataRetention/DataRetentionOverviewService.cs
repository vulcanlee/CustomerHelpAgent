using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Backup;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Parameters;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Web.DataRetention;

/// <summary>「資料保留與清理」頁的一列（一種資料）。值為 null 的欄位在畫面上顯示「—」（不適用）。</summary>
public sealed record DataRetentionRow
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>一併清掉的檔案等補充說明，例如「連同堆疊檔」。</summary>
    public string? Note { get; init; }

    public int? Count { get; init; }

    public string CountUnit { get; init; } = "筆";

    public FolderUsage? Folder { get; init; }

    public DateTime? OldestLocal { get; init; }

    public string RuleText { get; init; } = string.Empty;

    /// <summary>保留規則可在系統參數頁修改時的群組名稱（連結用）；null＝不能在網頁上改。</summary>
    public string? ParameterGroup { get; init; }

    /// <summary>下次自動清理會刪掉的數量；null＝不適用或不自動清理。</summary>
    public int? Expected { get; init; }

    public string? ExpectedNote { get; init; }

    /// <summary>負責清理的排程作業；null＝沒有排程（日誌檔、公告、排程執行紀錄）。</summary>
    public string? JobName { get; init; }

    public ScheduledJobOverviewItem? Job { get; init; }

    public string ScheduleText { get; init; } = "—";

    /// <summary>「立即清理」可以按；不能按時 <see cref="PurgeDisabledReason"/> 說明原因（null＝這一列沒有這個按鈕）。</summary>
    public bool CanPurgeNow { get; init; }

    public string? PurgeDisabledReason { get; init; }

    public string? ManageUrl { get; init; }

    /// <summary>這一列有「自訂清除」（0.9.134 起）。</summary>
    public bool CanCustomPurge { get; init; }

    /// <summary>這一列讀取失敗時的訊息；其他欄位是預設值。</summary>
    public string? Error { get; init; }
}

public sealed record DataRetentionOverview(bool SchedulingEnabled, string? SettingsError, IReadOnlyList<DataRetentionRow> Rows);

/// <summary>
/// 「自訂清除」的結果：刪掉幾筆、留下幾筆（只有已刪除資料會有：受保護、仍在使用、還有下屬或中途被還原），
/// 以及稽核要記的動作代碼與對象種類（由呼叫端寫，才記得到操作者）。
/// <paramref name="Failed"/>：已刪除資料途中有一筆失敗（已記錄錯誤），<paramref name="Removed"/> 是失敗前已刪掉的。
/// </summary>
public sealed record CustomPurgeOutcome(int Removed, int Left, string AuditAction, string TargetType, bool Failed = false);

/// <summary>
/// 「資料保留與清理」頁（0.9.133 起）：每種資料目前多少、佔多少空間、最舊到何時、保留規則、下次會清掉幾筆，
/// 以及負責清理的排程。「下次會清掉幾筆」由各服務以與清理相同的條件計算（<see cref="RetentionCutoffs"/>）。
/// 每一列各自 try/catch：一種資料讀不到只影響那一列。
/// </summary>
public sealed class DataRetentionOverviewService
{
    /// <summary>資料夾大小加總的時間上限：超過就顯示「至少」，不讓整頁卡住。</summary>
    private static readonly TimeSpan FolderBudget = TimeSpan.FromSeconds(10);

    private const string SoftDeleteNote = "最多；support 帳號、預設角色、仍在使用的角色與還有下屬的部門會留到下次";

    private readonly ScheduledJobOverviewService jobOverviewService;
    private readonly SoftDeletePurgeService softDeletePurgeService;
    private readonly ExceptionLogService exceptionLogService;
    private readonly TokenUsageLogService tokenUsageLogService;
    private readonly AiCallLogService aiCallLogService;
    private readonly AuditLogQueryService auditLogQueryService;
    private readonly NotificationQueryService notificationQueryService;
    private readonly ScheduledJobRunService jobRunService;
    private readonly AnnouncementService announcementService;
    private readonly BackupStore backupStore;
    private readonly INLogFilePathResolver nlogPathResolver;
    private readonly IOptions<SystemSettings> systemSettings;
    private readonly IOptionsMonitor<LogRetentionSettings> logRetention;
    private readonly IOptionsMonitor<SoftDeleteSettings> softDelete;
    private readonly IOptionsMonitor<NotificationSettings> notification;
    private readonly IOptionsMonitor<BackupSettings> backup;
    private readonly IOptionsMonitor<ScheduledJobSettings> scheduledJob;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DataRetentionOverviewService> logger;

    public DataRetentionOverviewService(
        ScheduledJobOverviewService jobOverviewService,
        SoftDeletePurgeService softDeletePurgeService,
        ExceptionLogService exceptionLogService,
        TokenUsageLogService tokenUsageLogService,
        AiCallLogService aiCallLogService,
        AuditLogQueryService auditLogQueryService,
        NotificationQueryService notificationQueryService,
        ScheduledJobRunService jobRunService,
        AnnouncementService announcementService,
        BackupStore backupStore,
        INLogFilePathResolver nlogPathResolver,
        IOptions<SystemSettings> systemSettings,
        IOptionsMonitor<LogRetentionSettings> logRetention,
        IOptionsMonitor<SoftDeleteSettings> softDelete,
        IOptionsMonitor<NotificationSettings> notification,
        IOptionsMonitor<BackupSettings> backup,
        IOptionsMonitor<ScheduledJobSettings> scheduledJob,
        TimeProvider timeProvider,
        ILogger<DataRetentionOverviewService> logger)
    {
        this.jobOverviewService = jobOverviewService;
        this.softDeletePurgeService = softDeletePurgeService;
        this.exceptionLogService = exceptionLogService;
        this.tokenUsageLogService = tokenUsageLogService;
        this.aiCallLogService = aiCallLogService;
        this.auditLogQueryService = auditLogQueryService;
        this.notificationQueryService = notificationQueryService;
        this.jobRunService = jobRunService;
        this.announcementService = announcementService;
        this.backupStore = backupStore;
        this.nlogPathResolver = nlogPathResolver;
        this.systemSettings = systemSettings;
        this.logRetention = logRetention;
        this.softDelete = softDelete;
        this.notification = notification;
        this.backup = backup;
        this.scheduledJob = scheduledJob;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<DataRetentionOverview> GetOverviewAsync()
    {
        var jobs = await jobOverviewService.GetOverviewAsync();
        var jobByName = jobs.Items.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var paths = systemSettings.Value.ExternalFileSystem;
        var nlogDirectory = nlogPathResolver.GetLogDirectory();

        // 資料夾大小是磁碟 IO，先在背景開跑，與下面的資料庫查詢同時進行。
        using var budget = new CancellationTokenSource(FolderBudget);
        var exceptionFolder = MeasureAsync(paths.ExceptionPath, budget.Token);
        var tokenFolder = MeasureAsync(paths.TokenUsagePath, budget.Token);
        var aiFolder = MeasureAsync(paths.AiCallLogPath, budget.Token);
        var auditFolder = MeasureAsync(paths.AuditContentPath, budget.Token);
        var backupFolder = MeasureAsync(paths.BackupPath, budget.Token);
        var nlogFolder = Task.Run(() => DirectorySizeProbe.Measure(nlogDirectory, IsLogFile, budget.Token));

        var rows = new List<DataRetentionRow>();
        rows.AddRange(await SoftDeleteRowsAsync(jobByName));
        rows.Add(await RowAsync("exception-log", "系統例外紀錄", () => ExceptionLogRowAsync(jobByName, exceptionFolder)));
        rows.Add(await RowAsync("token-usage", "Token 用量", () => TokenUsageRowAsync(jobByName, tokenFolder)));
        rows.Add(await RowAsync("ai-call-log", "AI 對話紀錄", () => AiCallLogRowAsync(jobByName, aiFolder)));
        rows.Add(await RowAsync("audit-log", "稽核紀錄", () => AuditLogRowAsync(jobByName, auditFolder)));
        rows.Add(await RowAsync("notification", "站內通知", () => NotificationRowAsync(jobByName)));
        rows.Add(await RowAsync("backup", "系統備份", () => BackupRowAsync(jobByName, backupFolder)));
        rows.Add(await RowAsync("job-run", "排程執行紀錄", JobRunRowAsync));
        rows.Add(await RowAsync("nlog", "日誌檔", () => NLogRowAsync(nlogDirectory, nlogFolder)));
        rows.Add(await RowAsync("announcement", "公告", AnnouncementRowAsync));

        return new DataRetentionOverview(jobs.SchedulingEnabled, jobs.SettingsError, rows);
    }

    /// <summary>「立即清理」：與「排程作業」頁的「立即執行」相同（同一個佇列）。</summary>
    public bool TryTrigger(string jobName, string? account, int? userId)
        => jobOverviewService.TryTrigger(jobName, account, userId);

    /// <summary>「自訂清除」的預覽：依 <paramref name="cutoff"/> 會刪掉幾筆（與清除同一個條件；已刪除資料是「最多」）。</summary>
    public async Task<int> PreviewCustomAsync(string rowKey, CustomPurgeCutoff cutoff)
    {
        var threshold = cutoff.ToThreshold(BasisOf(rowKey), timeProvider);
        var stats = rowKey switch
        {
            "exception-log" => await exceptionLogService.GetRetentionStatsAsync(threshold),
            "token-usage" => await tokenUsageLogService.GetRetentionStatsAsync(threshold),
            "ai-call-log" => await aiCallLogService.GetRetentionStatsBeforeAsync(threshold),
            "audit-log" => await auditLogQueryService.GetRetentionStatsAsync(threshold),
            "notification" => await notificationQueryService.GetRetentionStatsAsync(threshold, null),
            "job-run" => await jobRunService.GetRetentionStatsAsync(threshold),
            _ when SoftDeleteEntityOf(rowKey) is { } entity => (await softDeletePurgeService.GetRetentionStatsAsync(threshold)).Types[entity],
            _ => throw new ArgumentException($"「{rowKey}」沒有自訂清除。", nameof(rowKey)),
        };
        return stats.Expired ?? 0;
    }

    /// <summary>
    /// 「自訂清除」：刪除早於 <paramref name="cutoff"/> 的資料（連同檔案），只影響這一次、不改系統參數。
    /// 已刪除資料只清這一列那一種，保護規則與自動清理相同。失敗時拋出，由呼叫端通知。
    /// </summary>
    public async Task<CustomPurgeOutcome> PurgeCustomAsync(string rowKey, CustomPurgeCutoff cutoff, CancellationToken cancellationToken)
    {
        var threshold = cutoff.ToThreshold(BasisOf(rowKey), timeProvider);
        logger.LogInformation("Custom purge requested from the data retention page. RowKey={RowKey}, Threshold={Threshold}", rowKey, threshold);
        switch (rowKey)
        {
            case "exception-log":
                return new(await exceptionLogService.PurgeBeforeAsync(threshold), 0, AuditActions.ExceptionLog.Purge, "ExceptionLog");
            case "token-usage":
                return new(await tokenUsageLogService.PurgeBeforeBatchedAsync(threshold, cancellationToken)
                    ?? throw new InvalidOperationException("清除 Token 用量紀錄失敗。"), 0, AuditActions.TokenUsage.Purge, "TokenUsageLog");
            case "ai-call-log":
                return new(await aiCallLogService.PurgeOlderThanAsync(threshold)
                    ?? throw new InvalidOperationException("清除 AI 對話紀錄失敗。"), 0, AuditActions.AiCallLog.Purge, "AiCallLog");
            case "audit-log":
                return new(await auditLogQueryService.PurgeBeforeAsync(threshold), 0, AuditActions.Audit.Purge, "AuditLog");
            case "notification":
                return new(await notificationQueryService.PurgeBeforeAsync(threshold, cancellationToken), 0, AuditActions.Notification.Purge, "Notification");
            case "job-run":
                return new(await jobRunService.PruneAsync(threshold), 0, AuditActions.Job.PurgeRuns, "JobRun");
        }

        var entity = SoftDeleteEntityOf(rowKey) ?? throw new ArgumentException($"「{rowKey}」沒有自訂清除。", nameof(rowKey));
        var type = (await softDeletePurgeService.PurgeExpiredTypeAsync(entity, threshold, cancellationToken)).Types.Single();
        return new(type.Removed.Count, type.Protected + type.InUse + type.Skipped, SoftDeletePurgeAuditAction(entity), entity, type.Failed);
    }

    private static RetentionTimeBasis BasisOf(string rowKey) => rowKey switch
    {
        "audit-log" or "notification" or "job-run" => RetentionTimeBasis.Utc,
        "token-usage" or "ai-call-log" => RetentionTimeBasis.LocalDate,
        _ => RetentionTimeBasis.Local,
    };

    private static string? SoftDeleteEntityOf(string rowKey) => rowKey switch
    {
        "deleted-project" => nameof(Project),
        "deleted-category" => nameof(Category),
        "deleted-team" => nameof(Team),
        "deleted-user" => nameof(MyUser),
        "deleted-role" => nameof(RoleView),
        _ => null,
    };

    private static string SoftDeletePurgeAuditAction(string entity) => entity switch
    {
        nameof(Project) => AuditActions.Project.Purge,
        nameof(Category) => AuditActions.Category.Purge,
        nameof(Team) => AuditActions.Team.Purge,
        nameof(MyUser) => AuditActions.User.Purge,
        _ => AuditActions.Role.Purge,
    };

    private async Task<DataRetentionRow> RowAsync(string key, string label, Func<Task<DataRetentionRow>> build)
    {
        try
        {
            return await build();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load data retention row. RowKey={RowKey}", key);
            return new DataRetentionRow { Key = key, Label = label, Error = $"讀取失敗：{ex.GetType().Name}" };
        }
    }

    private async Task<IReadOnlyList<DataRetentionRow>> SoftDeleteRowsAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName)
    {
        (string Entity, string Key, string Label, string Url)[] kinds =
        [
            (nameof(Project), "deleted-project", "已刪除的專案", "/projects?deleted=1"),
            (nameof(Category), "deleted-category", "已刪除的分類", "/categories?deleted=1"),
            (nameof(Team), "deleted-team", "已刪除的團隊", "/teams?deleted=1"),
            (nameof(MyUser), "deleted-user", "已刪除的使用者", "/myusers?deleted=1"),
            (nameof(RoleView), "deleted-role", "已刪除的角色", "/roleviews?deleted=1"),
        ];

        try
        {
            var days = softDelete.CurrentValue.PurgeAfterDays;
            var stats = await softDeletePurgeService.GetRetentionStatsAsync(
                days > 0 ? RetentionCutoffs.SoftDelete(days, timeProvider) : null);
            var job = jobByName.GetValueOrDefault(SoftDeletePurgeJob.JobName);
            var rule = days > 0 ? $"刪除超過 {days} 天後永久刪除" : "不自動永久刪除（保留天數為 0）";

            return kinds.Select(kind =>
            {
                var s = stats.Types[kind.Entity];
                return WithJob(new DataRetentionRow
                {
                    Key = kind.Key,
                    Label = kind.Label,
                    Note = kind.Entity == nameof(Project) ? "連同附件檔" : null,
                    Count = s.Total,
                    Folder = kind.Entity == nameof(Project) ? new FolderUsage(FolderUsageStatus.Ok, stats.DeletedProjectFileBytes, 0) : null,
                    OldestLocal = s.Oldest,
                    RuleText = rule,
                    ParameterGroup = SystemParameterCatalog.GroupRetention,
                    Expected = s.Expired,
                    ExpectedNote = s.Expired > 0 ? SoftDeleteNote : null,
                    ManageUrl = kind.Url,
                    CanCustomPurge = true,
                }, SoftDeletePurgeJob.JobName, job, days > 0);
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load data retention rows for soft-deleted records.");
            return kinds.Select(kind => new DataRetentionRow { Key = kind.Key, Label = kind.Label, Error = $"讀取失敗：{ex.GetType().Name}" }).ToList();
        }
    }

    private async Task<DataRetentionRow> ExceptionLogRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName, Task<FolderUsage> folder)
    {
        var days = logRetention.CurrentValue.ExceptionLogDays;
        var stats = await exceptionLogService.GetRetentionStatsAsync(days > 0 ? RetentionCutoffs.ExceptionLog(days, timeProvider) : null);
        return WithJob(new DataRetentionRow
        {
            Key = "exception-log",
            Label = "系統例外紀錄",
            Note = "連同堆疊檔",
            Count = stats.Total,
            Folder = await folder,
            OldestLocal = stats.Oldest,
            RuleText = days > 0 ? $"{days} 天未再發生就刪除" : "不自動刪除（保留天數為 0）",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            ManageUrl = "/system-exceptions",
            CanCustomPurge = true,
        }, ExceptionLogRetentionJob.JobName, jobByName.GetValueOrDefault(ExceptionLogRetentionJob.JobName), days > 0);
    }

    private async Task<DataRetentionRow> TokenUsageRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName, Task<FolderUsage> folder)
    {
        var days = logRetention.CurrentValue.TokenUsageLogDays;
        var stats = await tokenUsageLogService.GetRetentionStatsAsync(days, timeProvider.GetLocalNow().DateTime);
        return WithJob(new DataRetentionRow
        {
            Key = "token-usage",
            Label = "Token 用量",
            Note = "連同原始明細檔",
            Count = stats.Total,
            Folder = await folder,
            OldestLocal = stats.Oldest,
            RuleText = days > 0 ? $"保留 {days} 天" : "不自動刪除（保留天數為 0）",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            ManageUrl = "/token-usage",
            CanCustomPurge = true,
        }, TokenUsageLogRetentionJob.JobName, jobByName.GetValueOrDefault(TokenUsageLogRetentionJob.JobName), days > 0);
    }

    private async Task<DataRetentionRow> AiCallLogRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName, Task<FolderUsage> folder)
    {
        var stats = await aiCallLogService.GetRetentionStatsAsync(timeProvider.GetLocalNow().DateTime);
        return WithJob(new DataRetentionRow
        {
            Key = "ai-call-log",
            Label = "AI 對話紀錄",
            Note = "連同內容檔",
            Count = stats.Total,
            Folder = await folder,
            OldestLocal = stats.Oldest,
            RuleText = $"保留 {aiCallLogService.RetentionDays} 天",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            ManageUrl = "/ai-call-logs",
            CanCustomPurge = true,
        }, AiCallLogRetentionJob.JobName, jobByName.GetValueOrDefault(AiCallLogRetentionJob.JobName), ruleActive: true);
    }

    private async Task<DataRetentionRow> AuditLogRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName, Task<FolderUsage> folder)
    {
        var days = logRetention.CurrentValue.AuditLogDays;
        var stats = await auditLogQueryService.GetRetentionStatsAsync(days > 0 ? RetentionCutoffs.AuditLog(days, timeProvider) : null);
        return WithJob(new DataRetentionRow
        {
            Key = "audit-log",
            Label = "稽核紀錄",
            Note = "連同詳細內容檔",
            Count = stats.Total,
            Folder = await folder,
            OldestLocal = ToLocal(stats.Oldest),
            RuleText = days > 0 ? $"保留 {days} 天" : "不自動刪除（保留天數為 0）",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            ManageUrl = "/audit-logs",
            CanCustomPurge = true,
        }, AuditLogRetentionJob.JobName, jobByName.GetValueOrDefault(AuditLogRetentionJob.JobName), days > 0);
    }

    private async Task<DataRetentionRow> NotificationRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName)
    {
        var settings = notification.CurrentValue;
        var (days, readDays) = (settings.RetentionDays, settings.ReadRetentionDays);
        var stats = await notificationQueryService.GetRetentionStatsAsync(
            days > 0 ? RetentionCutoffs.Notification(days, timeProvider) : null,
            readDays > 0 ? RetentionCutoffs.Notification(readDays, timeProvider) : null);
        var rules = new List<string>();
        if (days > 0)
        {
            rules.Add($"建立超過 {days} 天刪除");
        }

        if (readDays > 0)
        {
            rules.Add($"已讀超過 {readDays} 天刪除");
        }

        return WithJob(new DataRetentionRow
        {
            Key = "notification",
            Label = "站內通知",
            Note = "全體使用者；個人在鈴鐺裡自行刪除",
            Count = stats.Total,
            OldestLocal = ToLocal(stats.Oldest),
            RuleText = rules.Count > 0 ? string.Join("；", rules) : "不自動刪除（保留天數皆為 0）",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            CanCustomPurge = true,
        }, NotificationRetentionJob.JobName, jobByName.GetValueOrDefault(NotificationRetentionJob.JobName), rules.Count > 0);
    }

    private async Task<DataRetentionRow> BackupRowAsync(Dictionary<string, ScheduledJobOverviewItem> jobByName, Task<FolderUsage> folder)
    {
        var keep = backup.CurrentValue.KeepCount;
        var files = backupStore.List();
        var job = jobByName.GetValueOrDefault(SystemBackupJob.JobName);
        return new DataRetentionRow
        {
            Key = "backup",
            Label = "系統備份",
            Count = files.Count,
            CountUnit = "份",
            Folder = await folder,
            OldestLocal = files.Count > 0 ? files.Min(x => x.CreatedAtLocal) : null,
            RuleText = keep > 0 ? $"只留最新 {keep} 份" : "不自動刪除舊備份（保留份數為 0）",
            ParameterGroup = SystemParameterCatalog.GroupBackup,
            Expected = keep > 0 ? Math.Max(0, files.Count + 1 - keep) : null,
            ExpectedNote = keep > 0 ? "下一次備份成功後" : null,
            JobName = SystemBackupJob.JobName,
            Job = job,
            ScheduleText = job is null ? "—" : CronDescriber.Describe(job.Cron),
            ManageUrl = "/backups",
        };
    }

    private async Task<DataRetentionRow> JobRunRowAsync()
    {
        var days = scheduledJob.CurrentValue.JobRunRetentionDays;
        var stats = await jobRunService.GetRetentionStatsAsync(days > 0 ? RetentionCutoffs.JobRun(days, timeProvider) : null);
        return new DataRetentionRow
        {
            Key = "job-run",
            Label = "排程執行紀錄",
            Count = stats.Total,
            OldestLocal = ToLocal(stats.Oldest),
            RuleText = days > 0 ? $"保留 {days} 天" : "不自動刪除（保留天數為 0）",
            ParameterGroup = SystemParameterCatalog.GroupRetention,
            Expected = stats.Expired,
            ScheduleText = "每個排程作業執行完時",
            ManageUrl = "/scheduled-jobs",
            CanCustomPurge = true,
        };
    }

    private async Task<DataRetentionRow> NLogRowAsync(string directory, Task<FolderUsage> folder)
    {
        var usage = await folder;
        int? expired = null;
        DateTime? oldest = null;
        if (usage.Status is FolderUsageStatus.Ok or FolderUsageStatus.Partial)
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToList();
            oldest = files.Count > 0 ? files.Min(x => x.LastWriteTime) : null;
            var cutoff = timeProvider.GetLocalNow().DateTime.AddDays(-NLogRetention.MaxArchiveDays);
            expired = files.Count(x => x.LastWriteTime < cutoff);
        }

        return new DataRetentionRow
        {
            Key = "nlog",
            Label = "日誌檔",
            Note = "只能改 nlog.config",
            Count = usage.Files,
            CountUnit = "個檔案",
            Folder = usage,
            OldestLocal = oldest,
            RuleText = $"保留 {NLogRetention.MaxArchiveDays} 天",
            Expected = expired,
            ExpectedNote = expired > 0 ? "約略；由 NLog 每天換檔時刪除" : null,
            ScheduleText = "NLog 每天換檔時",
            ManageUrl = "/logs",
        };
    }

    private async Task<DataRetentionRow> AnnouncementRowAsync()
    {
        var items = await announcementService.GetAllAsync();
        return new DataRetentionRow
        {
            Key = "announcement",
            Label = "公告",
            Count = items.Count,
            CountUnit = "則",
            OldestLocal = items.Count > 0 ? items.Min(x => x.StartAt) : null,
            RuleText = "不自動刪除（過了結束時間只是不再顯示）",
            ManageUrl = "/announcements",
        };
    }

    /// <summary>
    /// 填上排程欄與「立即清理」：保留規則為 0（作業本身也會直接跳過）、作業不存在、已在佇列或執行中時不能按。
    /// 作業停用或總開關關閉時照樣能按（同「排程作業」頁的「立即執行」）。
    /// </summary>
    private static DataRetentionRow WithJob(DataRetentionRow row, string jobName, ScheduledJobOverviewItem? job, bool ruleActive)
    {
        string? reason = (job, ruleActive) switch
        {
            (null, _) => "找不到負責清理的排程作業",
            (_, false) => "保留天數為 0，不自動清理",
            ({ IsQueued: true }, _) => "已在佇列或執行中",
            ({ LastRun.Status: JobRunStatuses.Running }, _) => "已在佇列或執行中",
            _ => null,
        };

        return row with
        {
            JobName = jobName,
            Job = job,
            ScheduleText = job is null ? "—" : CronDescriber.Describe(job.Cron),
            CanPurgeNow = reason is null,
            PurgeDisabledReason = reason,
        };
    }

    private Task<FolderUsage> MeasureAsync(string? path, CancellationToken cancellationToken)
        => Task.Run(() => DirectorySizeProbe.Measure(path, cancellationToken));

    private static bool IsLogFile(FileInfo file) => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase);

    private DateTime? ToLocal(DateTime? utc) => utc is { } value ? jobOverviewService.ToLocal(value) : null;
}

/// <summary>
/// 日誌檔的保留天數。NLog 自己刪舊檔（<c>nlog.config</c> 的 <c>maxArchiveDays</c>），應用程式讀不到那個值，
/// 這裡抄一份給「資料保留與清理」頁顯示；<c>NLogRetentionTests</c> 比對兩邊，改了 nlog.config 沒改這裡會紅。
/// </summary>
public static class NLogRetention
{
    public const int MaxArchiveDays = 30;
}
