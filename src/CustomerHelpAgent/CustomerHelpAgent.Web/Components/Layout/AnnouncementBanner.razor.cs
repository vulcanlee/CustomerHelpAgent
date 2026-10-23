using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 每個登入後頁面上方的公告橫幅（0.9.100 起）。放在 <c>MainLayout</c>：版面整個連線只建立一次，換頁不會重新查詢。
///
/// 公告清單來自 singleton 的 <see cref="AnnouncementCache"/>；使用者的角色 Id 與已關閉的公告在連線開始時讀一次。
/// 管理頁存檔時經 <see cref="INotificationSignal"/> 即時更新，另外每 60 秒重新判斷一次（公告到了開始或結束時間、別的行程的修改）。
/// </summary>
public partial class AnnouncementBanner : IDisposable
{
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource disposing = new();
    private IDisposable? subscription;
    private int userId;
    private HashSet<int> roleIds = [];
    private HashSet<int> dismissed = [];
    private List<ActiveAnnouncement> visible = [];

    [Inject]
    public AnnouncementCache Cache { get; set; } = default!;

    [Inject]
    public AnnouncementService AnnouncementService { get; set; } = default!;

    [Inject]
    public INotificationSignal Signal { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<AnnouncementBanner> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        userId = CurrentUserService.CurrentUser.Id;
        if (userId <= 0)
        {
            return;
        }

        try
        {
            roleIds = await AnnouncementService.GetUserRoleIdsAsync(userId);
            dismissed = await AnnouncementService.GetDismissedIdsAsync(userId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load announcement state for the current user.");
        }

        subscription = Signal.SubscribeAnnouncements(() => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        }));
        await RefreshAsync();
        _ = RecheckAsync(disposing.Token);
    }

    /// <summary>從公告清單挑出要顯示的（時間、對象、沒被關閉）。</summary>
    internal static List<ActiveAnnouncement> Select(
        IEnumerable<ActiveAnnouncement> all, DateTime nowUtc, IReadOnlyCollection<int> roleIds, IReadOnlyCollection<string> teamNames, IReadOnlySet<int> dismissed)
        => all.Where(x => x.IsShowingAt(nowUtc) && x.IsFor(roleIds, teamNames) && !dismissed.Contains(x.Id))
            .OrderByDescending(x => x.StartAtUtc)
            .ToList();

    private async Task RecheckAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecheckInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 元件已關閉。
            Logger.LogDebug("Announcement banner recheck stopped.");
        }
    }

    private async Task RefreshAsync()
    {
        var all = await Cache.GetAsync();
        visible = Select(all, TimeProvider.GetUtcNow().UtcDateTime, roleIds, CurrentUserService.CurrentUser.TeamList, dismissed);
    }

    private async Task DismissAsync(ActiveAnnouncement item)
    {
        dismissed.Add(item.Id);
        visible.Remove(item);
        try
        {
            await AnnouncementService.DismissAsync(userId, item.Id);
        }
        catch (Exception ex)
        {
            // 沒存進去只是下次登入還會再看到一次。
            Logger.LogWarning(ex, "Failed to save announcement dismissal. AnnouncementId={AnnouncementId}", item.Id);
        }
    }

    public void Dispose()
    {
        subscription?.Dispose();
        disposing.Cancel();
        disposing.Dispose();
    }
}
