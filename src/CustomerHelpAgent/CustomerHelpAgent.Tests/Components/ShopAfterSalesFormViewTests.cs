using Bunit;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Views.Shop;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 顧客售後申請表單（/shop/after-sales/new）：刻意保留傳統的多步驟，第三步的欄位依申請類型而變；
/// 表單不顯示規則條文與資格判定（由客服審核）。
/// </summary>
public sealed class ShopAfterSalesFormViewTests : ShopViewTestBase
{
    [Fact]
    public void Form_QualityIssueSelected_ShouldShowPhotoUpload()
    {
        var cut = Render<ShopAfterSalesFormView>(parameters => parameters.Add(p => p.OrderItemId, White41ItemId));

        cut.WaitForElement("[data-type=QualityIssue]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-field=photos] input[type=file]"));
            Assert.Empty(cut.FindAll("[data-field=target-size]"));
            Assert.Empty(cut.FindAll("[data-field=target-color]"));
            Assert.Empty(cut.FindAll("[data-field=is-worn]"));
        });
    }

    [Fact]
    public void Form_SizeExchangeSelected_ShouldShowTargetSizeOnly()
    {
        var cut = Render<ShopAfterSalesFormView>(parameters => parameters.Add(p => p.OrderItemId, Black40ItemId));

        cut.WaitForElement("[data-type=SizeExchange]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-field=target-size]"));
            Assert.Single(cut.FindAll("[data-field=is-worn]"));
            Assert.Empty(cut.FindAll("[data-field=target-color]"));
            Assert.Empty(cut.FindAll("[data-field=photos]"));
        });
    }

    [Fact]
    public void Form_ShouldOfferFiveTypesWithChineseLabels()
    {
        var cut = Render<ShopAfterSalesFormView>(parameters => parameters.Add(p => p.OrderItemId, Black40ItemId));

        cut.WaitForAssertion(() => Assert.Equal(
            ["換尺碼", "換顏色", "退貨退款", "品質問題（開膠等）", "物流催件"],
            cut.FindAll("[data-type]").Select(x => x.TextContent.Trim())));
    }

    [Fact]
    public void Form_WithoutOrderItemId_ShouldWaitForOrderItemBeforeAskingType()
    {
        var cut = Render<ShopAfterSalesFormView>();

        cut.WaitForAssertion(() => Assert.Contains("選擇訂單商品", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-type]"));
    }

    [Fact]
    public void Form_WithOrderItemIdNotInMyOrders_ShouldNotPreselect()
    {
        var cut = Render<ShopAfterSalesFormView>(parameters => parameters.Add(p => p.OrderItemId, 9999));

        cut.WaitForAssertion(() => Assert.Contains("選擇訂單商品", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-type]"));
    }

    [Fact]
    public void TargetOptions_ShouldExcludeTheCurrentVariant()
    {
        IReadOnlyList<(string Color, string Size)> variants =
            [("黑", "40"), ("黑", "41"), ("黑", "42"), ("白", "40"), ("白", "41")];

        Assert.Equal(["41", "42"], ShopAfterSalesFormView.TargetSizes(variants, "黑", "40"));
        Assert.Equal(["白"], ShopAfterSalesFormView.TargetColors(variants, "黑", "40"));
        Assert.Empty(ShopAfterSalesFormView.TargetColors(variants, "黑", "42"));
    }

    /// <summary>傳統做法：顧客自行判斷是否符合，表單不得查規則或做資格判定（那是客服審核的事）。</summary>
    [Fact]
    public void FormView_ShouldNotLookUpPolicyOrEligibility()
    {
        var source = File.ReadAllText(ShopViewSource("ShopAfterSalesFormView.razor"))
            + File.ReadAllText(ShopViewSource("ShopAfterSalesFormView.razor.cs"));

        Assert.DoesNotContain("AfterSalesPolicyService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AfterSalesEligibility", source, StringComparison.Ordinal);
    }

    internal static string ShopViewSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web", "Components", "Views", "Shop", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Components", "Views", "Shop", fileName);
            if (File.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 Components/Views/Shop/{fileName}。");
    }
}

/// <summary>顧客「我的申請」（/shop/after-sales）：中文狀態，只有「待補件」的申請可以補充資料。</summary>
public sealed class ShopAfterSalesListViewTests : ShopViewTestBase
{
    [Fact]
    public void List_ShouldShowLabelsAndStaffNote_AndOfferResubmitOnlyForNeedMoreInfo()
    {
        using (var context = Factory.CreateDbContext())
        {
            var now = DateTime.Now;
            context.AfterSalesRequest.AddRange(
                Request("AS202610230001", Black40ItemId, AfterSalesType.SizeExchange, AfterSalesStatus.Submitted, null, now.AddHours(-2)),
                Request("AS202610230002", White41ItemId, AfterSalesType.QualityIssue, AfterSalesStatus.NeedMoreInfo, "請補拍鞋底開膠處", now.AddHours(-1)));
            context.SaveChanges();
        }

        var cut = Render<ShopAfterSalesListView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".shop-request").Count));
        var markup = cut.Markup;
        Assert.Contains("AS202610230001", markup, StringComparison.Ordinal);
        Assert.Contains("已提交", markup, StringComparison.Ordinal);
        Assert.Contains("待補件", markup, StringComparison.Ordinal);
        Assert.Contains("換尺碼", markup, StringComparison.Ordinal);
        Assert.Contains("品質問題（開膠等）", markup, StringComparison.Ordinal);
        Assert.Contains("請補拍鞋底開膠處", markup, StringComparison.Ordinal);

        var resubmit = Assert.Single(cut.FindAll("[data-action=resubmit]"));
        Assert.Contains("補充資料", resubmit.TextContent, StringComparison.Ordinal);
        Assert.Equal("AS202610230002", resubmit.GetAttribute("data-request-no"));
    }

    [Fact]
    public void Labels_ShouldCoverEveryTypeAndStatus()
    {
        Assert.Equal(
            ["已提交", "待補件", "已核准", "已駁回"],
            Enum.GetValues<AfterSalesStatus>().Select(AfterSalesLabels.StatusText));
        Assert.All(Enum.GetValues<AfterSalesType>(), type => Assert.NotEqual(type.ToString(), AfterSalesLabels.TypeText(type)));
    }

    [Fact]
    public void List_WithoutRequests_ShouldSayEmpty()
    {
        var cut = Render<ShopAfterSalesListView>();

        cut.WaitForAssertion(() => Assert.Contains("目前沒有售後申請", cut.Markup, StringComparison.Ordinal));
    }

    private AfterSalesRequest Request(string no, int itemId, AfterSalesType type, AfterSalesStatus status, string? staffNote, DateTime createdAt) => new()
    {
        RequestNo = no,
        CaseKey = Guid.NewGuid(),
        CustomerUserId = CustomerId,
        SalesOrderItemId = itemId,
        Type = type,
        Status = status,
        Channel = AfterSalesChannel.Traditional,
        Description = "測試描述",
        StaffNote = staffNote,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
}
