using System.Text.Json.Serialization.Metadata;
using AntDesign;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 審核窗的「AI 初審」面板（M4）：顯示最新一輪初審的流程圖、AI 建議與三個初審結論，並可「重新初審」。
/// 訂閱 <see cref="PreReviewSignal"/> 即時更新：先訂閱再載入；Dispose 後的訊號一律略過並取消訂閱。
/// 載入到最新一輪後以 <see cref="OnRunChanged"/> 通知審核頁（決策按鈕旁要顯示「初審未完成」「上次執行失敗」）。
/// </summary>
public partial class PreReviewPanel : IDisposable
{
    [Inject]
    public PreReviewRunService PreReviewRunService { get; set; } = default!;

    [Inject]
    public PreReviewSignal PreReviewSignal { get; set; } = default!;

    [Inject]
    public AfterSalesReviewCoordinator Coordinator { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<PreReviewPanel> Logger { get; set; } = default!;

    [Parameter, EditorRequired]
    public int RequestId { get; set; }

    /// <summary>申請仍是「已提交」時才可重新初審。</summary>
    [Parameter]
    public bool Retryable { get; set; }

    /// <summary>載入到（最新一輪）初審後通知審核頁；沒有初審時傳 null。</summary>
    [Parameter]
    public EventCallback<PreReviewRun?> OnRunChanged { get; set; }

    private IDisposable? subscription;
    private int subscribedRequestId;
    private bool disposed;
    private int loadSequence;
    private bool busy;

    private PreReviewRun? run;
    private PreReviewFinding? photoFinding;
    private PreReviewFinding? ruleFinding;
    private PreReviewFinding? historyFinding;
    private PhotoReviewResult? photo;
    private RuleCheckResult? rule;
    private HistoryReviewResult? history;
    private PreReviewSummary? summary;

    protected override async Task OnParametersSetAsync()
    {
        if (RequestId == subscribedRequestId)
        {
            return;
        }

        // 先訂閱再載入：載入途中被關閉時 Dispose 也收得到這個訂閱；關閉後的訊號一律略過。
        subscription?.Dispose();
        subscribedRequestId = RequestId;
        subscription = PreReviewSignal.Subscribe(RequestId, () => InvokeAsync(async () =>
        {
            if (disposed)
            {
                return;
            }

            await LoadAsync();
            StateHasChanged();
        }));
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var sequence = ++loadSequence;
        try
        {
            var latest = await PreReviewRunService.GetLatestForRequestAsync(RequestId);
            if (disposed || sequence != loadSequence)
            {
                return;
            }

            run = latest;
            photoFinding = ParseFinding(latest?.PhotoJson);
            ruleFinding = ParseFinding(latest?.RuleJson);
            historyFinding = ParseFinding(latest?.HistoryJson);
            photo = ParseDetail(photoFinding, PreReviewJsonContext.Default.PhotoReviewResult);
            rule = ParseDetail(ruleFinding, PreReviewJsonContext.Default.RuleCheckResult);
            history = ParseDetail(historyFinding, PreReviewJsonContext.Default.HistoryReviewResult);
            summary = ParseDetail(ParseFinding(latest?.SummaryJson), PreReviewJsonContext.Default.PreReviewSummary);
            await OnRunChanged.InvokeAsync(latest);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load pre-review panel. RequestId={RequestId}", RequestId);
        }
    }

    private async Task OnRetryAsync()
    {
        busy = true;
        try
        {
            var result = await Coordinator.RetryAsync(RequestId);
            if (result.Success)
            {
                ViewNotification.Warning(NotificationService, result.Message);
            }
            else
            {
                ViewNotification.Error(NotificationService, result.Message);
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Pre-review retry failed. RequestId={RequestId}", RequestId);
            ViewNotification.UnexpectedError(NotificationService, "重新初審時發生未預期的錯誤，請稍後再試。", ex);
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>節點結論欄存的是序列化的 <see cref="PreReviewFinding"/>；壞掉的資料當作沒有（畫面不因此當掉）。</summary>
    private static PreReviewFinding? ParseFinding(string? json) => PreReviewFindingReader.ParseFinding(json);

    private static T? ParseDetail<T>(PreReviewFinding? finding, JsonTypeInfo<T> typeInfo)
        where T : class
        => PreReviewFindingReader.ParseDetail(finding, typeInfo);

    private static string HeadlineOf(PreReviewFinding? finding)
        => string.IsNullOrWhiteSpace(finding?.Headline) ? "（尚無結論）" : finding.Headline;

    private static string PhotoVerdictText(string verdict) => verdict switch
    {
        PhotoReviewResult.Consistent => "照片與描述一致",
        PhotoReviewResult.Inconsistent => "照片與描述不一致",
        _ => "照片不清楚",
    };

    public void Dispose()
    {
        disposed = true;
        subscription?.Dispose();
    }
}
