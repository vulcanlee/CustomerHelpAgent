using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Email;

namespace CustomerHelpAgent.Tests;

public sealed class EmailQueueTests
{
    /// <summary>
    /// 守住 FullMode = Wait：Drop 系列模式下 TryWrite 永遠回 true，
    /// 信被丟掉呼叫端也不會知道。
    /// </summary>
    [Fact]
    public void TryEnqueue_WhenFull_ShouldReturnFalse()
    {
        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance, capacity: 2);

        Assert.True(queue.TryEnqueue(CreateMessage("a")));
        Assert.True(queue.TryEnqueue(CreateMessage("b")));
        Assert.False(queue.TryEnqueue(CreateMessage("c")));
    }

    [Fact]
    public async Task Worker_ShouldSendQueuedMessages()
    {
        var sender = new CapturingEmailSender();
        var (queue, worker) = CreateWorker(sender);

        queue.TryEnqueue(CreateMessage("first"));
        queue.TryEnqueue(CreateMessage("second"));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent.Count == 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["first", "second"], sender.Sent.Select(x => x.Subject));
    }

    /// <summary>一封寄失敗不可讓背景服務停掉，後面的信照樣要寄。</summary>
    [Fact]
    public async Task Worker_WhenOneMessageFails_ShouldKeepSendingTheNext()
    {
        var sender = new CapturingEmailSender(message =>
            message.Subject == "broken" ? new InvalidOperationException("SMTP down") : null);
        var (queue, worker) = CreateWorker(sender);

        queue.TryEnqueue(CreateMessage("broken"));
        queue.TryEnqueue(CreateMessage("ok"));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent.Count == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("ok", Assert.Single(sender.Sent).Subject);
    }

    private static (ChannelEmailQueue Queue, EmailDispatchWorker Worker) CreateWorker(IEmailSender sender)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sender);
        var provider = services.BuildServiceProvider();

        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        var worker = new EmailDispatchWorker(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ExceptionContextAccessor(),
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings()),
            NullLogger<EmailDispatchWorker>.Instance);

        return (queue, worker);
    }

    private static EmailMessage CreateMessage(string subject) =>
        new("alice@example.com", subject, "<p>x</p>", "x", EmailKinds.Test);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "背景寄信器在 5 秒內沒有處理完佇列。");
            await Task.Delay(20);
        }
    }
}
