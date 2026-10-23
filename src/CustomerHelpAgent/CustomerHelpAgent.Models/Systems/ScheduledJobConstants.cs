namespace CustomerHelpAgent.Models.Systems;

/// <summary>排程作業執行紀錄的觸發方式（存進 <c>JobRun.Trigger</c>，0.9.96 起）。</summary>
public static class JobRunTriggers
{
    /// <summary>到了排程時間。</summary>
    public const string Schedule = "Schedule";

    /// <summary>啟動時發現錯過了排程時段，補跑一次。</summary>
    public const string CatchUp = "CatchUp";

    /// <summary>管理員在「排程作業」頁按「立即執行」。</summary>
    public const string Manual = "Manual";
}

/// <summary>排程作業執行紀錄的狀態（存進 <c>JobRun.Status</c>，0.9.96 起）。</summary>
public static class JobRunStatuses
{
    public const string Running = "Running";

    public const string Succeeded = "Succeeded";

    public const string Failed = "Failed";

    /// <summary>執行到一半時網站關閉或行程結束。</summary>
    public const string Interrupted = "Interrupted";

    /// <summary>排程時間到了，但同一個作業的上一次執行還沒結束，這一次略過。</summary>
    public const string Skipped = "Skipped";
}
