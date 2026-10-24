using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 送件／補件 commit 後觸發 AI 初審（scoped，取代 Business 的 no-op 預設）：只處理 Agent 管道，
/// 建立新的一輪（補件後重新送件是下一個 Attempt）並放進 <see cref="PreReviewQueue"/>。傳統表單管道零 AI，直接略過。
/// 這裡只寫一筆資料、入列就返回，初審由 <see cref="PreReviewWorker"/> 在背景執行。
/// </summary>
public sealed class PreReviewSubmittedHook : IAfterSalesSubmittedHook
{
    private readonly PreReviewRunService runs;
    private readonly PreReviewQueue queue;
    private readonly PreReviewSignal signal;
    private readonly ILogger<PreReviewSubmittedHook> logger;

    public PreReviewSubmittedHook(PreReviewRunService runs, PreReviewQueue queue, PreReviewSignal signal, ILogger<PreReviewSubmittedHook> logger)
    {
        this.runs = runs;
        this.queue = queue;
        this.signal = signal;
        this.logger = logger;
    }

    public async Task OnSubmittedAsync(int requestId, AfterSalesChannel channel)
    {
        if (channel != AfterSalesChannel.Agent)
        {
            return;
        }

        var run = await runs.CreateAsync(requestId);
        if (run is null)
        {
            logger.LogWarning("Pre-review was not created because the request was not found. RequestId={RequestId}", requestId);
            return;
        }

        queue.TryEnqueue(run.Id);
        // 審核頁開著這筆申請時，面板立刻顯示新的一輪（初審中）。
        signal.Publish(requestId);
    }
}
