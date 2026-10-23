using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Parameters;
using CustomerHelpAgent.Web.Extensions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 系統參數（0.9.98 起）：資料庫裡的覆寫值疊在設定檔之上，存檔後不需重啟即生效。
///
/// 守門重點：目錄的每個鍵真的綁得到設定類別、範圍與真的驗證器一致、不可能放進機密；
/// 行為重點：存檔立即生效、不合法的值存不進去也不會在載入時讓設定壞掉、多行程同步、並行衝突。
/// </summary>
public sealed class SystemParameterTests
{
    // ---------- 目錄守門 ----------

    [Fact]
    public void Catalog_KeysAndLabels_ShouldBeUniqueAndDescribed()
    {
        var all = SystemParameterCatalog.All;
        Assert.Equal(all.Count, all.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Description), x.Key);
            Assert.False(string.IsNullOrWhiteSpace(x.EffectNote), x.Key);
            Assert.Equal($"{x.SectionName}:{x.PropertyPath}", x.Key);
        });
        Assert.All(all.Where(x => x.Kind == SystemParameterKind.Int), x =>
            Assert.True(x.Min is not null && x.Max is not null, $"{x.Key} 沒有範圍：請在設定類別加 [Range] 或在目錄明列 min／max。"));
    }

    [Fact]
    public void Catalog_EveryKey_ShouldBindToItsOptionsProperty()
    {
        // 每個鍵設成哨兵值後，對應設定類別的 IOptionsMonitor 要讀到它 —— 區段或屬性名稱拼錯時這裡會失敗。
        using var harness = Harness.Create();
        foreach (var definition in SystemParameterCatalog.All)
        {
            var sentinel = SentinelFor(definition, harness.Configuration[definition.Key]);
            harness.Provider.Apply(new Dictionary<string, string> { [definition.Key] = sentinel });

            var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(definition.OptionsType);
            object? value = monitorType.GetProperty(nameof(IOptionsMonitor<object>.CurrentValue))!.GetValue(harness.Services.GetRequiredService(monitorType));
            foreach (var segment in definition.PropertyPath.Split(':'))
            {
                value = value!.GetType().GetProperty(segment)!.GetValue(value);
            }

            Assert.True(
                string.Equals(SystemParameterValueCodec.Canonical(definition, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)), sentinel, StringComparison.Ordinal),
                $"{definition.Key} 設成 {sentinel}，但 {definition.OptionsType.Name} 讀到 {value}。");
        }
    }

    [Fact]
    public void Catalog_IntRanges_ShouldMatchTheRealValidators()
    {
        using var harness = Harness.Create();
        foreach (var definition in SystemParameterCatalog.All.Where(x => x.Kind == SystemParameterKind.Int))
        {
            var min = definition.Min!.Value;
            var max = definition.Max!.Value;
            Assert.True(harness.Validate(definition, min).Count == 0, $"{definition.Key}={min} 應該合法。");
            Assert.True(harness.Validate(definition, max).Count == 0, $"{definition.Key}={max} 應該合法。");

            var leaf = definition.PropertyPath.Split(':').Last();
            Assert.Contains(harness.Validate(definition, min - 1), x => x.Contains(leaf, StringComparison.Ordinal));
            Assert.Contains(harness.Validate(definition, max + 1), x => x.Contains(leaf, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Catalog_ShouldOnlyContainAllowedNonSecretKeys()
    {
        var allowedSections = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(SystemSettings), LogRetentionSettings.SectionName, AiCallLogSettings.SectionName, SoftDeleteSettings.SectionName,
            ScheduledJobSettings.SectionName, SlowOperationSettings.SectionName, ExceptionAlertSettings.SectionName,
            ClientErrorReportingSettings.SectionName, RateLimitSettings.SectionName, PasswordResetSettings.SectionName,
            BackupSettings.SectionName, NotificationSettings.SectionName, PasswordPolicySettings.SectionName, LockoutSettings.SectionName, TwoFactorSettings.SectionName,
            AiQuotaSettings.SectionName,
        };
        var secretLike = new Regex("password|secret|apikey|signingkey|connection|licensekey|recipients|path", RegexOptions.IgnoreCase);

        Assert.All(SystemParameterCatalog.All, x =>
        {
            Assert.Contains(x.SectionName, allowedSections);
            Assert.DoesNotMatch(secretLike, x.PropertyPath);
        });

        // SystemSettings 只開放名稱與簡介：版本號只有一個來源（每次異動 Patch +1），路徑是基礎設施。
        Assert.Equal(
            new[] { "SystemSettings:SystemInformation:SystemDescription", "SystemSettings:SystemInformation:SystemName" },
            SystemParameterCatalog.All.Where(x => x.SectionName == nameof(SystemSettings)).Select(x => x.Key).Order().ToArray());
        Assert.Null(SystemParameterCatalog.Find("SystemSettings:SystemInformation:SystemVersion"));
        Assert.Null(SystemParameterCatalog.Find("BootstrapSettings:SupportPassword"));
        Assert.Null(SystemParameterCatalog.Find("JwtSettings:SigningKey"));
    }

    [Fact]
    public void Catalog_EveryKey_ShouldBeDocumented()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "operations", "日誌與設定檔說明.md"));
        Assert.All(SystemParameterCatalog.All, x => Assert.Contains($"`{x.Key}`", doc, StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogOptions_ShouldBeReadLive_NotThroughIOptionsSnapshotAtStartup()
    {
        // 目錄裡的設定類別一律用 IOptionsMonitor 讀；IOptions<T> 是啟動時的快照，改了要重啟才生效。
        // SystemSettings 例外（路徑類用 IOptions 沒問題），但名稱與簡介只准經 ISystemIdentity 讀。
        var sources = SourceFiles().ToList();
        var types = SystemParameterCatalog.All.Select(x => x.OptionsType).Where(x => x != typeof(SystemSettings)).Distinct();
        foreach (var type in types)
        {
            var pattern = new Regex($@"IOptions(Snapshot)?<{type.Name}>");
            var offenders = sources.Where(x => pattern.IsMatch(x.Text)).Select(x => x.Name).ToList();
            Assert.True(offenders.Count == 0, $"{type.Name} 是系統參數，必須用 IOptionsMonitor 讀：{string.Join("、", offenders)}");
        }

        var identityReaders = sources
            .Where(x => Regex.IsMatch(x.Text, @"SystemInformation\.(SystemName|SystemDescription)"))
            .Select(x => x.Name)
            .Where(x => x is not ("SystemIdentity.cs" or "SystemSettingsValidator.cs" or "SystemParameterCatalog.cs"))
            .ToList();
        Assert.True(identityReaders.Count == 0, $"系統名稱與簡介只准經 ISystemIdentity 讀：{string.Join("、", identityReaders)}");
    }

    // ---------- 行為 ----------

    [Fact]
    public async Task Save_ShouldTakeEffectImmediately_AndNotifyOnce()
    {
        using var harness = Harness.Create();
        var monitor = harness.Services.GetRequiredService<IOptionsMonitor<LogRetentionSettings>>();
        Assert.Equal(365, monitor.CurrentValue.AuditLogDays);
        var changes = 0;
        using var subscription = monitor.OnChange((_, _) => changes++);

        var result = await harness.Manager.SaveAsync("LogRetentionSettings:AuditLogDays", "30", null, "admin");

        Assert.True(result.Success, result.Message);
        Assert.Equal(30, monitor.CurrentValue.AuditLogDays);
        Assert.Equal(1, changes);
        Assert.Equal("key=LogRetentionSettings:AuditLogDays; old=365(設定檔); new=30(系統參數)", result.AuditDetail);
        Assert.False(result.IsReset);
    }

    [Fact]
    public async Task Reset_ShouldReturnToTheConfigurationFileValue()
    {
        using var harness = Harness.Create();
        await harness.Manager.SaveAsync("LogRetentionSettings:AuditLogDays", "30", null, "admin");
        var stamp = (await harness.Service.GetAllAsync()).Single().ConcurrencyStamp;

        var result = await harness.Manager.ResetAsync("LogRetentionSettings:AuditLogDays", stamp);

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsReset);
        Assert.Equal(365, harness.Services.GetRequiredService<IOptionsMonitor<LogRetentionSettings>>().CurrentValue.AuditLogDays);
        Assert.Empty(await harness.Service.GetAllAsync());
        Assert.Equal("key=LogRetentionSettings:AuditLogDays; old=30(系統參數); new=365(設定檔)", result.AuditDetail);
    }

    [Fact]
    public async Task SavingTheConfigurationFileValue_ShouldResetInsteadOfStoringACopy()
    {
        using var harness = Harness.Create();
        await harness.Manager.SaveAsync("LogRetentionSettings:AuditLogDays", "30", null, "admin");
        var stamp = (await harness.Service.GetAllAsync()).Single().ConcurrencyStamp;

        var result = await harness.Manager.SaveAsync("LogRetentionSettings:AuditLogDays", "365", stamp, "admin");

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsReset);
        Assert.Empty(await harness.Service.GetAllAsync());
    }

    [Theory]
    [InlineData("AiCallLogSettings:RetentionDays", "0")]
    [InlineData("RateLimit:LoginRequestsPerMinute", "0")]
    [InlineData("LogRetentionSettings:AuditLogDays", "abc")]
    [InlineData("SystemSettings:SystemInformation:SystemName", "   ")]
    [InlineData("SystemSettings:SystemInformation:SystemName", "第一行\n第二行")]
    [InlineData("JwtSettings:SigningKey", "anything")]
    public async Task InvalidValues_ShouldBeRejected_AndNothingSaved(string key, string value)
    {
        using var harness = Harness.Create();
        var before = harness.Configuration[key];

        var result = await harness.Manager.SaveAsync(key, value, null, "admin");

        Assert.False(result.Success);
        Assert.Empty(await harness.Service.GetAllAsync());
        Assert.Empty(harness.Provider.Snapshot);
        Assert.Equal(before, harness.Configuration[key]);
    }

    [Fact]
    public async Task Save_ShouldRunEveryRegisteredValidator_NotJustTheRangeInTheCatalog()
    {
        // 目錄的範圍檢查只是友善的訊息；權威是 DI 裡的驗證器（跨欄位規則、衍生專案加的規則）。
        using var harness = Harness.Create(configure: services =>
            services.AddSingleton<IValidateOptions<SlowOperationSettings>>(new RejectValue(777)));

        var result = await harness.Manager.SaveAsync("SlowOperationSettings:DbCommandMs", "777", null, "admin");

        Assert.False(result.Success);
        Assert.Contains("777 不可以", result.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Service.GetAllAsync());
    }

    [Fact]
    public async Task Load_ShouldSkipInvalidAndUnknownOverrides_AndApplyTheValidOnes()
    {
        using var harness = Harness.Create();
        // 直接寫進資料庫（模擬舊版本留下、升級後範圍變窄、或有人直接改資料表）。
        await harness.Service.UpsertAsync("AiCallLogSettings:RetentionDays", "99999", null, "old");
        await harness.Service.UpsertAsync("SlowOperationSettings:DbCommandMs", "500", null, "old");
        await harness.Service.UpsertAsync("JwtSettings:SigningKey", "stolen-key-0123456789-0123456789", null, "attacker");
        var signingKeyBefore = harness.Configuration["JwtSettings:SigningKey"];

        await harness.Runtime.LoadAsync(CancellationToken.None);

        Assert.Contains("AiCallLogSettings:RetentionDays", harness.Runtime.Rejected.Keys);
        Assert.Equal(90, harness.Services.GetRequiredService<IOptionsMonitor<AiCallLogSettings>>().CurrentValue.RetentionDays);
        Assert.Equal(500, harness.Services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.DbCommandMs);
        Assert.Contains("JwtSettings:SigningKey", harness.Runtime.Orphans);
        Assert.Equal(signingKeyBefore, harness.Configuration["JwtSettings:SigningKey"]);

        var overview = await harness.Manager.GetOverviewAsync();
        Assert.Equal(SystemParameterSource.NotApplied, overview.Items.Single(x => x.Definition.Key == "AiCallLogSettings:RetentionDays").Source);
        Assert.Equal(SystemParameterSource.Override, overview.Items.Single(x => x.Definition.Key == "SlowOperationSettings:DbCommandMs").Source);
        Assert.Contains(overview.Orphans, x => x.ParameterKey == "JwtSettings:SigningKey");
    }

    [Fact]
    public async Task Load_ShouldRunEveryRegisteredValidator_AndSkipWhatTheyReject()
    {
        using var harness = Harness.Create(configure: services =>
            services.AddSingleton<IValidateOptions<SlowOperationSettings>>(new RejectValue(777)));
        await harness.Service.UpsertAsync("SlowOperationSettings:DbCommandMs", "777", null, "old");

        await harness.Runtime.RefreshAsync(CancellationToken.None);

        Assert.Contains("SlowOperationSettings:DbCommandMs", harness.Runtime.Rejected.Keys);
        Assert.Equal(1000, harness.Services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.DbCommandMs);
    }

    [Fact]
    public async Task Refresh_ShouldValidateAgainstTheConfigurationFile_NotAgainstOverridesBeingRemoved()
    {
        // 規則：HttpRequestMs=111 與 DbCommandMs=222 不可同時成立。
        // 目前套用 HttpRequestMs=111；另一個行程把它還原、同時把 DbCommandMs 改成 222。
        // 驗證時 HttpRequestMs 必須用設定檔的 3000，而不是即將被移除的 111，否則合法的修改會被誤擋。
        using var harness = Harness.Create(configure: services =>
            services.AddSingleton<IValidateOptions<SlowOperationSettings>>(new RejectCombination()));
        Assert.True((await harness.Manager.SaveAsync("SlowOperationSettings:HttpRequestMs", "111", null, "a")).Success);

        var stamp = (await harness.Service.GetAllAsync()).Single().ConcurrencyStamp;
        await harness.Service.DeleteAsync("SlowOperationSettings:HttpRequestMs", stamp);
        await harness.Service.UpsertAsync("SlowOperationSettings:DbCommandMs", "222", null, "other-host");
        await harness.Runtime.RefreshAsync(CancellationToken.None);

        var settings = harness.Services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue;
        Assert.Equal(3000, settings.HttpRequestMs);
        Assert.Equal(222, settings.DbCommandMs);
        Assert.Empty(harness.Runtime.Rejected);
    }

    [Fact]
    public async Task ConfigurationFileBrokenAtRuntime_ShouldKeepTheOverridesAlreadyApplied()
    {
        using var harness = Harness.Create();
        Assert.True((await harness.Manager.SaveAsync("ScheduledJobSettings:JobRunRetentionDays", "30", null, "admin")).Success);

        // 網站執行中有人把設定檔改壞（這裡直接改設定檔那一層）。
        ((IConfigurationRoot)harness.Configuration).Providers
            .OfType<Microsoft.Extensions.Configuration.Memory.MemoryConfigurationProvider>().Last()
            .Set("ScheduledJobSettings:Jobs:AuditLogRetention:Cron", "not a cron");
        await harness.Runtime.RefreshAsync(CancellationToken.None);

        Assert.Contains(ScheduledJobSettings.SectionName, harness.Runtime.BaseInvalid.Keys);
        Assert.Equal("30", harness.Provider.Snapshot["ScheduledJobSettings:JobRunRetentionDays"]);
    }

    [Fact]
    public async Task Refresh_ShouldPickUpAnotherProcessesChange_AndStayQuietWhenNothingChanged()
    {
        using var harness = Harness.Create();
        var monitor = harness.Services.GetRequiredService<IOptionsMonitor<SoftDeleteSettings>>();
        var changes = 0;
        using var subscription = monitor.OnChange((_, _) => changes++);

        // 另一個行程直接寫資料庫：這個行程在下一次刷新時看到。
        await harness.Service.UpsertAsync("SoftDeleteSettings:PurgeAfterDays", "30", null, "other-host");
        Assert.Equal(90, monitor.CurrentValue.PurgeAfterDays);

        await harness.Runtime.RefreshAsync(CancellationToken.None);
        Assert.Equal(30, monitor.CurrentValue.PurgeAfterDays);
        Assert.Equal(1, changes);

        await harness.Runtime.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ConcurrentEdits_TheSecondShouldGetAConflict()
    {
        using var harness = Harness.Create();
        Assert.True((await harness.Manager.SaveAsync("SlowOperationSettings:DbCommandMs", "500", null, "a")).Success);

        // 第二個人開窗時也沒有覆寫（stamp = null）→ 新增撞主鍵 → 衝突。
        var second = await harness.Manager.SaveAsync("SlowOperationSettings:DbCommandMs", "700", null, "b");
        Assert.False(second.Success);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, second.Message);

        // 拿舊版本號更新或還原 → 衝突。
        var stamp = (await harness.Service.GetAllAsync()).Single().ConcurrencyStamp;
        Assert.True((await harness.Manager.SaveAsync("SlowOperationSettings:DbCommandMs", "600", stamp, "a")).Success);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await harness.Manager.SaveAsync("SlowOperationSettings:DbCommandMs", "800", stamp, "b")).Message);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await harness.Manager.ResetAsync("SlowOperationSettings:DbCommandMs", stamp)).Message);
        Assert.Equal(600, harness.Services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.DbCommandMs);
    }

    [Fact]
    public async Task BrokenConfigurationFile_ShouldBlockSavingThatSection_WithAClearMessage()
    {
        using var harness = Harness.Create(new() { ["ScheduledJobSettings:Jobs:AuditLogRetention:Cron"] = "not a cron" });

        var result = await harness.Manager.SaveAsync("ScheduledJobSettings:JobRunRetentionDays", "30", null, "admin");

        Assert.False(result.Success);
        Assert.Contains("設定檔本身目前有誤", result.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Service.GetAllAsync());

        await harness.Runtime.RefreshAsync(CancellationToken.None);
        Assert.Contains(ScheduledJobSettings.SectionName, harness.Runtime.BaseInvalid.Keys);
    }

    [Fact]
    public async Task SystemName_ShouldBeReadLiveThroughSystemIdentity()
    {
        using var harness = Harness.Create();
        var identity = harness.Services.GetRequiredService<ISystemIdentity>();
        var original = identity.Name;

        var result = await harness.Manager.SaveAsync("SystemSettings:SystemInformation:SystemName", "  新名稱系統  ", null, "admin");

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(original, identity.Name);
        Assert.Equal("新名稱系統", identity.Name);
        Assert.Equal(harness.Configuration["SystemSettings:SystemInformation:SystemVersion"], identity.Version);
    }

    [Fact]
    public void SystemIdentity_ShouldFallBackToTheLastGoodValue_WhenTheConfigurationBreaks()
    {
        var monitor = new BreakableMonitor(new SystemSettings { SystemInformation = { SystemName = "正常名稱" } });
        var identity = new SystemIdentity(monitor);
        Assert.Equal("正常名稱", identity.Name);

        monitor.Broken = true;

        Assert.Equal("正常名稱", identity.Name);
    }

    [Theory]
    [InlineData("365", "30", true)]
    [InlineData("0", "30", true)]
    [InlineData("30", "365", false)]
    [InlineData("30", "0", false)]
    [InlineData("30", "30", false)]
    public void ShortensRetention_ShouldDetectLoweringOrStartingToPurge(string current, string next, bool expected)
        => Assert.Equal(expected, CustomerHelpAgent.Web.Components.Views.Admins.SystemParameterView.ShortensRetention(current, next));

    [Fact]
    public void AuditDetail_ShouldTruncateLongValues()
    {
        var detail = SystemParameterManager.BuildAuditDetail("k", new string('a', 500), "設定檔", "b", "系統參數");
        Assert.Contains(new string('a', SystemParameterManager.MaxAuditValueLength) + "…(設定檔)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', SystemParameterManager.MaxAuditValueLength + 1), detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RateLimitPartitionKey_ShouldChangeWhenTheLimitChanges()
    {
        // limiter 依分割鍵快取：上限不在鍵裡的話，持續呼叫的用戶端會一直沿用舊上限。
        Assert.NotEqual(
            CustomerHelpAgent.Web.Extensions.ServiceCollectionExtensions.BuildRateLimitPartitionKey(true, 10, "ip:203.0.113.1"),
            CustomerHelpAgent.Web.Extensions.ServiceCollectionExtensions.BuildRateLimitPartitionKey(true, 2, "ip:203.0.113.1"));
        Assert.NotEqual(
            CustomerHelpAgent.Web.Extensions.ServiceCollectionExtensions.BuildRateLimitPartitionKey(true, 10, "ip:203.0.113.1"),
            CustomerHelpAgent.Web.Extensions.ServiceCollectionExtensions.BuildRateLimitPartitionKey(false, 10, "ip:203.0.113.1"));
    }

    // ---------- 測試工具 ----------

    private static string SentinelFor(SystemParameterDefinition definition, string? current)
    {
        switch (definition.Kind)
        {
            case SystemParameterKind.Bool:
                return SystemParameterValueCodec.Canonical(definition, current) == "true" ? "false" : "true";
            case SystemParameterKind.Int:
                var candidate = definition.Min!.Value + 1;
                return (candidate.ToString(System.Globalization.CultureInfo.InvariantCulture) == SystemParameterValueCodec.Canonical(definition, current)
                    ? candidate + 1
                    : candidate).ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                return "哨兵值";
        }
    }

    private static IEnumerable<(string Name, string Text)> SourceFiles()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "CustomerHelpAgent");
        foreach (var project in new[] { "CustomerHelpAgent.Web", "CustomerHelpAgent.Business" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories)
                         .Where(x => x.EndsWith(".cs", StringComparison.Ordinal) || x.EndsWith(".razor", StringComparison.Ordinal))
                         .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                     && !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                yield return (Path.GetFileName(file), File.ReadAllText(file));
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 repo 根目錄（含 docs）。");
    }

    private sealed class RejectValue(int rejected) : IValidateOptions<SlowOperationSettings>
    {
        public ValidateOptionsResult Validate(string? name, SlowOperationSettings options)
            => options.DbCommandMs == rejected ? ValidateOptionsResult.Fail($"DbCommandMs={rejected} 不可以。") : ValidateOptionsResult.Success;
    }

    private sealed class RejectCombination : IValidateOptions<SlowOperationSettings>
    {
        public ValidateOptionsResult Validate(string? name, SlowOperationSettings options)
            => options.HttpRequestMs == 111 && options.DbCommandMs == 222
                ? ValidateOptionsResult.Fail("HttpRequestMs=111 與 DbCommandMs=222 不可同時成立。")
                : ValidateOptionsResult.Success;
    }

    private sealed class BreakableMonitor(SystemSettings value) : IOptionsMonitor<SystemSettings>
    {
        public bool Broken { get; set; }

        public SystemSettings CurrentValue => Broken
            ? throw new OptionsValidationException(nameof(SystemSettings), typeof(SystemSettings), ["壞掉了"])
            : value;

        public SystemSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SystemSettings, string?> listener) => null;
    }

    /// <summary>出貨的 appsettings ＋（測試要的）設定檔層覆寫 ＋ 系統參數覆寫層，註冊方式與 Program.cs 相同。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection connection;

        private Harness(SqliteConnection connection, ConfigurationManager configuration, SystemParameterConfigurationProvider provider, ServiceProvider services)
        {
            this.connection = connection;
            Configuration = configuration;
            Provider = provider;
            Services = services;
        }

        public ConfigurationManager Configuration { get; }

        public SystemParameterConfigurationProvider Provider { get; }

        public ServiceProvider Services { get; }

        public SystemParameterManager Manager => Services.GetRequiredService<SystemParameterManager>();

        public SystemParameterRuntime Runtime => Services.GetRequiredService<SystemParameterRuntime>();

        public SystemParameterService Service => Services.GetRequiredService<SystemParameterService>();

        public static Harness Create(Dictionary<string, string?>? fileOverrides = null, Action<IServiceCollection>? configure = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options))
            {
                context.Database.EnsureCreated();
            }

            var configuration = new ConfigurationManager();
            configuration.AddJsonFile(Path.Combine(FindRepoRoot(), "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "appsettings.json"), optional: false);
            configuration.AddInMemoryCollection(fileOverrides ?? []);
            var provider = configuration.AddSystemParameterOverrides();

            var services = new ServiceCollection();
            services.AddLogging(x => x.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDbContextFactory<BackendDBContext>>(new TestDbContextFactory(connection));
            services.AddSingleton<ISystemIdentity, SystemIdentity>();
            services.AddConfiguredOptions(configuration);
            services.AddConfiguredEmail(configuration);
            services.AddScheduledJobs();
            services.AddSystemParameters(provider);
            configure?.Invoke(services);
            return new Harness(connection, configuration, provider, services.BuildServiceProvider());
        }

        public IReadOnlyList<string> Validate(SystemParameterDefinition definition, int value)
            => Services.GetRequiredService<SystemParameterValidator>().Validate(
                definition.SectionName,
                new Dictionary<string, string> { [definition.Key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        public void Dispose()
        {
            Services.Dispose();
            connection.Dispose();
        }
    }
}

/// <summary>真主機：覆寫層排在 JSON 與環境變數之後，存檔後在主機內立即可見。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SystemParameterHostTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public SystemParameterHostTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task OverrideLayer_ShouldOutrankFileAndEnvironment_AndApplyInTheRealHost()
    {
        using var client = factory.CreateClient();
        var services = factory.Services;
        var root = (IConfigurationRoot)services.GetRequiredService<IConfiguration>();
        var provider = services.GetRequiredService<SystemParameterConfigurationProvider>();
        var providers = root.Providers.ToList();
        var position = providers.IndexOf(provider);
        Assert.True(position >= 0, "系統參數的設定來源沒有加進主機的設定。");
        Assert.True(position > providers.FindLastIndex(x => x is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider),
            "系統參數必須排在環境變數之後（優先權較高）。");

        // 用不在測試設定裡的鍵（WebApplicationFactory 後加的設定會蓋過覆寫層）。
        var manager = services.GetRequiredService<SystemParameterManager>();
        var current = services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.DbCommandMs;
        var target = current == 4321 ? 4322 : 4321;
        var overview = await manager.GetOverviewAsync();
        var stamp = overview.Items.Single(x => x.Definition.Key == "SlowOperationSettings:DbCommandMs").Override?.ConcurrencyStamp;

        var result = await manager.SaveAsync("SlowOperationSettings:DbCommandMs", target.ToString(System.Globalization.CultureInfo.InvariantCulture), stamp, "integration");

        Assert.True(result.Success, result.Message);
        Assert.Equal(target, services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.DbCommandMs);
    }
}
