using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Web.Extensions;

namespace CustomerHelpAgent.Tests;

public sealed class EmailSettingsTests
{
    [Fact]
    public void GetProvider_ShouldDefaultToNone_WhenBlank()
    {
        var settings = new EmailSettings { Provider = string.Empty };

        Assert.Equal(EmailProvider.None, settings.GetProvider());
    }

    [Theory]
    [InlineData("Smtp", EmailProvider.Smtp)]
    [InlineData("smtp", EmailProvider.Smtp)]
    [InlineData("PICKUP", EmailProvider.Pickup)]
    [InlineData("none", EmailProvider.None)]
    public void GetProvider_ShouldParse_CaseInsensitive(string value, EmailProvider expected)
    {
        var settings = new EmailSettings { Provider = value };

        Assert.Equal(expected, settings.GetProvider());
    }

    /// <summary>數字字串也能被 Enum.TryParse 接受（"5" → 5），必須額外用 IsDefined 擋掉。</summary>
    [Theory]
    [InlineData("SendGrid")]
    [InlineData("5")]
    public void GetProvider_ShouldThrow_WhenUnknown(string value)
    {
        var settings = new EmailSettings { Provider = value };

        Assert.False(settings.TryGetProvider(out _));
        Assert.Throws<InvalidOperationException>(() => settings.GetProvider());
    }

    [Theory]
    [InlineData("", EmailSecurityMode.StartTls)]
    [InlineData("sslonconnect", EmailSecurityMode.SslOnConnect)]
    [InlineData("None", EmailSecurityMode.None)]
    [InlineData("Auto", EmailSecurityMode.Auto)]
    public void GetSecurity_ShouldParse(string value, EmailSecurityMode expected)
    {
        var settings = new EmailSettings { Security = value };

        Assert.Equal(expected, settings.GetSecurity());
    }

    [Fact]
    public void GetSecurity_ShouldThrow_WhenUnknown()
    {
        var settings = new EmailSettings { Security = "Tls13" };

        Assert.Throws<InvalidOperationException>(() => settings.GetSecurity());
    }

    /// <summary>
    /// 出貨值必須是「未啟用」且不帶密碼：腳手架複製出去就要能啟動，
    /// 而且不會因為一份沒改過的設定檔就開始對外寄信。
    /// </summary>
    [Fact]
    public void ShippedSettings_ShouldBeDisabledWithoutSecrets()
    {
        var path = Path.Combine(FindWebRoot(), "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var section = document.RootElement.GetProperty(EmailSettings.SectionName);

        Assert.Equal("None", section.GetProperty("Provider").GetString());
        Assert.Equal(string.Empty, section.GetProperty("Password").GetString());
        Assert.Equal(string.Empty, section.GetProperty("UserName").GetString());
    }

    [Fact]
    public void Options_WithSmtpButNoHost_ShouldFailValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Smtp",
            ["EmailSettings:FromAddress"] = "noreply@example.com",
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EmailSettings>>().Value);

        Assert.Contains("Host", exception.Message);
    }

    [Fact]
    public void Options_WithSmtpAndInvalidFromAddress_ShouldFailValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Smtp",
            ["EmailSettings:Host"] = "smtp.example.com",
            ["EmailSettings:FromAddress"] = "not-an-address",
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EmailSettings>>().Value);
    }

    [Theory]
    [InlineData("Provider", "Mailgun")]
    [InlineData("Security", "Tls13")]
    [InlineData("Port", "0")]
    [InlineData("TimeoutSeconds", "0")]
    public void Options_WithInvalidValue_ShouldFailValidation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"EmailSettings:{key}"] = value,
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EmailSettings>>().Value);
    }

    /// <summary>Pickup 模式允許不填主機與寄件者 —— 那是開發用，信不會真的寄出。</summary>
    [Fact]
    public void Options_WithPickupOnly_ShouldPass()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = "Pickup",
        });

        Assert.Equal(EmailProvider.Pickup, provider.GetRequiredService<IOptions<EmailSettings>>().Value.GetProvider());
    }

    [Theory]
    [InlineData("TokenLifetimeMinutes", "4")]
    [InlineData("TokenLifetimeMinutes", "1441")]
    [InlineData("RequestCooldownSeconds", "-1")]
    [InlineData("RequestCooldownSeconds", "3601")]
    public void PasswordResetOptions_OutOfRange_ShouldFailValidation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"PasswordResetSettings:{key}"] = value,
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PasswordResetSettings>>().Value);
    }

    [Fact]
    public void PasswordResetOptions_Defaults_ShouldBe30MinutesAnd60Seconds()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var settings = provider.GetRequiredService<IOptions<PasswordResetSettings>>().Value;

        Assert.Equal(30, settings.TokenLifetimeMinutes);
        Assert.Equal(60, settings.RequestCooldownSeconds);
    }

    [Theory]
    [InlineData("None", typeof(NullEmailSender))]
    [InlineData("Pickup", typeof(PickupEmailSender))]
    [InlineData("Smtp", typeof(SmtpEmailSender))]
    public void EmailSender_ShouldFollowConfiguredProvider(string providerName, Type expected)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["EmailSettings:Provider"] = providerName,
            ["EmailSettings:Host"] = "smtp.example.com",
            ["EmailSettings:FromAddress"] = "noreply@example.com",
        });
        using var scope = provider.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        Assert.IsType(expected, sender);
    }

    [Fact]
    public void EmailQueue_ShouldBeSingleSharedInstance()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var queue = provider.GetRequiredService<IEmailQueue>();

        Assert.Same(provider.GetRequiredService<ChannelEmailQueue>(), queue);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<SystemSettings>(_ => { });
        services.AddSingleton<ISystemIdentity, SystemIdentity>();
        services.AddConfiguredEmail(configuration);
        return services.BuildServiceProvider();
    }

    private static string FindWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(dir.FullName, "CustomerHelpAgent.Web"),
                Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web"),
            })
            {
                if (File.Exists(Path.Combine(candidate, "appsettings.json")))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web。");
    }
}
