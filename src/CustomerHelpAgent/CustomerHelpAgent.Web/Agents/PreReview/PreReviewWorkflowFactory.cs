using Microsoft.Agents.AI.Workflows;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 組出初審 Workflow：
/// <c>Intake →</c> fan-out［照片初審、規則核對、歷史紀錄］→ fan-in barrier → 摘要 → 人工決策（RequestPort）→ 執行 → 通知；
/// 執行失敗另有條件邊回到人工決策。
/// ⚠️ Workflow 每次都重建（executor 欄位綁著這一輪），而且拓撲與 executor id 必須與存下 checkpoint 時完全相同，
/// 否則還原時拋「not compatible」。改 id、加減節點或邊，等於讓重啟前未完成的初審全部無法續跑。
/// </summary>
public sealed class PreReviewWorkflowFactory
{
    public const string IntakeId = "intake";
    public const string PhotoReviewId = "photo-review";
    public const string RuleCheckId = "rule-check";
    public const string HistoryReviewId = "history-review";
    public const string SummaryId = "summary";
    public const string DecisionPortId = "staff-decision";
    public const string ExecuteId = "execute";
    public const string NotifyId = "notify";

    private readonly PreReviewRunService runs;
    private readonly AfterSalesRequestService requests;
    private readonly AfterSalesPolicyService policies;
    private readonly ShopOrderService orders;
    private readonly AfterSalesDecisionService decisions;
    private readonly IPhotoReviewer photoReviewer;
    private readonly IHistoryReviewer historyReviewer;
    private readonly IPreReviewSummarizer summarizer;
    private readonly PreReviewSignal signal;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    public PreReviewWorkflowFactory(
        PreReviewRunService runs,
        AfterSalesRequestService requests,
        AfterSalesPolicyService policies,
        ShopOrderService orders,
        AfterSalesDecisionService decisions,
        IPhotoReviewer photoReviewer,
        IHistoryReviewer historyReviewer,
        IPreReviewSummarizer summarizer,
        PreReviewSignal signal,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        this.runs = runs;
        this.requests = requests;
        this.policies = policies;
        this.orders = orders;
        this.decisions = decisions;
        this.photoReviewer = photoReviewer;
        this.historyReviewer = historyReviewer;
        this.summarizer = summarizer;
        this.signal = signal;
        this.timeProvider = timeProvider;
        logger = loggerFactory.CreateLogger<PreReviewNodeContext>();
    }

    public Workflow Create(PreReviewRun run)
    {
        var nodes = new PreReviewNodeContext
        {
            RunId = run.Id,
            RequestId = run.AfterSalesRequestId,
            Runs = runs,
            Requests = requests,
            Policies = policies,
            Orders = orders,
            Decisions = decisions,
            PhotoReviewer = photoReviewer,
            HistoryReviewer = historyReviewer,
            Summarizer = summarizer,
            Signal = signal,
            Time = timeProvider,
            Logger = logger,
        };

        var intake = new IntakeExecutor(nodes);
        var photo = new PhotoReviewExecutor(nodes);
        var rule = new RuleCheckExecutor(nodes);
        var history = new HistoryReviewExecutor(nodes);
        var summary = new SummaryExecutor(nodes);
        var decision = RequestPort.Create<DecisionRequest, StaffDecision>(DecisionPortId);
        var execute = new ExecuteExecutor(nodes);
        var notify = new NotifyExecutor(nodes);

        return new WorkflowBuilder(intake)
            .AddFanOutEdge(intake, [photo, rule, history])
            .AddFanInBarrierEdge([photo, rule, history], summary)
            .AddEdge(summary, decision)
            .AddEdge(decision, execute)
            // 執行送出的兩種訊息各走各的邊：成功（ExecutionResult）→ 通知；失敗且申請仍待審（DecisionRequest）→ 回到人工決策。
            .AddEdge<ExecutionResult>(execute, notify, x => x is not null)
            .AddEdge<DecisionRequest>(execute, decision, x => x is not null)
            .WithOutputFrom(execute)
            .WithName("after-sales-pre-review")
            .Build();
    }
}
