namespace CustomerHelpAgent.Share.Agents;

/// <summary>AI 初審（Workflow）一輪的狀態。</summary>
public enum PreReviewStatus
{
    Pending = 1,
    Running = 2,
    AwaitingDecision = 3,
    Executing = 4,
    Completed = 5,
    Failed = 6,
    Superseded = 7,
}

/// <summary>初審 Workflow 的節點。</summary>
public enum PreReviewNode
{
    Intake,
    PhotoReview,
    RuleCheck,
    HistoryReview,
    Summary,
    StaffDecision,
    Execute,
    Notify,
}

/// <summary>單一節點的執行狀態。</summary>
public enum NodeStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Skipped,
}
