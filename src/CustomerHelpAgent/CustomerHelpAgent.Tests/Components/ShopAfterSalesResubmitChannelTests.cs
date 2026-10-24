using AntDesign;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Views.Shop;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>顧客的售後申請清單（/shop/after-sales）：補件的流程度量要記申請本身的管道。</summary>
public sealed class ShopAfterSalesResubmitChannelTests : ShopViewTestBase
{
    /// <summary>M6 修正：Agent 管道申請的「補件」原本被寫死記成傳統管道。</summary>
    [Fact]
    public async Task Resubmit_UsesRequestChannel()
    {
        var caseKey = Guid.NewGuid();
        await using (var db = Factory.CreateDbContext())
        {
            db.AfterSalesRequest.Add(new AfterSalesRequest
            {
                RequestNo = "AS202610230001",
                CaseKey = caseKey,
                CustomerUserId = CustomerId,
                SalesOrderItemId = White41ItemId,
                Type = AfterSalesType.QualityIssue,
                Status = AfterSalesStatus.NeedMoreInfo,
                Channel = AfterSalesChannel.Agent,
                Description = "開膠",
                StaffNote = "請補照片",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();
        }

        // AntDesign 的 Row 在渲染後會向 JS 取視窗尺寸；Loose 模式回傳 null 會讓它 NullReferenceException。
        JSInterop.Setup<AntDesign.JsInterop.Window>(_ => true).SetResult(new AntDesign.JsInterop.Window());

        var cut = Render<ContainerFragment>();
        cut.WaitForElement("button[data-action=resubmit]").Click();
        cut.WaitForElement(".shop-resubmit-modal textarea").Input("鞋底開膠，已補說明");
        cut.Find(".shop-resubmit-modal textarea").Blur();
        cut.Find(".shop-resubmit-modal .ant-btn-primary").Click();
        cut.WaitForElement(".ant-modal-confirm-btns .ant-btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            using var check = Factory.CreateDbContext();
            var metric = check.ProcessMetric.AsNoTracking().Single(x => x.Action == ProcessMetricActions.Resubmit);
            Assert.Equal(caseKey, metric.CaseKey);
            Assert.Equal(AfterSalesChannel.Agent, metric.Channel);
        });
    }

    /// <summary>AntContainer（確認窗的落點）＋申請清單。</summary>
    public sealed class ContainerFragment : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<AntContainer>(0);
            builder.CloseComponent();
            builder.OpenComponent<ShopAfterSalesListView>(1);
            builder.CloseComponent();
        }
    }
}
