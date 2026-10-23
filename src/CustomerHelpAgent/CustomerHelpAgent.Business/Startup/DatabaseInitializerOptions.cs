namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// <see cref="DatabaseInitializer"/> 的參數。目前不從設定檔讀取：唯一的值是跨行程鎖的等待上限，
/// 正式環境沒有理由調整，測試則以建構子傳入較短的值。
/// </summary>
public sealed class DatabaseInitializerOptions
{
    /// <summary>
    /// 等待其他行程釋放初始化鎖的上限。
    ///
    /// ⚠️ 不要調到 2 分鐘以上：IIS in-process 的 <c>startupTimeLimit</c> 預設 120 秒，
    /// 超過會被 IIS 判定啟動失敗（500.37）並砍掉行程，可能剛好砍在 migration 中途。
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
