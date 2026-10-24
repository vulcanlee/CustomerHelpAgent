using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Validation;
using CustomerHelpAgent.Web.Extensions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 0.9.92 起每個設定類別都在啟動時驗證：設定矛盾、拼錯、格式錯誤一律拒絕啟動。
///
/// 每個反例都斷言錯誤訊息含**設定鍵**：訊息是給部署的人看的，看不出要改哪一行就等於沒有驗證。
/// </summary>
public sealed class OptionsValidationTests
{
    // ---------- SystemSettings ----------

    [Fact]
    public void SystemSettings_ValidPaths_ShouldPass()
        => AssertValid(new SystemSettingsValidator().Validate(null, ValidSystemSettings()));

    [Theory]
    [InlineData("")]
    [InlineData("DB")]
    [InlineData(@"..\DB")]
    public void SystemSettings_MissingOrRelativePath_ShouldFail(string path)
    {
        var settings = ValidSystemSettings();
        settings.ExternalFileSystem.DatabasePath = path;

        AssertInvalid(new SystemSettingsValidator().Validate(null, settings), "SystemSettings:ExternalFileSystem:DatabasePath");
    }

    [Fact]
    public void SystemSettings_ShouldReportEveryInvalidPathAtOnce()
    {
        var settings = ValidSystemSettings();
        settings.ExternalFileSystem.ExceptionPath = "";
        settings.ExternalFileSystem.AiCallLogPath = "logs";
        settings.SystemInformation.SystemName = " ";

        var result = new SystemSettingsValidator().Validate(null, settings);

        AssertInvalid(result, "ExceptionPath");
        AssertInvalid(result, "AiCallLogPath");
        AssertInvalid(result, "SystemInformation:SystemName");
    }

    // ---------- BootstrapSettings ----------

    [Theory]
    [InlineData("", "secret", "BootstrapSettings:SupportAccount")]
    [InlineData("support", "", "BootstrapSettings:SupportPassword")]
    public void BootstrapSettings_BlankAccountOrPassword_ShouldFail(string account, string password, string key)
        => AssertInvalid(
            new BootstrapSettingsValidator().Validate(null, new BootstrapSettings { SupportAccount = account, SupportPassword = password }),
            key);

    // ---------- RateLimit ----------

    [Theory]
    [InlineData(0, 10, "RateLimit:ApiRequestsPerMinute")]
    [InlineData(120, -1, "RateLimit:LoginRequestsPerMinute")]
    public void RateLimit_NonPositive_ShouldFail(int api, int login, string key)
        => AssertInvalid(
            new RateLimitSettingsValidator().Validate(null, new RateLimitSettings { ApiRequestsPerMinute = api, LoginRequestsPerMinute = login }),
            key);

    // ---------- AiSettings ----------

    [Fact]
    public void AiSettings_ShippedDefaultWithoutApiKey_ShouldPass()
        => AssertValid(new AiSettingsValidator().Validate(null, new AiSettings()));

    [Fact]
    public void AiSettings_UnknownProvider_ShouldFail()
        => AssertInvalid(new AiSettingsValidator().Validate(null, new AiSettings { Provider = "Gemini" }), "AiSettings:Provider");

    [Fact]
    public void AiSettings_EndpointWithoutScheme_ShouldFail()
        => AssertInvalid(
            new AiSettingsValidator().Validate(null, new AiSettings { Endpoint = "myres.openai.azure.com" }),
            "AiSettings:Endpoint");

    [Fact]
    public void AiSettings_ApiKeyWithoutModelOrAzureEndpoint_ShouldFail()
    {
        var result = new AiSettingsValidator().Validate(null, new AiSettings { ApiKey = "key", Provider = "AzureOpenAI" });

        AssertInvalid(result, "AiSettings:Model");
        AssertInvalid(result, "AiSettings:Endpoint");
    }

    [Fact]
    public void AiSettings_CompleteOpenAiSettings_ShouldPass()
        => AssertValid(new AiSettingsValidator().Validate(null, new AiSettings { Provider = "openai", ApiKey = "key", Model = "gpt-6" }));

    [Theory]
    [InlineData(0, null, null, "AiSettings:TimeoutSeconds")]
    [InlineData(600, 0, null, "AiSettings:MaxOutputTokens")]
    [InlineData(600, null, 2.5, "AiSettings:Temperature")]
    public void AiSettings_OutOfRangeNumbers_ShouldFail(int timeoutSeconds, int? maxOutputTokens, double? temperature, string key)
        => AssertInvalid(
            new AiSettingsValidator().Validate(null, new AiSettings
            {
                TimeoutSeconds = timeoutSeconds,
                MaxOutputTokens = maxOutputTokens,
                Temperature = temperature,
            }),
            key);

    // ---------- AfterSalesAgentSettings ----------

    [Fact]
    public void AfterSalesAgentSettings_Defaults_ShouldPass()
        => AssertValid(new AfterSalesAgentSettingsValidator().Validate(null, new AfterSalesAgentSettings()));

    [Theory]
    [InlineData(0, 2, 15, "AfterSalesAgentSettings:MaxSteps")]
    [InlineData(21, 2, 15, "AfterSalesAgentSettings:MaxSteps")]
    [InlineData(8, 0, 15, "AfterSalesAgentSettings:MaxConsecutiveToolFailures")]
    [InlineData(8, 6, 15, "AfterSalesAgentSettings:MaxConsecutiveToolFailures")]
    [InlineData(8, 2, 0, "AfterSalesAgentSettings:ToolTimeoutSeconds")]
    [InlineData(8, 2, 121, "AfterSalesAgentSettings:ToolTimeoutSeconds")]
    public void AfterSalesAgentSettings_OutOfRange_ShouldFail(int maxSteps, int maxFailures, int timeoutSeconds, string key)
        => AssertInvalid(
            new AfterSalesAgentSettingsValidator().Validate(null, new AfterSalesAgentSettings
            {
                MaxSteps = maxSteps,
                MaxConsecutiveToolFailures = maxFailures,
                ToolTimeoutSeconds = timeoutSeconds,
            }),
            key);

    [Theory]
    [InlineData("Responses", "", null)]
    [InlineData("chatcompletions", "none", 0.2)]
    [InlineData("ChatCompletions", "minimal", 0.0)]
    [InlineData("Responses", "LOW", null)]
    [InlineData("Responses", "medium", 2.0)]
    [InlineData("Responses", "high", null)]
    public void AfterSalesAgentSettings_ValidModelRequestOptions_ShouldPass(string apiStyle, string reasoningEffort, double? temperature)
        => AssertValid(new AfterSalesAgentSettingsValidator().Validate(null, new AfterSalesAgentSettings
        {
            ApiStyle = apiStyle,
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
        }));

    [Theory]
    [InlineData("Assistants", "", null, "AfterSalesAgentSettings:ApiStyle")]
    [InlineData("", "", null, "AfterSalesAgentSettings:ApiStyle")]
    [InlineData("1", "", null, "AfterSalesAgentSettings:ApiStyle")]
    [InlineData("Responses", "extreme", null, "AfterSalesAgentSettings:ReasoningEffort")]
    [InlineData("Responses", "", -0.1, "AfterSalesAgentSettings:Temperature")]
    [InlineData("Responses", "", 2.5, "AfterSalesAgentSettings:Temperature")]
    public void AfterSalesAgentSettings_InvalidModelRequestOptions_ShouldFail(string apiStyle, string reasoningEffort, double? temperature, string key)
        => AssertInvalid(
            new AfterSalesAgentSettingsValidator().Validate(null, new AfterSalesAgentSettings
            {
                ApiStyle = apiStyle,
                ReasoningEffort = reasoningEffort,
                Temperature = temperature,
            }),
            key);

    [Theory]
    [InlineData("", true)]
    [InlineData("gpt-vision-deployment", true)]
    [InlineData(" gpt-vision-deployment", false)]
    [InlineData("gpt-vision-deployment ", false)]
    public void AfterSalesAgentSettings_VisionModel_RejectsSurroundingWhitespace(string visionModel, bool valid)
    {
        var result = new AfterSalesAgentSettingsValidator().Validate(null, new AfterSalesAgentSettings { VisionModel = visionModel });
        if (valid)
        {
            AssertValid(result);
        }
        else
        {
            AssertInvalid(result, "AfterSalesAgentSettings:VisionModel");
        }
    }

    [Fact]
    public void AfterSalesAgentSettings_OutOfRangeInConfiguration_ShouldFailOnStart()
        => Assert.Throws<OptionsValidationException>(
            () => ValidateRegisteredOptions(ShippedConfiguration(), new() { ["AfterSalesAgentSettings:MaxSteps"] = "0" }));

    // ---------- AiPricingSettings ----------

    [Fact]
    public void AiPricing_NonPositiveExchangeRate_ShouldFail()
        => AssertInvalid(new AiPricingSettingsValidator().Validate(null, new AiPricingSettings { UsdToTwd = 0 }), "AiPricingSettings:UsdToTwd");

    [Fact]
    public void AiPricing_NegativeRateAndLongContextWithoutThreshold_ShouldFail()
    {
        var settings = new AiPricingSettings
        {
            UsdToTwd = 31.5,
            Models =
            {
                ["gpt-test"] = new AiModelPricing
                {
                    Rates = new AiModelRates { TextInputPerMillion = -1 },
                    LongContextRates = new AiModelRates { TextInputPerMillion = 2 },
                    LongContextThresholdTokens = 0,
                },
            },
        };

        var result = new AiPricingSettingsValidator().Validate(null, settings);

        AssertInvalid(result, "AiPricingSettings:Models:gpt-test:Rates:TextInputPerMillion");
        AssertInvalid(result, "AiPricingSettings:Models:gpt-test:LongContextThresholdTokens");
    }

    // ---------- CacheSettings ----------

    [Theory]
    [InlineData("Memcached", "", 30, "CacheSettings:Provider")]
    [InlineData("Redis", "", 30, "CacheSettings:RedisConnection")]
    [InlineData("Memory", "", 0, "CacheSettings:DefaultExpirationMinutes")]
    public void Cache_InvalidSettings_ShouldFail(string provider, string redis, int minutes, string key)
        => AssertInvalid(
            new CacheSettingsValidator().Validate(null, new CacheSettings { Provider = provider, RedisConnection = redis, DefaultExpirationMinutes = minutes }),
            key);

    // ---------- Cors ----------

    [Theory]
    [InlineData("*")]
    [InlineData("app.example.com")]
    [InlineData("https://app.example.com/")]
    [InlineData("https://app.example.com/path")]
    [InlineData("ftp://app.example.com")]
    public void Cors_InvalidOrigin_ShouldFail(string origin)
        => AssertInvalid(new CorsSettingsValidator().Validate(null, new CorsSettings { AllowedOrigins = [origin] }), "Cors:AllowedOrigins");

    [Fact]
    public void Cors_ValidOrigins_ShouldPass()
        => AssertValid(new CorsSettingsValidator().Validate(null, new CorsSettings
        {
            AllowedOrigins = ["https://app.example.com", "http://localhost:5173"],
        }));

    // ---------- GoogleOAuth ----------

    [Fact]
    public void GoogleOAuth_EnabledWithoutCredentials_ShouldFail()
    {
        var result = new GoogleOAuthSettingsValidator().Validate(null, new GoogleOAuthSettings { Enabled = true });

        AssertInvalid(result, "GoogleOAuthSettings:ClientId");
        AssertInvalid(result, "GoogleOAuthSettings:ClientSecret");
    }

    [Fact]
    public void GoogleOAuth_Disabled_ShouldPassEvenWhenEmpty()
        => AssertValid(new GoogleOAuthSettingsValidator().Validate(null, new GoogleOAuthSettings { Enabled = false }));

    // ---------- DemoSettings ----------

    [Theory]
    [InlineData("")]
    [InlineData("demo123")]
    public void DemoSettings_EnabledWithShortPassword_ShouldFail(string password)
        => AssertInvalid(
            new DemoSettingsValidator().Validate(null, new DemoSettings { Enabled = true, DemoUserPassword = password }),
            "DemoSettings:DemoUserPassword");

    [Fact]
    public void DemoSettings_Disabled_ShouldPassEvenWhenEmpty()
        => AssertValid(new DemoSettingsValidator().Validate(null, new DemoSettings { Enabled = false, DemoUserPassword = string.Empty }));

    // ---------- ForwardedHeaders ----------

    [Theory]
    [InlineData("10.0.0.0/40", "KnownNetworks")]
    [InlineData("10.0.0.0", "KnownNetworks")]
    [InlineData("not-an-ip/8", "KnownNetworks")]
    public void ForwardedHeaders_InvalidNetwork_ShouldFail(string network, string key)
        => AssertInvalid(
            new ForwardedHeadersSettingsValidator().Validate(null, new ForwardedHeadersSettings { KnownNetworks = [network] }),
            $"ForwardedHeaders:{key}");

    [Fact]
    public void ForwardedHeaders_InvalidProxy_ShouldFail()
        => AssertInvalid(
            new ForwardedHeadersSettingsValidator().Validate(null, new ForwardedHeadersSettings { KnownProxies = ["proxy.local"] }),
            "ForwardedHeaders:KnownProxies");

    [Fact]
    public void ForwardedHeaders_ValidEntries_ShouldPass()
        => AssertValid(new ForwardedHeadersSettingsValidator().Validate(null, new ForwardedHeadersSettings
        {
            KnownProxies = ["10.0.0.5", "::1"],
            KnownNetworks = ["10.0.0.0/8", "fd00::/64"],
        }));

    // ---------- 既有類別補上的缺口（透過實際註冊驗證） ----------

    [Fact]
    public void EmailPublicBaseUrl_WithoutScheme_ShouldFailOnStart()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => ValidateRegisteredOptions(ShippedConfiguration(), new() { ["EmailSettings:PublicBaseUrl"] = "erp.example.com" }));

        Assert.Contains("EmailSettings:PublicBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionAlertRecipient_InvalidEmail_ShouldFailOnStart()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => ValidateRegisteredOptions(ShippedConfiguration(), new() { ["ExceptionAlertSettings:Recipients:0"] = "ops-team" }));

        Assert.Contains("ExceptionAlertSettings:Recipients", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("36501")]
    public void SoftDeletePurgeAfterDays_OutOfRange_ShouldFailOnStart(string days)
    {
        // 保留天數寫壞時拒絕啟動：不可以讓自動永久刪除用錯的門檻刪資料（0.9.97 起）。
        var exception = Assert.Throws<OptionsValidationException>(
            () => ValidateRegisteredOptions(ShippedConfiguration(), new() { ["SoftDeleteSettings:PurgeAfterDays"] = days }));

        Assert.Contains("PurgeAfterDays", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeMismatch_InClassWithoutRules_ShouldFailOnStart()
    {
        // Security 沒有欄位規則；改用 ValidateOnStart 之後，bool 填錯在啟動時就失敗，而不是第一次讀取時。
        Assert.ThrowsAny<Exception>(
            () => ValidateRegisteredOptions(ShippedConfiguration(), new() { ["Security:ReturnExceptionDetails"] = "yes" }));
    }

    // ---------- 範本出貨的設定 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    public void ShippedAppSettings_ShouldPassValidation(string? environment)
    {
        // Production 範本刻意把機密留空，要由環境變數提供（StartupSafetyValidator 也會擋）；這裡模擬已提供的狀態。
        var overrides = environment == "Production"
            ? new Dictionary<string, string?> { ["BootstrapSettings:SupportPassword"] = "a-production-password" }
            : [];

        ValidateRegisteredOptions(ShippedConfiguration(environment), overrides);
    }

    // ---------- 守門 ----------

    [Fact]
    public void ConfigurationSections_ShouldNeverBeBoundWithoutStartupValidation()
    {
        var webRoot = Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web");
        var files = new[] { Path.Combine(webRoot, "Program.cs") }
            .Concat(Directory.EnumerateFiles(Path.Combine(webRoot, "Extensions"), "*.cs"))
            .ToList();

        var violations = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // 單純的 Configure<T>(...GetSection(...)) 沒有任何驗證。
            foreach (Match match in Regex.Matches(source, @"\.Configure<\w+>\(\s*[\w.]*GetSection\("))
            {
                violations.Add($"{name}: {match.Value} —— 請改用 AddValidatedOptions 或 AddOptions<T>().Bind(...).ValidateOnStart()");
            }

            // AddOptions<T>()...Bind(...) 的同一個敘述裡必須有 ValidateOnStart()。
            foreach (var statement in source.Split(';').Where(s => s.Contains("AddOptions<", StringComparison.Ordinal) && s.Contains(".Bind(", StringComparison.Ordinal)))
            {
                if (!statement.Contains("ValidateOnStart()", StringComparison.Ordinal))
                {
                    violations.Add($"{name}: {statement.Trim().Split('\n')[0].Trim()} …（缺少 ValidateOnStart()）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "從設定檔綁定的類別一律要在啟動時驗證（0.9.92 起）：設定寫錯要在部署當下失敗，不是帶著錯的值跑。"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ---------- helpers ----------

    private static SystemSettings ValidSystemSettings()
    {
        var settings = new SystemSettings();
        settings.SystemInformation.SystemName = "企業管理平台";
        var paths = settings.ExternalFileSystem;
        paths.DatabasePath = @"C:\temp\app\DB";
        paths.DownloadPath = @"C:\temp\app\Download";
        paths.UploadPath = @"C:\temp\app\Upload";
        paths.ProjectFilePath = @"C:\temp\app\ProjectFile";
        paths.AfterSalesFilePath = @"C:\temp\app\AfterSalesFile";
        paths.ExceptionPath = @"C:\temp\app\Exception";
        paths.TokenUsagePath = @"C:\temp\app\TokenUsage";
        paths.AiCallLogPath = @"C:\temp\app\AiCallLog";
        paths.AuditContentPath = @"C:\temp\app\AuditContent";
        paths.DataProtectionKeyPath = @"\\fileserver\share\Keys";
        paths.BackupPath = @"D:\backup\app";
        return settings;
    }

    private static void AssertValid(ValidateOptionsResult result)
        => Assert.True(result.Succeeded, "預期通過驗證，實際錯誤：" + string.Join(" | ", result.Failures ?? []));

    private static void AssertInvalid(ValidateOptionsResult result, string expectedKey)
    {
        Assert.True(result.Failed, $"預期驗證失敗（{expectedKey}），實際卻通過。");
        Assert.Contains(result.Failures!, failure => failure.Contains(expectedKey, StringComparison.Ordinal));
    }

    private static IConfigurationBuilder ShippedConfiguration(string? environment = null)
    {
        var webRoot = Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web");
        var builder = new ConfigurationBuilder().AddJsonFile(Path.Combine(webRoot, "appsettings.json"), optional: false);
        if (environment is not null)
        {
            builder.AddJsonFile(Path.Combine(webRoot, $"appsettings.{environment}.json"), optional: false);
        }

        return builder;
    }

    /// <summary>以與 Program.cs 相同的註冊方式建立容器，執行所有啟動驗證（與正式啟動時呼叫的是同一個 IStartupValidator）。</summary>
    private static void ValidateRegisteredOptions(IConfigurationBuilder builder, Dictionary<string, string?> overrides)
    {
        var configuration = builder.AddInMemoryCollection(overrides).Build();

        var services = new ServiceCollection();
        services.AddConfiguredOptions(configuration);
        services.AddConfiguredEmail(configuration);
        services.AddScheduledJobs();
        services.AddValidatedOptions<GoogleOAuthSettings, GoogleOAuthSettingsValidator>(configuration, GoogleOAuthSettings.SectionName);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }
}
