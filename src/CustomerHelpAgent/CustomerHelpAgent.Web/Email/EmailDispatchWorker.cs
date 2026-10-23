using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// 背景寄信器：從 <see cref="ChannelEmailQueue"/> 逐封取出、以當下設定的 provider 寄出。
///
/// <para>與 <see cref="ExceptionLogWriter"/> 不同，這裡**可以也應該**使用 <see cref="ILogger"/>：
/// 它不在例外記錄管線內，寄送失敗記成 Error 正好會進「系統例外紀錄」（來源＝背景作業）。</para>
///
/// <para>⚠️ 失敗不重試：多數失敗（帳密錯、主機名錯、收件者被拒）重試永遠不會好，
/// 只會讓錯誤訊息重複出現。使用者重新申請即可。</para>
/// </summary>
public sealed class EmailDispatchWorker : BackgroundService
{
    private readonly ChannelEmailQueue queue;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ILogger<EmailDispatchWorker> logger;
    private readonly IOptionsMonitor<SlowOperationSettings>? slowOptions;
    private readonly LoggingPipelineMonitor? monitor;

    public EmailDispatchWorker(
        ChannelEmailQueue queue,
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        IOptionsMonitor<EmailSettings> emailOptions,
        ILogger<EmailDispatchWorker> logger,
        IOptionsMonitor<SlowOperationSettings>? slowOptions = null,
        LoggingPipelineMonitor? monitor = null)
    {
        this.slowOptions = slowOptions;
        this.monitor = monitor;
        this.queue = queue;
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
        this.emailOptions = emailOptions;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Email dispatch worker started.");

        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchOneAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關機；佇列中尚未寄出的信會遺失（見 IEmailQueue 註解）。
        }

        logger.LogInformation("Email dispatch worker stopped.");
    }

    internal async Task DispatchOneAsync(EmailMessage message, CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, "EmailDispatch", null, null));
        var settings = emailOptions.CurrentValue;

        try
        {
            // 單封信的上限：SmtpClient.Timeout 只管單一次網路操作，連線＋登入＋寄送加總要另外設。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds * 2));

            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await sender.SendAsync(message, timeout.Token);

            // 慢寄信（LOG-21）：只記信件種類，收件者與內容一律不記。
            var threshold = slowOptions?.CurrentValue.ExternalCallMs ?? 0;
            if (SlowOperationSettings.IsSlow(stopwatch.Elapsed, threshold))
            {
                logger.LogWarning(
                    "Slow email dispatch. Kind={Kind}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                    message.Kind, stopwatch.ElapsedMilliseconds, threshold);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queued email dispatch failed. Kind={Kind}, Provider={Provider}", message.Kind, settings.Provider);

            // 告警信寄不出去不會再觸發告警（避免迴圈），只能在系統健康監控看到（LOG-22）。
            if (message.Kind == EmailKinds.ExceptionAlert)
            {
                monitor?.RecordAlertSendFailure();
            }
        }
        finally
        {
            contextAccessor.Clear();
        }
    }
}
