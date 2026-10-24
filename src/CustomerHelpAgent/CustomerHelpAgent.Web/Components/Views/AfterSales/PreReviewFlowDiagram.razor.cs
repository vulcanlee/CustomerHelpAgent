using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>初審流程圖（唯讀）：依傳入的那一輪初審顯示各節點的執行者徽章與狀態色；沒有那一輪時全部是「等待中」。</summary>
public partial class PreReviewFlowDiagram
{
    /// <summary>每個陣列是一個階段；多於一個節點的階段是並行（fan-out）。</summary>
    private static readonly PreReviewNode[][] Stages =
    [
        [PreReviewNode.Intake],
        [PreReviewNode.PhotoReview, PreReviewNode.RuleCheck, PreReviewNode.HistoryReview],
        [PreReviewNode.Summary],
        [PreReviewNode.StaffDecision],
        [PreReviewNode.Execute],
        [PreReviewNode.Notify],
    ];

    [Parameter]
    public PreReviewRun? Run { get; set; }

    private NodeStatus StatusOf(PreReviewNode node)
        => Run?.Nodes.FirstOrDefault(x => x.Node == node)?.Status ?? NodeStatus.Waiting;

    private string? NoteOf(PreReviewNode node)
        => Run?.Nodes.FirstOrDefault(x => x.Node == node)?.Note;

    private static string ActorClass(PreReviewNode node) => PreReviewLabels.NodeActor(node) switch
    {
        PreReviewLabels.ActorAi => "ai",
        PreReviewLabels.ActorHuman => "human",
        _ => "program",
    };
}
