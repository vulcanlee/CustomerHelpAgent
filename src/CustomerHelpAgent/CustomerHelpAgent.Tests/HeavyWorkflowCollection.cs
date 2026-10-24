namespace CustomerHelpAgent.Tests;

/// <summary>
/// 會跑多 Agent 交接（MAF Handoff Workflow）的測試類別共用此 Collection，<b>不與其他測試平行</b>（M5）。
///
/// <para>為什麼：Handoff Workflow 以 <c>InProcessExecution.OffThread</c> 在背景執行緒執行（模型鏈、工具、checkpoint），
/// 同時讀串流的一側也在發事件，CPU 負載重；和 bUnit 元件測試同時跑時，元件測試的等待偶爾逾時（完整測試偶發失敗）。
/// xUnit 會在其他平行測試結束後才依序執行這個 Collection。</para>
/// <para>成員：<c>MafHandoffBehaviorTests</c>（框架行為；checkpoint 在記憶體）、<c>AfterSalesHandoffRunnerTests</c>
/// （Runner；資料庫用 <see cref="TestFileDbContextFactory"/>）、<c>ShopAssistantViewTests</c>（線上客服頁的 bUnit 測試，
/// 含多 Agent 模式的模式切換、交接呈現與回放；整個類別改用暫存檔資料庫）。</para>
/// 新增會跑 Handoff Workflow 的測試類別時，一律加上 <c>[Collection(nameof(HeavyWorkflowCollection))]</c>；
/// 會在背景執行緒存取資料庫的，資料庫改用 <see cref="TestFileDbContextFactory"/>（共用 in-memory 連線並行時會出錯）。
/// </summary>
[CollectionDefinition(nameof(HeavyWorkflowCollection), DisableParallelization = true)]
public sealed class HeavyWorkflowCollection
{
}
