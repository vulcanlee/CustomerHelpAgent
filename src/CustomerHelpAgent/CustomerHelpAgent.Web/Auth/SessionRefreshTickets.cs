using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>一張換發 Cookie 的 ticket 的內容。</summary>
public sealed record SessionRefreshTicket(string Id, int UserId, string OldStamp, string NewStamp);

/// <summary>
/// 「這台裝置保持登入」用的一次性 ticket（0.9.103 起）。
///
/// 使用者在互動頁（沒有 HttpContext，換不了 Cookie）改了自己的密碼或角色之後，工作階段版本已經換掉；
/// 頁面拿一張 ticket 整頁導到 <c>/Auths/RefreshSession</c>，由那一頁換發新 Cookie。其他裝置維持登出。
/// ⚠️ ticket 只有 2 分鐘、只能用一次，而且換發時必須帶著原本的 Cookie（同一個使用者、舊版本相符）—— 否則別人可以把自己的 ticket 連結寄給你，讓你登入成他。
/// </summary>
public sealed class SessionRefreshTicketService
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ITimeLimitedDataProtector protector;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SessionRefreshTicketService> logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> consumed = new(StringComparer.Ordinal);

    public SessionRefreshTicketService(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider, ILogger<SessionRefreshTicketService> logger)
    {
        protector = dataProtectionProvider.CreateProtector("CustomerHelpAgent.SessionRefresh.v1").ToTimeLimitedDataProtector();
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public string Issue(int userId, string oldStamp, string newStamp)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return protector.Protect($"{id}|{userId}|{oldStamp}|{newStamp}", timeProvider.GetUtcNow().Add(Lifetime));
    }

    /// <summary>驗證並作廢；格式錯、過期、用過一律回 null。</summary>
    public SessionRefreshTicket? Consume(string? protectedTicket)
    {
        if (string.IsNullOrWhiteSpace(protectedTicket))
        {
            return null;
        }

        string payload;
        try
        {
            payload = protector.Unprotect(protectedTicket, out _);
        }
        catch (CryptographicException)
        {
            logger.LogInformation("Session refresh ticket rejected because it is invalid or expired.");
            return null;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var userId))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var expired in consumed.Where(x => x.Value < now).Select(x => x.Key).ToList())
        {
            consumed.TryRemove(expired, out _);
        }

        if (!consumed.TryAdd(parts[0], now.Add(Lifetime)))
        {
            logger.LogWarning("Session refresh ticket rejected because it was already used. UserId={UserId}", userId);
            return null;
        }

        return new SessionRefreshTicket(parts[0], userId, parts[2], parts[3]);
    }
}

/// <summary>
/// 互動頁在「自己的工作階段版本被換掉」之後呼叫（0.9.103 起）：變更自己的密碼、管理員修改自己的角色或管理員身分。
/// 版本確實變了而且帳號仍可登入，就整頁導到 <c>/Auths/RefreshSession</c> 換發這台裝置的 Cookie。
/// </summary>
public sealed class SessionRefreshNavigator
{
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly ISecurityStampService securityStampService;
    private readonly SessionRefreshTicketService ticketService;
    private readonly NavigationManager navigationManager;
    private readonly ILogger<SessionRefreshNavigator> logger;

    public SessionRefreshNavigator(
        AuthenticationStateProvider authenticationStateProvider,
        ISecurityStampService securityStampService,
        SessionRefreshTicketService ticketService,
        NavigationManager navigationManager,
        ILogger<SessionRefreshNavigator> logger)
    {
        this.authenticationStateProvider = authenticationStateProvider;
        this.securityStampService = securityStampService;
        this.ticketService = ticketService;
        this.navigationManager = navigationManager;
        this.logger = logger;
    }

    /// <summary>需要換發就導頁並回 true；版本沒變或帳號已不能登入回 false（呼叫端照原本的流程走）。</summary>
    public async Task<bool> KeepSignedInAsync(string returnUrl)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var userId) || userId <= 0)
        {
            return false;
        }

        var oldStamp = principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType) ?? string.Empty;
        var state = await securityStampService.GetStateAsync(userId, TimeSpan.Zero);
        if (!state.IsActive || SecurityStamps.Matches(oldStamp, state.SecurityStamp) || string.IsNullOrEmpty(oldStamp))
        {
            return false;
        }

        var ticket = ticketService.Issue(userId, oldStamp, state.SecurityStamp);
        logger.LogInformation("Refreshing the login cookie of the current device after a session change. UserId={UserId}", userId);
        navigationManager.NavigateTo(
            $"{SecurityStampCookieEvents.RefreshSessionPath}?ticket={Uri.EscapeDataString(ticket)}&returnUrl={Uri.EscapeDataString(returnUrl)}",
            forceLoad: true);
        return true;
    }
}
