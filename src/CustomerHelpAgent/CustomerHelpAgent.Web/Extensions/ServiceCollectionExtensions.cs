using AntDesign;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Repositories;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Backup;
using CustomerHelpAgent.Web.Caching;
using CustomerHelpAgent.Web.Components.Layout;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Validation;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Web.Health;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Localization;
using CustomerHelpAgent.Web.Dashboard;
using CustomerHelpAgent.Web.DataRetention;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;
using System.Globalization;
using System.Threading.Channels;
using System.Threading.RateLimiting;

namespace CustomerHelpAgent.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredLocalization(this IServiceCollection services)
    {
        services.AddLocalization();

        // 只支援繁體中文（0.9.136 起拿掉 en-US）：英文瀏覽器也拿到 zh-TW。
        var supportedCultures = new[]
        {
            new CultureInfo("zh-TW")
        };

        var defaultCulture = supportedCultures[0];

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture(defaultCulture);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;

            options.RequestCultureProviders = new List<IRequestCultureProvider>
            {
                new AcceptLanguageHeaderRequestCultureProvider()
            };
        });

        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = defaultCulture.Name;

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<SystemStartupState>();
        services.AddSingleton<LogLevelRuntimeState>();
        services.AddScoped<INLogFilePathResolver, NLogFilePathResolver>();
        services.AddScoped<ILogQueryService, LogQueryService>();
        services.AddScoped<IAiChatCompletionClient, AiChatCompletionClient>();
        services.AddScoped<IAiLogAnalysisService, AiLogAnalysisService>();
        services.AddScoped<IAiSystemPromptProvider, AiSystemPromptProvider>();
        services.AddScoped<IAiExceptionAnalysisService, AiExceptionAnalysisService>();
        services.AddScoped<IAiHealthProbe, AiHealthProbe>();
        services.AddScoped<AgentChatClientFactory>();

        // 全專案第一個 AddHttpClient。刻意用 **named client** 而非 typed client：
        // 1. AddHttpClient<IAiLogAnalysisService, AiLogAnalysisService>() 會把服務註冊成
        //    Transient，與本方法「一律 AddScoped」的慣例不一致，讀註冊表的人會被誤導。
        // 2. 在 Blazor Server，DI scope 等於 SignalR circuit（可存活數小時）。typed client
        //    會讓同一個 HttpClient 實例活在整個 circuit 上，等於架空 HandlerLifetime 的
        //    輪替機制（DNS 變更吃不到）。named client 是每次呼叫才 CreateClient，
        //    handler 仍由工廠池化。
        // 3. retry 刻意不做：這是使用者主動觸發的單次動作，失敗讓他再按一次即可。
        //    自動重試只會讓成本加倍，還會掩蓋「設定錯誤」這種重試永遠不會好的問題。
        services.AddHttpClient(AiChatCompletionClient.HttpClientName, (serviceProvider, client) =>
        {
            // 在 CreateClient 時（而非註冊時）讀設定，這樣改 appsettings 不必重啟，
            // 也不會像急切讀取那樣讓測試的組態覆寫失效。
            var settings = serviceProvider.GetRequiredService<IOptionsMonitor<AiSettings>>().CurrentValue;
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120);
        });
        services.AddScoped<IDatabaseUsageService, DatabaseUsageService>();
        services.AddScoped<IHealthLogReader, HealthLogReader>();
        services.AddScoped<ISystemHealthService, SystemHealthService>();
        services.AddScoped<NotificationTestService>();
        services.AddScoped<AuthenticationStateHelper>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        // 稽核的長內容（修改差異、查詢條件）寫成檔案（0.9.131 起）。
        services.AddScoped<AuditContentFileStore>();
        // 稽核來源 IP（0.9.117 起）：circuit handler 寫入、AuditLogService 讀取，兩者必須拿到同一個實例。
        services.AddScoped<ClientIpProvider>();
        services.AddScoped<IClientIpProvider>(sp => sp.GetRequiredService<ClientIpProvider>());
        // 站內通知與公告（0.9.100 起）。訊號與公告快取是 singleton（跨連線共用）；發送服務是 scoped。
        services.AddSingleton<INotificationSignal, NotificationSignal>();
        services.AddSingleton<AnnouncementCache>();
        services.AddScoped<INotificationMailer, NotificationMailer>();
        services.AddScoped<INotificationSender, NotificationSender>();
        services.AddScoped<NotificationQueryService>();
        services.AddScoped<AnnouncementService>();

        // AI 提示詞（0.9.108 起）。
        services.AddScoped<PromptTemplateService>();
        // 個人資料頁（0.9.102 起）。
        services.AddScoped<ProfileService>();
        // 系統名稱與簡介的唯一讀取入口（0.9.98 起，可在「系統參數」頁修改，讀到的永遠是目前的值）。
        services.AddSingleton<ISystemIdentity, SystemIdentity>();
        services.AddScoped<ITotpService, TotpService>();
        // 兩步驟驗證（0.9.104 起）。
        services.AddSingleton<ITwoFactorSecretProtector, DataProtectionTwoFactorSecretProtector>();
        services.AddScoped<ITwoFactorService, TwoFactorService>();
        services.AddSingleton<TwoFactorLoginCookies>();
        services.AddScoped<IRbacBackfillService, RbacBackfillService>();

        // 啟動時的資料庫準備（0.9.91 起取代 Program.cs 的 migrate 與 seed）。
        // 衍生專案的種子資料：實作 IDatabaseSeeder 並在這裡註冊成 Scoped（Order 不可重複）。
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddOptions<DatabaseInitializerOptions>();
        services.AddScoped<IDatabaseSeeder, DefaultRoleViewSeeder>();
        services.AddScoped<IDatabaseSeeder, SupportUserSeeder>();
        services.AddScoped<IDatabaseSeeder, RbacBackfillSeeder>();
        services.AddScoped<IDatabaseSeeder, LegacySystemPromptSeeder>();
        // 售後展示的示範帳號與商品、訂單、規則（DemoSettings:Enabled 時才寫入）。
        services.AddScoped<IDatabaseSeeder, DemoAccountSeeder>();
        services.AddScoped<IDatabaseSeeder, DemoCatalogSeeder>();

        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IRbacWriteService, RbacWriteService>();
        services.AddScoped<IEffectiveTeamResolver, EffectiveTeamResolver>();
        services.AddSingleton<ITeamTreeCache, TeamTreeCache>();
        // 密碼原則（0.9.101 起）：所有設定密碼的路徑都經過它；只讀 IOptionsMonitor 與時鐘，所以是 singleton。
        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();
        // 工作階段失效（0.9.103 起）：版本讀取有快取，所以是 singleton；Cookie 驗證器每個請求一個。
        services.AddSingleton<ISecurityStampService, SecurityStampService>();
        services.AddScoped<SecurityStampCookieEvents>();
        services.AddSingleton<SessionRefreshTicketService>();
        services.AddScoped<SessionRefreshNavigator>();
        services.AddScoped<MyUserServiceLogin>();
        services.AddScoped<ExternalLoginService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<SidebarMenuService>();
        services.AddScoped<PageHelpService>();
        services.AddScoped<RolePermissionService>();
        services.AddScoped<RoleViewService>();
        services.AddScoped<MyUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        // 專案附件實體檔的唯一刪除入口（0.9.97 起，含根目錄檢查）。
        services.AddScoped<ProjectFileStore>();
        // 系統備份（0.9.99 起）：檔案清單與建立備份。建立備份只由排程作業 SystemBackup 呼叫（含「立即備份」）。
        services.AddSingleton<BackupStore>();
        services.AddScoped<SystemBackupService>();
        services.AddScoped<ProjectService>();
        // 依保留天數永久刪除已軟刪除的資料（0.9.97 起，系統層級、不套團隊範圍；由排程作業呼叫）。
        services.AddScoped<SoftDeletePurgeService>();
        services.AddScoped<ProjectRepository>();
        services.AddScoped<CategoryService>();
        services.AddScoped<AfterSalesPolicyService>();
        services.AddScoped<ShopOrderService>();
        // 售後申請（1.0.3 起）：照片實體檔一律經 AfterSalesFileStore（含根目錄檢查）；埋點失敗不中斷流程。
        services.AddScoped<AfterSalesFileStore>();
        services.AddScoped<AfterSalesRequestService>();
        services.AddScoped<ProcessMetricService>();
        services.AddScoped<CategoryRepository>();
        services.AddScoped<TeamService>();
        services.AddScoped<TeamRepository>();

        // 稽核紀錄的「讀」端。寫端是 Services/Other 的 AuditLogService（0.9.131 起也改用 IDbContextFactory，
        // 每筆稽核各用一個 context）；讀端只服務 Blazor 畫面（見 DataAccessServiceLifetimeTests）。
        services.AddScoped<AuditLogQueryService>();

        #region 系統例外紀錄
        // 記錄管線：ILoggerProvider（生產）→ 有界 Channel → ExceptionLogWriter（消費）→ ExceptionLogService。
        // Channel 有界且滿載即丟棄：寧可漏記，也不能讓例外記錄拖垮正在等待的使用者。
        // 與 nlog.config 的 AsyncWrapper overflowAction="Discard" 同一種取捨。
        // ⚠️ FullMode 用 Wait 搭配 provider 的 TryWrite（不阻塞），**不要改回 DropWrite**：
        // Drop 系列模式下 TryWrite 永遠回傳 true，丟棄筆數永遠是 0（0.9.77 修正，與 ChannelEmailQueue 同理）。
        var exceptionChannel = Channel.CreateBounded<ExceptionLogEntry>(
            new BoundedChannelOptions(ExceptionLogProvider.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        services.AddSingleton(exceptionChannel.Reader);
        services.AddSingleton(exceptionChannel.Writer);
        services.AddSingleton<ExceptionContextAccessor>();
        services.AddScoped<ExceptionStackFileStore>();
        services.AddScoped<ExceptionLogService>();
        services.AddHostedService<ExceptionLogWriter>();

        // 程序層級的未處理例外（射後不理的 Task、背景執行緒）。由 Program.cs 在 Build 後呼叫 Register。
        services.AddSingleton<ProcessExceptionHooks>();

        // 例外 Email 告警（LOG-12）。單例：暴增計數、冷卻與每小時配額都是記憶體狀態。
        services.AddSingleton<ExceptionAlertService>();

        // 日誌管線自我監控（LOG-22）與瀏覽器錯誤回報（LOG-20，每個 circuit 一份）。
        services.AddSingleton<LoggingPipelineMonitor>();
        services.AddScoped<BrowserErrorReporter>();

        // 以 DI 註冊 ILoggerProvider，讓它拿得到 Channel 與情境存取器。
        // ⚠️ 必須晚於 Program.cs 的 builder.Logging.ClearProviders()，否則會被清掉；
        // 本方法由 AddApplicationServices 呼叫，時序在其後，NLog 與本 provider 並存。
        services.AddSingleton<ILoggerProvider, ExceptionLogProvider>();
        #endregion

        #region Token 用量紀錄
        // 不需要佇列與背景寫入器：LLM 呼叫是使用者主動觸發、一次數秒到數分鐘，
        // 多一次幾毫秒的資料庫寫入可忽略（與例外紀錄「短時間重複數百次」的情境不同）。
        services.AddScoped<TokenUsageRawStore>();
        // 費用計算器：由 TokenUsageLogService 在寫入前呼叫，呼叫端不必知道它的存在。
        services.AddScoped<IAiUsageCostCalculator, AiUsageCostCalculator>();
        services.AddScoped<TokenUsageLogService>();
        // AI 用量上限（0.9.109 起）：送出前檢查（AiChatCompletionClient）、記帳後提醒（TokenUsageLogService）。
        services.AddScoped<IAiQuotaService, AiQuotaService>();
        // 轉發到同一個實例：呼叫端只依賴 ITokenUsageRecorder（只有記錄），
        // 頁面才用得到完整的 TokenUsageLogService（查詢、統計、刪除）。
        services.AddScoped<ITokenUsageRecorder>(sp => sp.GetRequiredService<TokenUsageLogService>());
        #endregion

        #region AI 對話紀錄
        // 與 Token 用量同一個模式：呼叫端只依賴 IAiCallLogRecorder（只有記錄），
        // 頁面才用得到完整的 AiCallLogService（查詢、明細、刪除）。內文只准進這裡（速查 §6.7）。
        services.AddScoped<AiCallLogFileStore>();
        services.AddScoped<AiCallLogService>();
        services.AddScoped<IAiCallLogRecorder>(sp => sp.GetRequiredService<AiCallLogService>());
        #endregion

        #region 排程作業（0.9.96 起）
        services.AddScheduledJobs();
        #endregion

        services.AddHttpContextAccessor();
        services.AddScoped<IRecordAccessScopeProvider, RecordAccessScopeProvider>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CustomerHelpAgent.Web.Components.ApplicationCircuitHandler>();

        return services;
    }

    /// <summary>
    /// 排程作業（0.9.96 起）：框架的服務、四個內建清理作業與排程器。
    /// 週期性的背景工作一律以 <see cref="ScheduledJobServiceCollectionExtensions.AddScheduledJob{TJob}"/> 註冊在這裡（速查表「排程作業」），
    /// 不要再自己寫 BackgroundService 計時器。獨立成一個方法：設定驗證器需要作業清單，測試的設定驗證也呼叫它。
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services)
    {
        // TimeProvider 讓測試能以假時鐘驗證排程時間與保留天數的門檻。
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScheduledJobRunService>();
        services.AddSingleton<JobLockProvider>();
        services.AddSingleton<ScheduledJobTriggerQueue>();
        services.AddSingleton<ScheduledJobRunner>();
        services.AddSingleton<ScheduledJobOverviewService>();

        // 「資料保留與清理」頁（0.9.133 起）：用到的資料服務都是 scoped，所以它也是 scoped。
        services.AddScoped<DataRetentionOverviewService>();

        // 首頁儀表板的小工具（0.9.106 起）；新增小工具在 AddDashboard 加一行 AddDashboardWidget。
        services.AddDashboard();

        services.AddScheduledJob<AuditLogRetentionJob>(
            AuditLogRetentionJob.JobName, "稽核紀錄清理",
            "刪除超過保留天數的稽核紀錄（保留天數在「系統參數」頁設定，預設 365 天，0 = 不清除）。", "0 3 * * *");
        services.AddScheduledJob<ExceptionLogRetentionJob>(
            ExceptionLogRetentionJob.JobName, "系統例外紀錄清理",
            "刪除超過保留天數的系統例外紀錄與堆疊檔（保留天數在「系統參數」頁設定，預設 90 天，0 = 不清除）。", "0 3 * * *");
        services.AddScheduledJob<AiCallLogRetentionJob>(
            AiCallLogRetentionJob.JobName, "AI 對話紀錄清理",
            "刪除超過保留天數的 AI 對話紀錄與內容檔（保留天數在「系統參數」頁設定，預設 90 天）。", "0 3 * * *");
        services.AddScheduledJob<TokenUsageLogRetentionJob>(
            TokenUsageLogRetentionJob.JobName, "Token 用量紀錄清理",
            "刪除超過保留天數的 Token 用量紀錄與原始檔（保留天數在「系統參數」頁設定，預設 365 天，0 = 不清除）。", "0 3 * * *");
        services.AddScheduledJob<SoftDeletePurgeJob>(
            SoftDeletePurgeJob.JobName, "已刪除資料清理",
            "已刪除超過保留天數的專案（含附件檔）、分類、團隊、使用者與角色，永久刪除（保留天數在「系統參數」頁設定，預設 90 天，0 = 不清除）。", "0 3 * * *");

        services.AddScheduledJob<NotificationRetentionJob>(
            NotificationRetentionJob.JobName, "站內通知清理",
            "刪除建立超過保留天數的站內通知（不論已讀未讀，預設 90 天），以及標為已讀超過已讀保留天數的通知（預設 7 天）；兩者都在「系統參數」頁設定，0 = 不清除。", "0 3 * * *");
        services.AddScheduledJob<SystemBackupJob>(
            SystemBackupJob.JobName, "系統備份",
            "備份資料庫、專案附件、例外堆疊檔、Token 原始檔、稽核內容檔與金鑰環到備份目錄，並依保留份數刪除較舊的備份（份數在「系統參數」頁設定，預設 7 份）。", "0 2 * * *");
        services.AddScheduledJob<PasswordExpiryReminderJob>(
            PasswordExpiryReminderJob.JobName, "密碼到期提醒",
            "密碼在 7 天內到期的使用者各收到一則站內通知（密碼有效天數在「系統參數」頁設定，預設 0 = 不過期，此時不動作）。", "0 8 * * *");

        // ⚠️ 必須註冊在 ExceptionLogWriter 之後：主機以相反順序停止，作業在關機時記的錯誤才還有人寫進系統例外紀錄。
        services.AddHostedService<JobSchedulerWorker>();
        return services;
    }

    public static IServiceCollection AddConfiguredOptions(this IServiceCollection services, IConfiguration configuration)
    {
        // 0.9.92 起每個從設定檔綁定的類別都在啟動時驗證（不再有單純的 Configure<T>）：
        // 設定矛盾、拼錯、格式錯誤一律拒絕啟動，而不是帶著錯的值跑、等使用者操作時才失敗或靜默失效。
        // 規則在 Configuration/Validation/ 各類別的 IValidateOptions；Program.cs 會在資料庫初始化之前就執行驗證。
        // 沒有欄位規則的類別（Security、Swagger）也要 ValidateOnStart：型別錯誤（bool 填了 "yes"）才會在啟動時就失敗。
        services.AddValidatedOptions<SystemSettings, SystemSettingsValidator>(configuration, nameof(SystemSettings));
        services.AddValidatedOptions<BootstrapSettings, BootstrapSettingsValidator>(configuration, nameof(BootstrapSettings));
        services.AddValidatedOptions<CorsSettings, CorsSettingsValidator>(configuration, CorsSettings.SectionName);
        services.AddValidatedOptions<CacheSettings, CacheSettingsValidator>(configuration, CacheSettings.SectionName);
        services.AddValidatedOptions<RateLimitSettings, RateLimitSettingsValidator>(configuration, RateLimitSettings.SectionName);
        services.AddValidatedOptions<AiSettings, AiSettingsValidator>(configuration, AiSettings.SectionName);
        services.AddValidatedOptions<AiPricingSettings, AiPricingSettingsValidator>(configuration, AiPricingSettings.SectionName);
        services.AddValidatedOptions<ForwardedHeadersSettings, ForwardedHeadersSettingsValidator>(configuration, ForwardedHeadersSettings.SectionName);
        services.AddValidatedOptions<ScheduledJobSettings, ScheduledJobSettingsValidator>(configuration, ScheduledJobSettings.SectionName);
        services.AddValidatedOptions<DemoSettings, DemoSettingsValidator>(configuration, DemoSettings.SectionName);
        services.AddOptions<SecuritySettings>().Bind(configuration.GetSection(SecuritySettings.SectionName)).ValidateOnStart();
        services.AddOptions<SwaggerSettings>().Bind(configuration.GetSection(SwaggerSettings.SectionName)).ValidateOnStart();

        // 保留天數寫壞（0 或超過 3650）就啟動失敗，不要讓自動過期悄悄用錯的門檻刪資料。
        services.AddOptions<SlowOperationSettings>()
            .Bind(configuration.GetSection(SlowOperationSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ClientErrorReportingSettings>()
            .Bind(configuration.GetSection(ClientErrorReportingSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LogRetentionSettings>()
            .Bind(configuration.GetSection(LogRetentionSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AiCallLogSettings>()
            .Bind(configuration.GetSection(AiCallLogSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SoftDeleteSettings>()
            .Bind(configuration.GetSection(SoftDeleteSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<NotificationSettings>()
            .Bind(configuration.GetSection(NotificationSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AiQuotaSettings>()
            .Bind(configuration.GetSection(AiQuotaSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<BackupSettings>()
            .Bind(configuration.GetSection(BackupSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PasswordPolicySettings>()
            .Bind(configuration.GetSection(PasswordPolicySettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LockoutSettings>()
            .Bind(configuration.GetSection(LockoutSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TwoFactorSettings>()
            .Bind(configuration.GetSection(TwoFactorSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>綁定設定區段、在啟動時驗證，並註冊該類別的驗證器。</summary>
    public static IServiceCollection AddValidatedOptions<TOptions, TValidator>(this IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(configuration.GetSection(sectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        return services;
    }

    /// <summary>
    /// 資料庫註冊。
    ///
    /// ⚠️ **以 <c>IDbContextFactory</c> 為主**，不要改回單純的 <c>AddDbContext</c>：
    /// 在 Blazor Server，DI scope ＝ SignalR circuit，存活時間等同使用者整個連線
    /// （數分鐘到數小時），不是一次 HTTP 請求。scoped 的 DbContext 會導致
    /// 追蹤實體只增不減（記憶體成長、讀到過期資料），兩個元件事件重疊時還會炸
    /// 「A second operation was started on this context」。
    ///
    /// Blazor 路徑的 <c>Services/DataAccess/*</c> 一律注入工廠、每個方法用完即棄。
    /// 仍保留一個 scoped 的 <see cref="BackendDBContext"/> 供 Repository（API 路徑，
    /// scope ＝ 單次 HTTP 請求，本來就正確）與健康檢查／診斷服務使用，
    /// 但它改由工廠產生，避免 options 生命週期與工廠衝突。
    /// </summary>
    public static IServiceCollection AddConfiguredDatabase(this IServiceCollection services, SystemSettings systemSettings)
    {
        services.AddDbContextFactory<BackendDBContext>((sp, options) =>
        {
            // Foreign Keys 明確開啟：目前的原生程式庫（e_sqlite3）預設就是開啟，但不能靠預設 ——
            // 日後換原生程式庫時若預設變成關閉，所有 FK 的 Restrict／Cascade 會靜默失效。
            // 刻意不加 busy_timeout：Microsoft.Data.Sqlite 遇到 SQLITE_BUSY 會自己重試到命令逾時（預設 30 秒）。
            // 兩者都由 SqliteBehaviorTests 釘住。WAL 存在資料庫檔內，由 DatabaseInitializer 設定一次。
            var sqliteConnectionString = new SqliteConnectionStringBuilder(
                MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath))
            {
                ForeignKeys = true,
            }.ToString();
            options.UseSqlite(sqliteConnectionString);

            // 慢資料庫指令（LOG-21）：只記指令類型與耗時，不記 SQL 與參數。
            options.AddInterceptors(sp.GetRequiredService<SlowDbCommandInterceptor>());
        });
        services.AddSingleton<SlowDbCommandInterceptor>();

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContext());

        return services;
    }

    /// <summary>
    /// 把 Data Protection 的金鑰環固定到檔案系統，並固定應用程式識別名稱。
    ///
    /// <para>⚠️ **沒有這一段，登入就存不住。** 登入 Cookie 是用 Data Protection 的金鑰
    /// 加密的；不設定時金鑰預設落在使用者設定檔下，而 IIS 的應用程式集區若未載入使用者
    /// 設定檔，就會退化成「只存在記憶體」—— 每次回收都換一批金鑰，舊 Cookie 全部解不開，
    /// 全站使用者被登出。0.9.39 之前正是這個狀態。</para>
    ///
    /// <para>⚠️ <c>SetApplicationName</c> 同樣不可省略：不設定時判別子取自 content root
    /// 路徑，**換一個部署目錄就等於換一組金鑰用途**，既有 Cookie 一樣失效。</para>
    ///
    /// <para>⚠️ Windows 上金鑰檔預設以 DPAPI 加密，因此 **IIS 應用程式集區的識別身分
    /// 換掉之後，金鑰檔一樣會解不開**。部署注意事項見正式部署與安全檢查清單。</para>
    /// </summary>
    public static IServiceCollection AddConfiguredDataProtection(
        this IServiceCollection services, SystemSettings systemSettings)
    {
        var keyPath = systemSettings.ExternalFileSystem.DataProtectionKeyPath;
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new InvalidOperationException(
                "SystemSettings:ExternalFileSystem:DataProtectionKeyPath 不可為空白 —— "
                + "留空會讓金鑰退回框架預設位置，在 IIS 上等同每次回收就把所有人登出。");
        }

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
            .SetApplicationName(MagicObjectHelper.DataProtectionApplicationName);

        return services;
    }

    public static IServiceCollection AddConfiguredCache(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CacheSettings.SectionName).Get<CacheSettings>() ?? new CacheSettings();

        switch (settings.GetProvider())
        {
            case CacheProvider.Redis:
                if (string.IsNullOrWhiteSpace(settings.RedisConnection))
                {
                    throw new InvalidOperationException("CacheSettings:RedisConnection 不可為空白。");
                }

                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = settings.RedisConnection;
                    options.InstanceName = settings.InstanceName;
                });
                break;

            case CacheProvider.Memory:
                services.AddDistributedMemoryCache();
                break;

            default:
                throw new InvalidOperationException($"不支援的快取 provider：{settings.Provider}");
        }

        services.AddSingleton<ICacheService, DistributedCacheService>();

        return services;
    }

    /// <summary>
    /// 寄信服務：None／Pickup／Smtp 三選一，外加背景寄送佇列。
    ///
    /// ⚠️ **provider 在解析 <see cref="IEmailSender"/> 時才決定**，不像 <see cref="AddConfiguredCache"/>
    /// 在註冊當下 switch：註冊時讀設定會讓 <c>WebApplicationFactory</c> 子類的組態覆寫失效
    /// （它們在 Build 之後才套用），測試就無法切換 provider。
    /// </summary>
    public static IServiceCollection AddConfiguredEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailSettings>()
            .Bind(configuration.GetSection(EmailSettings.SectionName))
            .ValidateDataAnnotations()
            .Validate(s => s.TryGetProvider(out _), "EmailSettings:Provider 只接受 None、Pickup 或 Smtp。")
            .Validate(s => s.TryGetSecurity(out _), "EmailSettings:Security 只接受 Auto、None、StartTls 或 SslOnConnect。")
            .Validate(s => s.HasRequiredSmtpFields(), "EmailSettings 使用 Smtp 時，Host 不可留空、FromAddress 必須是有效的 Email。")
            .Validate(
                s => string.IsNullOrWhiteSpace(s.PublicBaseUrl) || OptionsErrors.IsHttpUrl(s.PublicBaseUrl),
                "EmailSettings:PublicBaseUrl 有填時必須是以 http:// 或 https:// 開頭的完整網址（信件裡的連結以它為開頭）。")
            .ValidateOnStart();

        // 例外告警（0.9.78 起，LOG-12）。收件人為空即停用；實際寄出仍要 Provider 不是 None。
        services.AddOptions<ExceptionAlertSettings>()
            .Bind(configuration.GetSection(ExceptionAlertSettings.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                s => s.Recipients.Where(x => !string.IsNullOrWhiteSpace(x)).All(x => System.Net.Mail.MailAddress.TryCreate(x.Trim(), out _)),
                "ExceptionAlertSettings:Recipients 每一項都必須是有效的 Email（寫錯的收件人要到寄告警信時才會失敗，而且只留在日誌裡）。")
            .ValidateOnStart();

        // 忘記密碼的時效（0.9.60 起）。類別在 Models（Business 要讀），驗證跟著寄信一起註冊。
        services.AddOptions<PasswordResetSettings>()
            .Bind(configuration.GetSection(PasswordResetSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<NullEmailSender>();
        services.AddScoped<PickupEmailSender>();
        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<IEmailSender>(sp =>
            sp.GetRequiredService<IOptionsMonitor<EmailSettings>>().CurrentValue.GetProvider() switch
            {
                EmailProvider.Pickup => sp.GetRequiredService<PickupEmailSender>(),
                EmailProvider.Smtp => sp.GetRequiredService<SmtpEmailSender>(),
                _ => sp.GetRequiredService<NullEmailSender>(),
            });

        // 佇列是 singleton（全站共用一條），消費端是背景服務；兩者都不持有 scoped 服務，
        // 寄送時才由 EmailDispatchWorker 逐封建立 scope 解析 IEmailSender。
        services.AddSingleton<ChannelEmailQueue>();
        services.AddSingleton<IEmailQueue>(sp => sp.GetRequiredService<ChannelEmailQueue>());
        services.AddHostedService<EmailDispatchWorker>();

        services.AddScoped<IEmailHealthProbe, EmailHealthProbe>();
        services.AddScoped<EmailTestService>();

        return services;
    }

    public static IServiceCollection AddConfiguredCors(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
        services.AddCors(options =>
        {
            options.AddPolicy("ConfiguredCors", policy =>
            {
                if (settings.AllowedOrigins.Length == 0)
                {
                    policy.SetIsOriginAllowed(_ => false);
                    return;
                }

                policy
                    .WithOrigins(settings.AllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 被限流時也要維持 ApiResult 信封（專案不變量：Web API 回應一律包信封）。
            // 另一個作用是「先把 body 寫掉」—— 否則空 body 的 429 會被
            // UseStatusCodePagesWithReExecute 拿原始的 POST + JSON 去重跑 /not-found，
            // 那是 Blazor 頁面、會被 antiforgery 擋下，最後回給呼叫端的是 400 HTML。
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json; charset=utf-8";

                var payload = ApiResult.FailureResult("請求過於頻繁，請稍後再試。", StatusCodes.Status429TooManyRequests);
                await context.HttpContext.Response.WriteAsJsonAsync(payload, cancellationToken);
            };

            // ⚠️ 一定要用 AddPolicy + PartitionedRateLimiter，不要用 AddFixedWindowLimiter。
            // 後者是**全站共用的單一計數器**：任一呼叫端每分鐘打滿配額，
            // 其他所有使用者都會拿到 429（0.4.34 之前正是如此）。
            //
            // ⚠️ 登入的較嚴格配額**寫在同一個 policy 裡**，而不是用
            // [EnableRateLimiting("login")] 屬性 —— 端點慣例（MapControllers().RequireRateLimiting("api")）
            // 套用時機晚於屬性，會把屬性指定的 policy 蓋掉，導致登入配額靜默失效。
            // 這一點單靠測試看不出來（兩者都回 401），是實跑才發現的。
            options.AddPolicy("api", context =>
            {
                // ⚠️ 在**請求時**才讀設定，不要在註冊時急切讀取：
                // 那樣不但無法支援設定重載，連 WebApplicationFactory 的測試覆寫都會失效
                // （它套用組態的時機晚於服務註冊）。
                var settings = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitSettings>>()
                    .CurrentValue;

                var isLogin = IsLoginRequest(context);
                var permitLimit = isLogin ? settings.LoginRequestsPerMinute : settings.ApiRequestsPerMinute;

                var partitionKey = BuildRateLimitPartitionKey(isLogin, permitLimit, ResolvePartitionKey(context));

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });
        });

        return services;
    }

    /// <summary>
    /// 限流計數器的鍵。前綴讓登入與一般 API 各自計數，不會互相消耗配額。
    ///
    /// ⚠️ 鍵含上限值（0.9.98 起，上限可在「系統參數」頁修改）：limiter 依鍵快取，工廠只在第一次建立時呼叫，
    /// 不含上限值的話持續呼叫的用戶端會一直沿用舊上限。改了上限就換一個新的計數器，舊的閒置後由框架回收。
    /// </summary>
    internal static string BuildRateLimitPartitionKey(bool isLogin, int permitLimit, string caller)
        => $"{(isLogin ? "login" : "api")}:{permitLimit}:{caller}";

    /// <summary>
    /// 登入端點（含 /api/v1 平行路由）。登入是暴力破解的主要標的，配額比一般 API 嚴格得多；
    /// 帳號鎖定是第二道防線，但它擋不住「橫向」猜測多個帳號。
    /// </summary>
    private static bool IsLoginRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/api/Auth/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/Auth/login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 限流的分割鍵：已驗證身分優先（同一人換 IP 也受同一份配額），
    /// 未登入則退回來源 IP，取不到 IP 時才落到共用的 "anonymous" 分割。
    ///
    /// ⚠️ 取 IP 會受 <c>X-Forwarded-For</c> 影響，因此
    /// <c>UseForwardedHeaders</c> 必須設定 KnownProxies/KnownNetworks，
    /// 否則呼叫端可自行偽造標頭來繞過配額。見 ApplicationBuilderExtensions。
    /// </summary>
    internal static string ResolvePartitionKey(HttpContext context)
    {
        var user = context.User?.Identity;
        if (user is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(context.User!.Identity!.Name))
        {
            return $"user:{context.User.Identity.Name}";
        }

        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "anonymous" : $"ip:{ip}";
    }

    public static IServiceCollection AddConfiguredHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }
}
