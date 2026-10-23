using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 後台售後審核（/after-sales-review）的傳統做法：審核窗預設只有申請內容，訂單要客服自己點開（並記埋點）；
/// 要求補件把狀態改成「待補件」並記埋點。登入者沿用基底的使用者，另外授予「售後審核」權限。
/// </summary>
public sealed class AfterSalesReviewViewTests : ShopViewTestBase
{
    private readonly int requestId;

    public AfterSalesReviewViewTests()
    {
        using (var context = Factory.CreateDbContext())
        {
            var role = context.MyUser.Where(x => x.Id == CustomerId).Select(x => x.RoleViewId).Single()!.Value;
            var permission = new Permission { Key = MagicObjectHelper.角色_售後審核, DisplayName = MagicObjectHelper.角色_售後審核 };
            context.Permission.Add(permission);
            context.SaveChanges();
            context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = role, PermissionId = permission.Id });
            context.SaveChanges();
        }

        // 必須在第一次向 Services 取服務之前註冊。
        Services.AddSingleton(new AfterSalesPolicyService(
            Factory, new RecordingAuditLogService(), new CurrentUserService(), TimeProvider.System, NullLogger<AfterSalesPolicyService>.Instance));

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
