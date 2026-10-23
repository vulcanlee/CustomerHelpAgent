namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 一鍵重置示範資料（<see cref="DemoResetService"/>）成功 commit 之後要一併清空的記憶體狀態（1.0.5 起）。
/// 實作放在 Web（例如對話中暫存的照片 <c>AgentPhotoInbox</c>）：Business 不能參考 Web，改由 Web 以 DI 登記本介面，
/// 重置服務注入 <c>IEnumerable&lt;IDemoResetParticipant&gt;</c> 逐一呼叫——不論從哪裡觸發重置，都不會漏清。
/// 登記方式：<c>services.AddSingleton&lt;IDemoResetParticipant&gt;(sp =&gt; sp.GetRequiredService&lt;實作&gt;())</c>，與原服務共用同一個實例。
/// </summary>
public interface IDemoResetParticipant
{
    /// <summary>資料庫已重置完成時呼叫；應快速、不拋例外（拋出也只記警告，不影響重置結果）。</summary>
    void OnDemoDataReset();
}
