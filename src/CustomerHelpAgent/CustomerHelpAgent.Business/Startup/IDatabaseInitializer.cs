namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 啟動時把資料庫準備好：套用 migration、設定 WAL、依序執行所有 <see cref="IDatabaseSeeder"/>。
/// 由 Program.cs 在 <c>app.Run()</c> 之前明確呼叫一次（刻意不做成 HostedService：
/// 要保證資料庫就緒之前不接任何請求）。
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
