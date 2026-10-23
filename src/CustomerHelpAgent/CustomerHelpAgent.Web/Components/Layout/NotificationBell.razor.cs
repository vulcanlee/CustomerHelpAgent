using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Web.Components.Auths;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 上方列的通知鈴鐺（0.9.100 起）。
///
/// 更新來源有兩個：同一個行程內發出的通知經 <see cref="INotificationSignal"/> 即時推送；別的行程（IIS 重疊回收、多台主機）發的
/// 靠每 60 秒輪詢補上。⚠️ Dispose 時一定要取消訂閱與停掉計時器（singleton 會讓這個元件一直活著）。
/// 連結只導向站內網址（經 <see cref="ReturnUrlGuard"/> 檢查，檢查後改變了就不導向）。
/// 0.9.125 起每則可刪除（×）、「清除已讀」刪除自己所有已讀的通知；都不跳確認（只動自己的，通知本來就會過期）。
/// </summary>
public partial class NotificationBell : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal const int ListSize = 10;

    private readonly CancellationTokenSource disposing = new();
    private IDisposable? subscription;
    private int userId;
    private int unreadCount;
    private bool isOpen;
    private List<NotificationAdapterModel> items = [];

    private bool hasRead;

    [Inject]
    public NotificationQueryService QueryService { get; set; } = default!;

    [Inject]
    public INotificationSignal Signal { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<NotificationBell> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        userId = CurrentUserService.CurrentUser.Id;
        if (userId <= 0)
        {
            return;
        }

        subscription = Signal.Subscribe(userId, () => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        }));
        await RefreshAsync();
        _ = PollAsync(disposing.Token);
    }

    /// <summary>「99+」：超過 99 則就不再顯示確切數字。</summary>
    internal static string BadgeText(int count) => count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval, TimeProvider);
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
            Logger.LogDebug("Notification bell polling stopped.");
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            unreadCount = await QueryService.GetUnreadCountAsync(userId);
            if (isOpen)
            {
                items = await QueryService.GetLatestAsync(userId, ListSize);
                hasRead = await QueryService.HasReadAsync(userId);
            }
        }
        catch (Exception ex)
        {
            // 鈴鐺讀不到不該讓整個版面壞掉；下一次輪詢再試。
            Logger.LogWarning(ex, "Failed to refresh notifications.");
        }
    }

    private async Task ToggleAsync()
    {
        isOpen = !isOpen;
        if (isOpen)
        {
            await RefreshAsync();
        }
    }

    private async Task OpenAsync(NotificationAdapterModel item)
    {
        if (item.ReadAtUtc is null)
        {
            await QueryService.MarkReadAsync(userId, item.Id);
        }

        isOpen = false;
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(item.Link) && ReturnUrlGuard.Sanitize(item.Link) == item.Link)
        {
            NavigationManager.NavigateTo(item.Link);
        }
    }

    private async Task MarkAllReadAsync()
    {
        await QueryService.MarkAllReadAsync(userId);
        await RefreshAsync();
    }

    private async Task DeleteAsync(NotificationAdapterModel item)
    {
        await QueryService.DeleteAsync(userId, item.Id);
        await RefreshAsync();
    }

    private async Task ClearReadAsync()
    {
        await QueryService.DeleteReadAsync(userId);
        await RefreshAsync();
    }

    private string ToLocalText(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeProvider.LocalTimeZone).ToString("yyyy-MM-dd HH:mm");

    public void Dispose()
    {
        subscription?.Dispose();
        disposing.Cancel();
        disposing.Dispose();
    }
}
