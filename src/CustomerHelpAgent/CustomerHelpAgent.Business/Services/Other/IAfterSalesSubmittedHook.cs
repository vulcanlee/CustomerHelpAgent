using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 售後申請送件／補件<b>已 commit</b> 之後的掛勾（M4）：Web 以它觸發 AI 初審（只處理 Agent 管道）。
/// ⚠️ 呼叫端（<c>AfterSalesRequestService</c>）把例外記成 Warning 後吞下：申請已經成立，掛勾失敗不可讓送件失敗
/// （顧客端 Agent 的工具在 commit 之後不得回報失敗）。實作要快，不可在這裡同步跑初審。
/// </summary>
public interface IAfterSalesSubmittedHook
{
    Task OnSubmittedAsync(int requestId, AfterSalesChannel channel);
}

/// <summary>預設實作：什麼都不做（沒有註冊掛勾時，例如 Business 的單元測試）。</summary>
public sealed class NoOpAfterSalesSubmittedHook : IAfterSalesSubmittedHook
{
    public static readonly NoOpAfterSalesSubmittedHook Instance = new();

    public Task OnSubmittedAsync(int requestId, AfterSalesChannel channel) => Task.CompletedTask;
}
