using System.Threading.Channels;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// 背景寄信佇列（有界 Channel）。消費端是 <see cref="EmailDispatchWorker"/>。
///
/// <para>⚠️ FullMode 用 <see cref="BoundedChannelFullMode.Wait"/> 搭配 <c>TryWrite</c>，
/// **不要改成 DropWrite／DropOldest**：Drop 系列模式下 <c>TryWrite</c> 永遠回傳 true，
/// 信被丟掉了呼叫端也不會知道。Wait 模式下佇列滿時 <c>TryWrite</c> 會老實回 false。</para>
/// </summary>
public sealed class ChannelEmailQueue : IEmailQueue
{
    public const int DefaultCapacity = 100;

    private readonly Channel<EmailMessage> channel;
    private readonly int capacity;
    private readonly ILogger<ChannelEmailQueue> logger;

    public ChannelEmailQueue(ILogger<ChannelEmailQueue> logger)
        : this(logger, DefaultCapacity)
    {
    }

    internal ChannelEmailQueue(ILogger<ChannelEmailQueue> logger, int capacity)
    {
        this.logger = logger;
        this.capacity = capacity;
        channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<EmailMessage> Reader => channel.Reader;

    public bool TryEnqueue(EmailMessage message)
    {
        if (channel.Writer.TryWrite(message))
        {
            return true;
        }

        logger.LogWarning("Email queue is full and the message was dropped. Kind={Kind}, Capacity={Capacity}", message.Kind, capacity);
        return false;
    }
}
