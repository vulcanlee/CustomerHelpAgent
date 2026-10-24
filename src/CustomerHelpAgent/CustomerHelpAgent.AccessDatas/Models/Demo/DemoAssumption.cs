namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 效益對照頁的假設值（M6）：固定單列（<see cref="Id"/> = 1），由 <c>DemoAssumptionService</c> 讀寫；
/// 沒有資料時服務回傳下列預設值。這些是「假設」不是實測，頁面與匯出都要標示「假設／推估」。
/// 數值型別用 double（與 <c>TokenUsageLog.CostTwd</c> 一致；SQLite 把 decimal 存成 TEXT）。
/// 重置示範資料不會刪除這一列。
/// </summary>
public class DemoAssumption
{
    /// <summary>固定為 <see cref="SingletonId"/>，不自動產生。</summary>
    public int Id { get; set; }

    /// <summary>傳統管道排隊等審核的時間（分鐘）。</summary>
    public double QueueWaitMinutesTraditional { get; set; } = DefaultQueueWaitMinutesTraditional;

    /// <summary>AI 管道排隊等審核的時間（分鐘）。</summary>
    public double QueueWaitMinutesAi { get; set; } = DefaultQueueWaitMinutesAi;

    /// <summary>傳統管道每案平均來回聯繫次數。</summary>
    public double ContactsPerCaseTraditional { get; set; } = DefaultContactsPerCaseTraditional;

    /// <summary>AI 管道每案平均來回聯繫次數。</summary>
    public double ContactsPerCaseAi { get; set; } = DefaultContactsPerCaseAi;

    /// <summary>每次聯繫花的客服分鐘數。</summary>
    public double MinutesPerContact { get; set; } = DefaultMinutesPerContact;

    /// <summary>客服每分鐘成本（新台幣）。</summary>
    public double StaffCostPerMinuteTwd { get; set; } = DefaultStaffCostPerMinuteTwd;

    /// <summary>每月案件量。</summary>
    public int MonthlyCaseVolume { get; set; } = DefaultMonthlyCaseVolume;

    public DateTime UpdatedAt { get; set; }

    /// <summary>最後修改者；預設值（尚未儲存過）為 null。刻意不設外鍵。</summary>
    public int? UpdatedByUserId { get; set; }

    public const int SingletonId = 1;

    public const double DefaultQueueWaitMinutesTraditional = 240;
    public const double DefaultQueueWaitMinutesAi = 30;
    public const double DefaultContactsPerCaseTraditional = 2.0;
    public const double DefaultContactsPerCaseAi = 0.5;
    public const double DefaultMinutesPerContact = 5;
    public const double DefaultStaffCostPerMinuteTwd = 8;
    public const int DefaultMonthlyCaseVolume = 1000;
}
