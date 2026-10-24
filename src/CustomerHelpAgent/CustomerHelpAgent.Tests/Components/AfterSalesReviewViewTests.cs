using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 後台售後審核（/after-sales-review）的傳統做法：審核窗預設只有申請內容，訂單要客服自己點開（並記埋點）；
/// 要求補件把狀態改成「待補件」並記埋點。登入者沿用基底的使用者，另外授予「售後審核」權限。
/// </summary>
public sealed class AfterSalesReviewViewTests : PreReviewViewTestBase
{
    private readonly int requestId;

    public AfterSalesReviewViewTests()
    {
        var service = Services.GetRequiredService<AfterSalesRequestService>();
        var submitted = service.SubmitAsync(CustomerId, new SubmitAfterSalesInput
        {
            CaseKey = Guid.NewGuid(),
            OrderItemId = Black40ItemId,
            Type = AfterSalesType.SizeExchange,
            TargetSize = "41",
            IsWorn = false,
            Description = "尺寸偏小",
        }, []).GetAwaiter().GetResult();
        requestId = submitted.RequestId!.Value;
    }

    [Fact]
    public void OpenRequest_ShouldShowOnlyTheApplication_UntilStaffOpensTheOrder()
    {
        var cut = Render<AfterSalesReviewView>();

        cut.WaitForElement("tr.after-sales-review-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("申請內容", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("簽收至今", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal([ProcessMetricActions.OpenRequest], StaffActions()));

        Button(cut, "查看訂單").Click();

        cut.WaitForAssertion(() => Assert.Contains("簽收至今", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("3 天", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal([ProcessMetricActions.OpenRequest, ProcessMetricActions.ViewOrder], StaffActions()));
    }

    [Fact]
    public void RequestMoreInfo_WithNote_ShouldMoveToNeedMoreInfo_AndRecordMetric()
    {
        var cut = Render<AfterSalesReviewView>();
        cut.WaitForElement("tr.after-sales-review-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("處理", cut.Markup, StringComparison.Ordinal));

        // AntDesign 的 TextArea 在 blur 時才回寫值（瀏覽器裡點按鈕會先 blur）。
        cut.Find(".after-sales-review-section textarea").Input("請補拍鞋底");
        cut.Find(".after-sales-review-section textarea").Blur();
        Button(cut, "要求補件").Click();

        cut.WaitForAssertion(() =>
        {
            using var context = Factory.CreateDbContext();
            var row = context.AfterSalesRequest.AsNoTracking().Single(x => x.Id == requestId);
            Assert.Equal(AfterSalesStatus.NeedMoreInfo, row.Status);
            Assert.Equal("請補拍鞋底", row.StaffNote);
        });
        cut.WaitForAssertion(() => Assert.Contains(ProcessMetricActions.RequestMoreInfo, StaffActions()));
    }

    [Fact]
    public void Panel_TraditionalChannel_NotRendered()
    {
        var cut = Render<AfterSalesReviewView>();

        cut.WaitForElement("tr.after-sales-review-row").Click();

        cut.WaitForAssertion(() => Assert.Contains("申請內容", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".prereview-panel"));
        Assert.Empty(cut.FindAll(".prereview-flow"));
        Assert.DoesNotContain("AI 初審尚未完成", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_AgentRow_ShowsPreReviewTag()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(
            agentRequestId,
            PreReviewStatus.AwaitingDecision,
            new PreReviewSummary(PreReviewRecommendations.Approve, "建議核准換貨", ["簽收 3 天"], ["確認庫存"]));

        var cut = Render<AfterSalesReviewView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.after-sales-review-row").Count));
        var rows = cut.FindAll("tr.after-sales-review-row");
        var agentRow = rows.Single(x => x.TextContent.Contains("AI 助理", StringComparison.Ordinal));
        var traditionalRow = rows.Single(x => x.TextContent.Contains("傳統表單", StringComparison.Ordinal));
        Assert.Contains("建議核准", agentRow.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("建議", traditionalRow.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("初審", traditionalRow.TextContent, StringComparison.Ordinal);
    }

    /// <summary>M5：「AI 多 Agent」管道與單一 Agent 一樣有初審標籤、AI 初審面板與「AI 助手」分頁。</summary>
    [Fact]
    public async Task Queue_HandoffRow_ShowsPreReviewTagPanelAndAssistantTab()
    {
        var handoffRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.AgentHandoff);
        await SeedRunAsync(
            handoffRequestId,
            PreReviewStatus.AwaitingDecision,
            new PreReviewSummary(PreReviewRecommendations.Approve, "建議核准換貨", ["簽收 3 天"], ["確認庫存"]));

        var cut = Render<AfterSalesReviewView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.after-sales-review-row").Count));
        var handoffRow = cut.FindAll("tr.after-sales-review-row").Single(x => x.TextContent.Contains("AI 多 Agent", StringComparison.Ordinal));
        Assert.Contains("建議核准", handoffRow.TextContent, StringComparison.Ordinal);

        handoffRow.Click();

        cut.WaitForElement(".prereview-flow");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".ant-tabs-tab"), x => x.TextContent.Contains("AI 助手", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Decide_WhileRunning_AsksConfirm()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        var run = await SeedRunAsync(agentRequestId, PreReviewStatus.Running);
        var container = Render<AntDesign.AntContainer>();
        var cut = Render<AfterSalesReviewView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.after-sales-review-row").Count));
        cut.FindAll("tr.after-sales-review-row").Single(x => x.TextContent.Contains("AI 助理", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains("處理", cut.Markup, StringComparison.Ordinal));
        // 決策按鈕旁先提示會略過 AI 建議。
        Assert.Contains("AI 初審尚未完成，直接決定會略過 AI 建議", cut.Find(".after-sales-review-hint").TextContent, StringComparison.Ordinal);

        Button(cut, "核准").Click();

        // 確認窗出現，按確認前申請還是「已提交」。
        container.WaitForAssertion(() => Assert.Contains("AI 初審尚未完成", container.Find(".ant-modal-confirm-title").TextContent, StringComparison.Ordinal));
        Assert.Contains("會略過 AI 建議", container.Find(".ant-modal-confirm-content").TextContent, StringComparison.Ordinal);
        Assert.Equal(AfterSalesStatus.Submitted, LoadRequest(agentRequestId).Status);

        container.Find(".ant-modal-confirm-btns .ant-btn-primary").Click();

        // 確認後決定生效，這一輪標為已由人工處理。
        cut.WaitForAssertion(() => Assert.Equal(AfterSalesStatus.Approved, LoadRequest(agentRequestId).Status));
        using var context = Factory.CreateDbContext();
        Assert.Equal(PreReviewStatus.Superseded, context.PreReviewRun.AsNoTracking().Single(x => x.Id == run.Id).Status);
    }

    [Fact]
    public void Approve_TraditionalChannel_GoesThroughCoordinator_AndShowsFulfilmentNo()
    {
        var container = Render<AntDesign.AntContainer>();
        var cut = Render<AfterSalesReviewView>();
        cut.WaitForElement("tr.after-sales-review-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("處理", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("AI 初審尚未完成", cut.Markup, StringComparison.Ordinal);

        Button(cut, "核准").Click();
        container.WaitForAssertion(() => Assert.Contains("確認核准", container.Find(".ant-modal-confirm-title").TextContent, StringComparison.Ordinal));
        container.Find(".ant-modal-confirm-btns .ant-btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Equal(AfterSalesStatus.Approved, LoadRequest(requestId).Status));
        using var context = Factory.CreateDbContext();
        var fulfilmentNo = context.AfterSalesFulfillment.AsNoTracking().Single(x => x.AfterSalesRequestId == requestId).ReferenceNo;
        container.WaitForAssertion(() => Assert.Contains($"處理單號 {fulfilmentNo}", container.Markup, StringComparison.Ordinal));
        Assert.Contains(ProcessMetricActions.Approve, StaffActions());
    }

    [Fact]
    public async Task Panel_FailedRun_ShowsRedFailureText_AndRetryCallsCoordinator()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(agentRequestId, PreReviewStatus.Failed);
        var cut = Render<AfterSalesReviewView>();
        OpenAgentRow(cut);

        Assert.Contains("AI 初審失敗", cut.WaitForElement(".prereview-warning").TextContent, StringComparison.Ordinal);

        Button(cut, "重新初審").Click();

        cut.WaitForAssertion(() =>
        {
            using var context = Factory.CreateDbContext();
            var runs = context.PreReviewRun.AsNoTracking().Where(x => x.AfterSalesRequestId == agentRequestId).OrderBy(x => x.Attempt).ToList();
            Assert.Equal(2, runs.Count);
            Assert.Equal(PreReviewStatus.Pending, runs[1].Status);
        });
    }

    [Fact]
    public async Task Panel_ExecutingRun_HidesRetry()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(agentRequestId, PreReviewStatus.Executing);
        var cut = Render<AfterSalesReviewView>();
        OpenAgentRow(cut);

        cut.WaitForElement(".prereview-flow");
        Assert.DoesNotContain(cut.FindAll("button"), x => x.TextContent.Trim() == "重新初審");
    }

    [Fact]
    public async Task AwaitingDecision_ShowsPreviousFailure_AndUsesNormalConfirm()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(
            agentRequestId,
            PreReviewStatus.AwaitingDecision,
            null,
            (PreReviewNode.StaffDecision, NodeStatus.Running, "目標規格已無庫存"));
        var container = Render<AntDesign.AntContainer>();
        var cut = Render<AfterSalesReviewView>();
        OpenAgentRow(cut);

        cut.WaitForAssertion(() => Assert.Contains("目標規格已無庫存", cut.Find(".after-sales-review-hint-error").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".after-sales-review-hint:not(.after-sales-review-hint-error)"));

        Button(cut, "核准").Click();

        container.WaitForAssertion(() => Assert.Equal("確認核准", container.Find(".ant-modal-confirm-title").TextContent.Trim()));
    }

    [Fact]
    public async Task Decide_FailedRun_NeedMoreInfo_SkipsConfirm()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(agentRequestId, PreReviewStatus.Failed);
        var cut = Render<AfterSalesReviewView>();
        OpenAgentRow(cut);
        cut.WaitForAssertion(() => Assert.Contains("處理", cut.Markup, StringComparison.Ordinal));

        cut.Find(".after-sales-review-section textarea").Input("請補拍鞋底");
        cut.Find(".after-sales-review-section textarea").Blur();
        Button(cut, "要求補件").Click();

        cut.WaitForAssertion(() => Assert.Equal(AfterSalesStatus.NeedMoreInfo, LoadRequest(agentRequestId).Status));
    }

    [Fact]
    public async Task Decide_RunningRun_NeedMoreInfoWithoutNote_RejectedBeforeConfirm()
    {
        var agentRequestId = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        await SeedRunAsync(agentRequestId, PreReviewStatus.Running);
        var container = Render<AntDesign.AntContainer>();
        var cut = Render<AfterSalesReviewView>();
        OpenAgentRow(cut);
        cut.WaitForAssertion(() => Assert.Contains("處理", cut.Markup, StringComparison.Ordinal));

        Button(cut, "要求補件").Click();

        container.WaitForAssertion(() => Assert.Contains(AfterSalesDecisionService.MoreInfoRequiredMessage, container.Markup, StringComparison.Ordinal));
        Assert.Empty(container.FindAll(".ant-modal-confirm"));
        Assert.Equal(AfterSalesStatus.Submitted, LoadRequest(agentRequestId).Status);
    }

    private void OpenAgentRow(IRenderedComponent<AfterSalesReviewView> cut)
    {
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.after-sales-review-row").Count));
        cut.FindAll("tr.after-sales-review-row").Single(x => x.TextContent.Contains("AI 助理", StringComparison.Ordinal)).Click();
    }

    private AfterSalesRequest LoadRequest(int id)
    {
        using var context = Factory.CreateDbContext();
        return context.AfterSalesRequest.AsNoTracking().Single(x => x.Id == id);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<AfterSalesReviewView> cut, string text)
        => cut.FindAll("button").First(x => x.TextContent.Trim() == text);

    private List<string> StaffActions()
    {
        using var context = Factory.CreateDbContext();
        return context.ProcessMetric.AsNoTracking()
            .Where(x => x.AfterSalesRequestId == requestId && x.Actor == ProcessActor.Staff)
            .OrderBy(x => x.Id)
            .Select(x => x.Action)
            .ToList();
    }
}
