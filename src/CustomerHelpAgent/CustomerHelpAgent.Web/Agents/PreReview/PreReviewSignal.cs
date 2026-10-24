using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 初審進度的即時訊號（singleton，M4）：只告訴畫面「這筆申請的初審有變動」，畫面自己重新查詢（訊號不帶內容）。
/// 訂閱、派送與防洩漏的細節在 <see cref="KeyedSignalHub{TKey}"/>（與站內通知訊號共用）：
/// 元件 Dispose 時一定要釋放訂閱；派送不帶背景 Workflow 的執行情境。單一行程內有效，多個行程之間不互通。
/// </summary>
public sealed class PreReviewSignal
{
    private readonly KeyedSignalHub<int> hub;

    public PreReviewSignal(ILogger<PreReviewSignal> logger)
    {
        hub = new KeyedSignalHub<int>(logger, "PreReview");
    }

    /// <summary>目前的訂閱數（測試與診斷用）。</summary>
    public int SubscriberCount => hub.SubscriberCount;

    /// <summary>目前有訂閱的申請數（測試用：取消訂閱後應歸零）。</summary>
    public int KeyCount => hub.KeyCount;

    /// <summary>訂閱某筆申請的初審變動；回傳的物件 Dispose 時取消訂閱（元件 Dispose 時一定要呼叫）。</summary>
    public IDisposable Subscribe(int requestId, Func<Task> handler) => hub.Subscribe(requestId, handler);

    public void Publish(int requestId) => hub.Publish(requestId);
}
