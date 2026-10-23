using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Web.Components.Auths;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Web.Components.Views.Dashboard;

/// <summary>
/// 首頁「我的通知」（0.9.106 起）：未讀數與最新 5 則。同一個行程發的通知與標為已讀經 <see cref="INotificationSignal"/> 即時更新；
/// 別的行程（IIS 重疊回收、多台主機）發的與鈴鐺一樣靠每 60 秒輪詢補上（0.9.122 起，之前要重新整理）。
/// ⚠️ Dispose 時一定要取消訂閱與停掉計時器（訊號是 singleton）。全部通知與「全部標為已讀」在右上角的鈴鐺。
/// </summary>
public partial class MyNotificationsWidget : IDisposable
{
    internal const int ListSize = 5;

    private readonly CancellationTokenSource disposing = new();
    private IDisposable? subscription;
    private bool loaded;
    private int userId;
    private int unreadCount;
    private List<NotificationAdapterModel> items = [];

    [Inject]
    public NotificationQueryService QueryService { get; set; } = default!;

    [Inject]
    public INotificationSignal Signal { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<MyNotificationsWidget> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        userId = CurrentUserService.CurrentUser.Id;
        subscription = Signal.Subscribe(userId, () => InvokeAsync(async () =>
        {
            await LoadAsync();
            StateHasChanged();
        }));
        await LoadAsync();
        _ = PollAsync(disposing.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(NotificationBell.PollInterval, TimeProvider);
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
            Logger.LogDebug("Notification widget polling stopped.");
        }
    }

    /// <summary>輪詢用：讀不到不該讓首頁壞掉，下一次輪詢再試。</summary>
    private async Task RefreshAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh the notification widget.");
        }
    }

    private async Task LoadAsync()
    {
        unreadCount = await QueryService.GetUnreadCountAsync(userId);
        items = await QueryService.GetLatestAsync(userId, ListSize);
        loaded = true;
    }

    private async Task MarkReadAsync(NotificationAdapterModel item)
    {
        if (item.ReadAtUtc is null)
        {
            await QueryService.MarkReadAsync(userId, item.Id);
        }
    }

    /// <summary>只導向站內網址（與鈴鐺相同的規則）。</summary>
    private static string? SafeLink(string? link)
        => string.IsNullOrWhiteSpace(link) || ReturnUrlGuard.Sanitize(link) != link ? null : link;

    private string ToLocalText(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeProvider.LocalTimeZone).ToString("MM-dd HH:mm");

    public void Dispose()
    {
        subscription?.Dispose();
        disposing.Cancel();
        disposing.Dispose();
    }
}
