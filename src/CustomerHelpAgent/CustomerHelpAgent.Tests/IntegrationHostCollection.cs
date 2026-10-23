namespace CustomerHelpAgent.Tests;

/// <summary>
/// 所有會啟動真實 host（<see cref="ApiTestApplicationFactory"/> 與其子類）的測試類別共用此 Collection，**不可平行**。
///
/// <para>為什麼：兩個行程層級的共用狀態會在 host 並行啟停時互相踩到 ——</para>
/// <list type="number">
/// <item><c>Program.cs</c> 結束時呼叫 <c>NLog.LogManager.Shutdown()</c>，把全行程的 NLog 設定清掉；
/// 另一個剛好正在啟動的 host 讀不到設定，改去 appsettings 的 <c>NLog</c> 區段載入，
/// 碰到 <c>BasePath</c> 就丟 <c>NLogConfigurationException</c>，整個類別的測試一起失敗。</item>
/// <item><see cref="ApiTestApplicationFactory"/> 在建構時寫入、在 Dispose 時清除<b>行程環境變數</b>，
/// 並行時一個 factory 的清除會抹掉另一個 factory 的設定。</item>
/// </list>
///
/// <para>0.9.60 新增兩個 host 測試類別後這個競態從偶發變成幾乎每次都發生，因此收斂到這裡。
/// 新增會啟動 host 的測試類別時，一律加上 <c>[Collection(nameof(IntegrationHostCollection))]</c>。</para>
/// </summary>
[CollectionDefinition(nameof(IntegrationHostCollection), DisableParallelization = true)]
public sealed class IntegrationHostCollection
{
}
