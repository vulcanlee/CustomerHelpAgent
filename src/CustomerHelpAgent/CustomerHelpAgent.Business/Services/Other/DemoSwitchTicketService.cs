using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>展示身分切換票券的用途。</summary>
public enum DemoSwitchPurpose
{
    /// <summary>展示者切換成示範帳號。</summary>
    SwitchIdentity,

    /// <summary>示範帳號切回展示者。</summary>
    SwitchBack,
}

/// <summary>
/// 一張展示身分切換票券的內容。<see cref="TargetAccount"/> 只在 <see cref="DemoSwitchPurpose.SwitchIdentity"/> 時有值。
/// <see cref="ReturnUrl"/>：切換成功後要去的站內路徑（例如展示控制台「準備此劇本」的 <c>/shop/assistant</c>）；
/// 兌換頁只接受站內路徑，其餘照預設（顧客 <c>/shop</c>、其他 <c>/App</c>）。
/// </summary>
public sealed record DemoSwitchTicket(int CreatorUserId, DemoSwitchPurpose Purpose, string? TargetAccount, string? ReturnUrl = null);

/// <summary>
/// 展示身分切換的一次性票券（1.0.5 起，Singleton，只存在記憶體）。
///
/// 版面與展示控制台是互動元件，沒有 HttpContext、換不了 Cookie；它們取一張票券後整頁導到 <c>/Auths/DemoSwitch</c>，由那一頁換發 Cookie。
/// ⚠️ 票券只有 <see cref="Lifetime"/>、只能兌換一次，並綁定建立者 —— 兌換頁必須另外確認票券的建立者等於目前 Cookie 的使用者，
/// 否則別人可以把自己的票券連結寄給你。本服務不判斷誰可以切換（管理員、示範模式、示範帳號），那些由兌換頁檢查。
/// 行程重啟後所有票券失效（記憶體保存），對 60 秒的票券沒有影響。
/// </summary>
public sealed class DemoSwitchTicketService
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly TimeProvider timeProvider;
    private readonly ILogger<DemoSwitchTicketService> logger;
    private readonly ConcurrentDictionary<string, (DemoSwitchTicket Ticket, DateTimeOffset ExpiresAt)> tickets = new(StringComparer.Ordinal);

    public DemoSwitchTicketService(TimeProvider timeProvider, ILogger<DemoSwitchTicketService> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>展示者要切換成 <paramref name="targetAccount"/>；<paramref name="returnUrl"/> 是切換後要去的站內路徑（null＝預設）。</summary>
    public string Issue(int presenterUserId, string targetAccount, string? returnUrl = null)
        => Add(new DemoSwitchTicket(presenterUserId, DemoSwitchPurpose.SwitchIdentity, targetAccount, returnUrl));

    /// <summary>目前（已切換的）使用者要切回展示者；展示者是誰由 Cookie 的展示者 claim 決定，不寫在票券上。</summary>
    public string IssueSwitchBack(int currentUserId)
        => Add(new DemoSwitchTicket(currentUserId, DemoSwitchPurpose.SwitchBack, null));

    /// <summary>兌換並作廢；不存在、已用過、已過期一律回 false。</summary>
    public bool TryRedeem(string? ticket, [NotNullWhen(true)] out DemoSwitchTicket? redeemed)
    {
        redeemed = null;
        if (string.IsNullOrEmpty(ticket) || !tickets.TryRemove(ticket, out var entry))
        {
            logger.LogInformation("Demo switch ticket rejected because it is unknown or already used.");
            return false;
        }

        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            logger.LogInformation("Demo switch ticket rejected because it expired. CreatorUserId={CreatorUserId}", entry.Ticket.CreatorUserId);
            return false;
        }

        redeemed = entry.Ticket;
        return true;
    }

    private string Add(DemoSwitchTicket ticket)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in tickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList())
        {
            tickets.TryRemove(expired, out _);
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[id] = (ticket, now.Add(Lifetime));
        logger.LogInformation("Demo switch ticket issued. CreatorUserId={CreatorUserId}, Purpose={Purpose}", ticket.CreatorUserId, ticket.Purpose);
        return id;
    }
}
