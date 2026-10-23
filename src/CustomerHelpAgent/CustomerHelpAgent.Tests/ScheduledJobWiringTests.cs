using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 排程作業的接線（0.9.96 起），以真正的主機（WebApplicationFactory）驗證：
/// 四個內建作業都已註冊且在 scope 內解析得到、舊的兩個 BackgroundService 計時器已移除、排程器註冊在例外寫入器之後。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ScheduledJobWiringTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public ScheduledJobWiringTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void BuiltInJobs_ShouldBeRegistered_AndResolvableInAScope()
    {
        var descriptors = factory.Services.GetServices<ScheduledJobDescriptor>().ToList();

        Assert.Equal(
            new[] { AiCallLogRetentionJob.JobName, AuditLogRetentionJob.JobName, ExceptionLogRetentionJob.JobName, NotificationRetentionJob.JobName, PasswordExpiryReminderJob.JobName, SoftDeletePurgeJob.JobName, SystemBackupJob.JobName, TokenUsageLogRetentionJob.JobName },
            descriptors.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());

        using var scope = factory.Services.CreateScope();
        Assert.All(descriptors, d => Assert.IsAssignableFrom<IScheduledJob>(scope.ServiceProvider.GetRequiredService(d.JobType)));
    }

    [Fact]
    public void Scheduler_ShouldReplaceTheOldTimers_AndStopBeforeTheExceptionWriter()
    {
        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        var names = hosted.Select(x => x.GetType().Name).ToList();

        Assert.DoesNotContain("LogRetentionWorker", names);
        Assert.DoesNotContain("AiCallLogRetentionWorker", names);

        // 主機以相反順序停止：排程器排在後面才會先停，作業在關機時記的錯誤還有例外寫入器接手。
        var scheduler = hosted.FindIndex(x => x is JobSchedulerWorker);
        var writer = hosted.FindIndex(x => x is ExceptionLogWriter);
        Assert.True(scheduler > writer && writer >= 0, $"JobSchedulerWorker（{scheduler}）必須註冊在 ExceptionLogWriter（{writer}）之後。");
    }
}
