using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>審核窗的「AI 初審」面板：流程圖的節點與狀態色、AI 建議卡、程式降級警示，以及訂閱初審訊號即時更新。</summary>
public sealed class PreReviewPanelTests : PreReviewViewTestBase
{
    [Fact]
    public async Task Panel_ShowsNodeStatesAndRecommendation()
    {
        var requestId = await AddRequestAsync(Black40ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(
            requestId,
            PreReviewStatus.AwaitingDecision,
            new PreReviewSummary(PreReviewRecommendations.Approve, "建議核准換貨", ["簽收 3 天、未穿過"], ["確認目標尺碼庫存"]),
            (PreReviewNode.Intake, NodeStatus.Done, null),
            (PreReviewNode.PhotoReview, NodeStatus.Skipped, null),
            (PreReviewNode.RuleCheck, NodeStatus.Done, null),
            (PreReviewNode.HistoryReview, NodeStatus.Running, null));

        var cut = Render<PreReviewPanel>(p => p.Add(x => x.RequestId, requestId));

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll(".prereview-node").Count));
        Assert.Equal("Done", NodeStatusOf(cut, PreReviewNode.Intake));
        Assert.Equal("Skipped", NodeStatusOf(cut, PreReviewNode.PhotoReview));
        Assert.Equal("Running", NodeStatusOf(cut, PreReviewNode.HistoryReview));
        Assert.Equal("Waiting", NodeStatusOf(cut, PreReviewNode.StaffDecision));

        // 每格標示由誰執行：AI、程式或人；三路並行在同一個階段。
        Assert.Equal("AI", BadgeOf(cut, PreReviewNode.PhotoReview));
        Assert.Equal("程式", BadgeOf(cut, PreReviewNode.RuleCheck));
        Assert.Equal("AI", BadgeOf(cut, PreReviewNode.HistoryReview));
        Assert.Equal("AI", BadgeOf(cut, PreReviewNode.Summary));
        Assert.Equal("人", BadgeOf(cut, PreReviewNode.StaffDecision));
        Assert.Equal("程式", BadgeOf(cut, PreReviewNode.Execute));
        Assert.Single(cut.FindAll(".prereview-stage-fan"));
        Assert.Equal(3, cut.FindAll(".prereview-stage-fan .prereview-node").Count);

        var card = cut.Find(".prereview-card");
        Assert.Contains("建議核准", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("簽收 3 天、未穿過", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("確認目標尺碼庫存", card.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".prereview-warning"));
        Assert.Equal(3, cut.FindAll("details.prereview-finding").Count);
    }

    [Fact]
    public async Task Panel_Signal_RefreshesDiagram()
    {
        var requestId = await AddRequestAsync(Black40ItemId, AfterSalesChannel.Agent);
        var run = await SeedRunAsync(requestId, PreReviewStatus.Running, null, (PreReviewNode.HistoryReview, NodeStatus.Running, null));
        var signal = Services.GetRequiredService<PreReviewSignal>();

        var cut = Render<PreReviewPanel>(p => p.Add(x => x.RequestId, requestId));
        cut.WaitForAssertion(() => Assert.Equal("Running", NodeStatusOf(cut, PreReviewNode.HistoryReview)));
        Assert.Equal(1, signal.SubscriberCount);

        await Services.GetRequiredService<PreReviewRunService>().SetNodeAsync(run.Id, PreReviewNode.HistoryReview, NodeStatus.Done);
        signal.Publish(requestId);

        cut.WaitForAssertion(() => Assert.Equal("Done", NodeStatusOf(cut, PreReviewNode.HistoryReview)));

        // 關閉面板就取消訂閱。
        await DisposeComponentsAsync();
        Assert.Equal(0, signal.SubscriberCount);
    }

    [Fact]
    public async Task Panel_Warning_RenderedRed()
    {
        var requestId = await AddRequestAsync(Black40ItemId, AfterSalesChannel.Agent);
        const string warning = "AI 建議已被程式改為需人工判斷：規則核對不符資格";
        await SeedRunAsync(
            requestId,
            PreReviewStatus.AwaitingDecision,
            new PreReviewSummary(PreReviewRecommendations.Manual, "需人工判斷", ["簽收超過 7 天"], ["確認簽收日"], warning));

        var cut = Render<PreReviewPanel>(p => p.Add(x => x.RequestId, requestId));

        var warningElement = cut.WaitForElement(".prereview-warning");
        Assert.Equal(warning, warningElement.TextContent.Trim());
        Assert.Equal("alert", warningElement.GetAttribute("role"));
        Assert.Contains("需人工判斷", cut.Find(".prereview-card-head").TextContent, StringComparison.Ordinal);
    }

    /// <summary>送件掛勾失敗、還沒有任何一輪初審：仍可按「重新初審」補跑（申請仍待審時）。</summary>
    [Fact]
    public async Task Panel_NoRun_Retryable_ShowsRetryButton()
    {
        var requestId = await AddRequestAsync(Black40ItemId, AfterSalesChannel.Agent);
        // 按鈕在 RequirePermission（售後審核:edit）裡：以有「售後審核」頁面權限的客服身分渲染。
        Services.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = CustomerId, Account = "customer1", RoleList = [MagicObjectHelper.角色_售後審核] };

        var cut = Render<PreReviewPanel>(p => p.Add(x => x.RequestId, requestId).Add(x => x.Retryable, true));

        cut.WaitForAssertion(() => Assert.Contains("重新初審", cut.Find(".prereview-actions button").TextContent, StringComparison.Ordinal));
        Assert.Contains("AI 初審尚未開始", cut.Find(".prereview-empty").TextContent, StringComparison.Ordinal);

        // 申請已不是待審：不顯示。
        var notRetryable = Render<PreReviewPanel>(p => p.Add(x => x.RequestId, requestId).Add(x => x.Retryable, false));
        notRetryable.WaitForAssertion(() => Assert.NotNull(notRetryable.Find(".prereview-empty")));
        Assert.Empty(notRetryable.FindAll(".prereview-actions"));
    }

    private static string? NodeStatusOf(IRenderedComponent<PreReviewPanel> cut, PreReviewNode node)
        => cut.Find($".prereview-node[data-node='{node}']").GetAttribute("data-status");

    private static string BadgeOf(IRenderedComponent<PreReviewPanel> cut, PreReviewNode node)
        => cut.Find($".prereview-node[data-node='{node}'] .prereview-badge").TextContent.Trim();
}
