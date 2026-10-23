using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 系統例外的 Email 告警（LOG-12）：三種觸發、兩層節流、信件只含摘要。
/// </summary>
public sealed class ExceptionAlertServiceTests
{
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
    private readonly CapturingEmailQueue queue = new();

    [Fact]
    public void NewSignature_ShouldSendOneEmailPerRecipient()
    {
        var service = CreateService(new ExceptionAlertSettings { Recipients = ["ops@example.com", "dev@example.com"] });

        Assert.True(service.Evaluate(Outcome(id: 1, isNew: true)));

        Assert.Equal(2, queue.Enqueued.Count);
        Assert.All(queue.Enqueued, message => Assert.Equal(EmailKinds.ExceptionAlert, message.Kind));
        Assert.Contains("新的例外類型第一次發生", queue.Enqueued[0].Subject);
    }

    [Fact]
    public void Critical_ShouldSendEvenWhenSignatureAlreadyExists()
    {
        var service = CreateService();

        Assert.True(service.Evaluate(Outcome(id: 1, isNew: false, isCritical: true)));
        Assert.Contains("Critical", Assert.Single(queue.Enqueued).Subject);
    }

    [Fact]
    public void RepeatedOccurrence_BelowBurstThreshold_ShouldNotSend()
    {
        var service = CreateService(new ExceptionAlertSettings { Recipients = ["ops@example.com"], BurstThreshold = 5 });

        for (var index = 0; index < 4; index++)
        {
            Assert.False(service.Evaluate(Outcome(id: 1, isNew: false)));
        }

        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public void Burst_WithinWindow_ShouldSendOnce_ThenCoolDown()
    {
        var service = CreateService(new ExceptionAlertSettings
        {
            Recipients = ["ops@example.com"],
            BurstThreshold = 3,
            BurstWindowMinutes = 10,
            PerSignatureCooldownMinutes = 60,
        });

        var sent = Enumerable.Range(0, 6).Select(_ => service.Evaluate(Outcome(id: 1, isNew: false))).ToList();

        // 第 3 次達標寄出；之後每次也都達標，但在冷卻時間內只寄一次。
        Assert.Equal([false, false, true, false, false, false], sent);
        Assert.Contains("10 分鐘內發生 3 次", Assert.Single(queue.Enqueued).Subject);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.False(service.Evaluate(Outcome(id: 1, isNew: false)));  // 視窗已過，只剩 1 次
    }

    [Fact]
    public void OccurrencesOutsideBurstWindow_ShouldNotCount()
    {
        var service = CreateService(new ExceptionAlertSettings { Recipients = ["ops@example.com"], BurstThreshold = 3, BurstWindowMinutes = 10 });

        service.Evaluate(Outcome(id: 1, isNew: false));
        service.Evaluate(Outcome(id: 1, isNew: false));
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(service.Evaluate(Outcome(id: 1, isNew: false)));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public void HourlyCap_ShouldSuppress_AndReportSuppressedCountInNextEmail()
    {
        var service = CreateService(new ExceptionAlertSettings { Recipients = ["ops@example.com"], MaxEmailsPerHour = 2 });

        Assert.True(service.Evaluate(Outcome(id: 1, isNew: true)));
        Assert.True(service.Evaluate(Outcome(id: 2, isNew: true)));
        Assert.False(service.Evaluate(Outcome(id: 3, isNew: true)));
        Assert.False(service.Evaluate(Outcome(id: 4, isNew: true)));
        Assert.Equal(2, queue.Enqueued.Count);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(service.Evaluate(Outcome(id: 5, isNew: true)));

        Assert.Contains("先前有 2 則告警", queue.Enqueued[^1].TextBody);
    }

    [Fact]
    public void NoRecipients_ShouldBeDisabled()
    {
        var service = CreateService(new ExceptionAlertSettings { Recipients = [] });

        Assert.False(service.Evaluate(Outcome(id: 1, isNew: true, isCritical: true)));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public void EmailDispatchFailures_ShouldNotTriggerAlerts()
    {
        // SMTP 掛掉時：寄告警失敗 → 例外紀錄 → 再告警 → 再失敗……的迴圈。
        var service = CreateService();

        Assert.False(service.Evaluate(Outcome(id: 1, isNew: true, page: "EmailDispatch")));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public void OverflowRow_ShouldNotTriggerAlerts()
    {
        var service = CreateService();

        Assert.False(service.Evaluate(Outcome(id: 1, isNew: true, isOverflow: true)));
    }

    [Fact]
    public void NullOutcome_ShouldBeIgnored()
    {
        Assert.False(CreateService().Evaluate(null));
    }

    [Fact]
    public void Email_ShouldContainSummaryAndLink_ButNoAccountOrMessage()
    {
        var service = CreateService(publicBaseUrl: "https://erp.example.com/");

        service.Evaluate(Outcome(id: 1, isNew: true) with { TraceId = "K7Q2M9XA" });

        var message = Assert.Single(queue.Enqueued);
        Assert.Contains("System.InvalidOperationException", message.TextBody);
        Assert.Contains("/api/Category/{id}", message.TextBody);
        Assert.Contains("K7Q2M9XA", message.TextBody);
        Assert.Contains("https://erp.example.com/system-exceptions", message.TextBody);
        Assert.Contains("https://erp.example.com/system-exceptions", message.HtmlBody);
        Assert.DoesNotContain("alice", message.TextBody);
        Assert.DoesNotContain("alice", message.HtmlBody);
    }

    [Fact]
    public void Email_WithoutPublicBaseUrl_ShouldMentionPagePathOnly()
    {
        var service = CreateService(publicBaseUrl: "");

        service.Evaluate(Outcome(id: 1, isNew: true));

        var message = Assert.Single(queue.Enqueued);
        Assert.Contains("/system-exceptions", message.TextBody);
        Assert.DoesNotContain("http", message.TextBody);
    }

    private ExceptionAlertService CreateService(ExceptionAlertSettings? settings = null, string publicBaseUrl = "")
    {
        var systemSettings = new SystemSettings();
        systemSettings.SystemInformation.SystemName = "測試系統";

        return new ExceptionAlertService(
            new StaticOptionsMonitor<ExceptionAlertSettings>(settings ?? new ExceptionAlertSettings { Recipients = ["ops@example.com"] }),
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings { PublicBaseUrl = publicBaseUrl }),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(systemSettings)),
            queue,
            clock);
    }

    private static ExceptionRecordOutcome Outcome(
        int id,
        bool isNew,
        bool isCritical = false,
        bool isOverflow = false,
        string? page = "/api/Category/{id}")
        => new(
            id,
            isNew,
            isOverflow,
            "System.InvalidOperationException",
            ExceptionSources.WebApi,
            page,
            OccurrenceCount: 1,
            FirstOccurredAt: new DateTime(2026, 10, 1, 9, 0, 0),
            LastOccurredAt: new DateTime(2026, 10, 1, 9, 0, 0),
            TraceId: null,
            IsCritical: isCritical);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
