using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 排程作業的描述（singleton）。排程器、設定驗證器與管理頁都只讀描述；作業本身（<see cref="JobType"/>，scoped）
/// 每次執行才在新的 scope 解析。
///
/// ⚠️ 不可以從根容器列舉 <see cref="IScheduledJob"/>：排程器與驗證器都是 singleton，作業持有 scoped 的 DbContext，
/// 從根容器解析會變成一個永遠不釋放、追蹤越積越多的 DbContext（開發環境則直接啟動失敗）。
/// </summary>
/// <param name="Name">穩定的識別名稱（英文字母與數字）：用在設定鍵、鎖檔名稱、執行紀錄與稽核。上線後不要改。</param>
/// <param name="DisplayName">管理頁顯示的名稱。</param>
/// <param name="Description">管理頁顯示的說明。</param>
/// <param name="DefaultCron">預設的 cron（5 欄位：分 時 日 月 星期，依伺服器本地時區）；可由 <c>ScheduledJobSettings:Jobs:名稱:Cron</c> 覆寫。</param>
/// <param name="JobType">實作 <see cref="IScheduledJob"/> 的類別。</param>
public sealed partial record ScheduledJobDescriptor(string Name, string DisplayName, string Description, string DefaultCron, Type JobType)
{
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex NamePattern();
}
