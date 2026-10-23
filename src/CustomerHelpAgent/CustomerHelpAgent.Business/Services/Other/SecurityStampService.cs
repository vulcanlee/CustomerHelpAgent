using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>產生新的工作階段版本（0.9.103 起）。</summary>
public static class SecurityStamps
{
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>兩邊都有值而且相同才算相符；空字串（尚未設定）一律不符。</summary>
    public static bool Matches(string? claimStamp, string? currentStamp)
        => !string.IsNullOrEmpty(claimStamp) && !string.IsNullOrEmpty(currentStamp) && string.Equals(claimStamp, currentStamp, StringComparison.Ordinal);
}

/// <summary>工作階段檢查需要的使用者狀態：目前的版本與是否仍可登入（存在、未刪除、啟用）。</summary>
public sealed record UserSessionState(string SecurityStamp, bool IsActive);

/// <summary>
/// 工作階段版本的讀取、輪替與快取（0.9.103 起，singleton）。
///
/// Cookie 驗證器每次 HTTP 請求都會問一次，所以讀取有快取（存活時間由呼叫端決定）；
/// ⚠️ 輪替一律經 <see cref="RotateAsync"/> 或在實體上設定後呼叫 <see cref="Invalidate"/>，同一個行程的快取立即失效。
/// 別的行程（IIS 重疊回收、多台主機）最晚在快取存活時間後看到新版本。
/// </summary>
public interface ISecurityStampService
{
    /// <summary>目前狀態；使用者不存在或已刪除時回 <see cref="UserSessionState.IsActive"/> = false。<paramref name="maxAge"/> 為零時一定查資料庫。</summary>
    Task<UserSessionState> GetStateAsync(int userId, TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// 這個工作階段是否仍有效：帳號可登入、版本相符。快取裡的版本不符時**再查一次資料庫**才判定失效 ——
    /// 版本剛換過（例如剛被停用又啟用、剛改密碼後重新登入），快取還是舊的，不重查會把新的登入誤判成失效。
    /// </summary>
    /// <param name="requireActive">false 時只比版本（呼叫端自己處理停用，例如換頁檢查要給停用帳號另一個結果）。</param>
    Task<bool> IsValidAsync(int userId, string? claimStamp, TimeSpan maxAge, bool requireActive = true, CancellationToken cancellationToken = default);

    /// <summary>換一個新版本（只改這一欄，不動 ConcurrencyStamp），讓這位使用者所有工作階段失效。</summary>
    Task RotateAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>資料庫裡還是空字串（重疊回收時舊版程式新增的列）就補一個，回傳目前的值。</summary>
    Task<string> EnsureAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>清掉這位使用者的快取（在實體上直接換版本並存檔之後呼叫）。</summary>
    void Invalidate(int userId);
}

public sealed class SecurityStampService : ISecurityStampService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SecurityStampService> logger;
    private readonly ConcurrentDictionary<int, (UserSessionState State, DateTimeOffset FetchedAt)> cache = new();

    public SecurityStampService(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<SecurityStampService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<UserSessionState> GetStateAsync(int userId, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (maxAge > TimeSpan.Zero && cache.TryGetValue(userId, out var cached) && now - cached.FetchedAt < maxAge)
        {
            return cached.State;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.MyUser.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.SecurityStamp, x.Status })
            .FirstOrDefaultAsync(cancellationToken);
        var state = row is null ? new UserSessionState(string.Empty, false) : new UserSessionState(row.SecurityStamp, row.Status);
        cache[userId] = (state, now);
        return state;
    }

    public async Task<bool> IsValidAsync(int userId, string? claimStamp, TimeSpan maxAge, bool requireActive = true, CancellationToken cancellationToken = default)
    {
        bool Valid(UserSessionState s) => (s.IsActive || !requireActive) && SecurityStamps.Matches(claimStamp, s.SecurityStamp);

        var state = await GetStateAsync(userId, maxAge, cancellationToken);
        if (Valid(state))
        {
            return true;
        }

        if (maxAge > TimeSpan.Zero)
        {
            state = await GetStateAsync(userId, TimeSpan.Zero, cancellationToken);
        }

        return Valid(state);
    }

    public async Task RotateAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.MyUser.IgnoreQueryFilters()
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, SecurityStamps.New()), cancellationToken);
        Invalidate(userId);
        logger.LogInformation("Security stamp rotated. UserId={UserId}, Rows={Rows}", userId, rows);
    }

    public async Task<string> EnsureAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.MyUser
            .Where(x => x.Id == userId && x.SecurityStamp == string.Empty)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, SecurityStamps.New()), cancellationToken);
        if (updated > 0)
        {
            Invalidate(userId);
            logger.LogInformation("Security stamp was empty; assigned a new one. UserId={UserId}", userId);
        }

        return await context.MyUser.AsNoTracking().Where(x => x.Id == userId).Select(x => x.SecurityStamp).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    public void Invalidate(int userId) => cache.TryRemove(userId, out _);
}
