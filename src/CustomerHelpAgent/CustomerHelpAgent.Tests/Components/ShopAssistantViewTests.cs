using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Components.Views.Shop.Assistant;
using CustomerHelpAgent.Web.Configuration;
// MAF 1.25 也有同名的 Microsoft.Agents.AI.AgentSessionStore。
using AgentSessionStore = CustomerHelpAgent.Business.Services.DataAccess.AgentSessionStore;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 線上客服頁（/shop/assistant）：真實的 <see cref="AfterSalesAgentRunner"/>＋腳本化模型（<see cref="ScriptedAgentModel"/>），
/// 驗證對話泡泡、規則卡、確認卡、照片上傳區、幕後面板與會話還原。種子沿用 <see cref="ShopViewTestBase"/>（黑 40 簽收 3 天、白 41），
/// 另補換尺碼與品質問題兩條規則。
/// </summary>
public sealed class ShopAssistantViewTests : ShopViewTestBase
{
    private static readonly Regex RequestNoPattern = new(@"AS\d{12}");

    private readonly ScriptedAgentModel model = new();
    private readonly AgentPhotoInbox inbox = new();
    private readonly BackendDBContext permissionContext;
    private readonly AiSettings aiSettings = new()
    {
        Provider = nameof(AiProvider.AzureOpenAI),
        Endpoint = "https://unit-test.openai.azure.com",
        ApiKey = "key",
        Model = "my-deployment",
    };

    public ShopAssistantViewTests()
    {
        SeedPolicies();
        permissionContext = Factory.CreateDbContext();

        Services.AddSingleton(inbox);
        Services.AddSingleton(sp => new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(aiSettings),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            new RecordingTokenUsageRecorder(),
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            sp.GetRequiredService<CurrentUserService>(),
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (_, _) => model,
        });
        Services.AddSingleton(sp => new AgentSessionStore(Factory, sp.GetRequiredService<TimeProvider>(), NullLogger<AgentSessionStore>.Instance));
        Services.AddSingleton(sp => new AgentTraceService(Factory, sp.GetRequiredService<TimeProvider>(), NullLogger<AgentTraceService>.Instance));
        Services.AddSingleton(sp =>
        {
            var currentUser = sp.GetRequiredService<CurrentUserService>();
            var time = sp.GetRequiredService<TimeProvider>();
            return new AfterSalesAgentRunner(
                sp.GetRequiredService<AgentChatClientFactory>(),
                new StubSystemPromptProvider(),
                sp.GetRequiredService<AgentSessionStore>(),
                sp.GetRequiredService<AgentTraceService>(),
                sp.GetRequiredService<ShopOrderService>(),
                new AfterSalesPolicyService(Factory, new RecordingAuditLogService(), currentUser, time, NullLogger<AfterSalesPolicyService>.Instance),
                sp.GetRequiredService<AfterSalesRequestService>(),
                new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
                inbox,
                currentUser,
                new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()),
                time,
                NullLoggerFactory.Instance);
        });
    }

    [Fact]
    public void Assistant_FlagshipA_ShowsQuestionThenConfirmCard()
    {
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋：黑色 40 碼、白色 41 碼。請問是黑 40 還是白 41？有穿過嗎？");
        var cut = Render<ShopAssistantView>();

        Send(cut, "剛買的鞋尺碼不合適，想換大一碼");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("黑 40 還是白 41？", cut.Find(".chat-bubble-agent").TextContent, StringComparison.Ordinal);
            Assert.Contains("剛買的鞋尺碼不合適", cut.Find(".chat-bubble-user").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll(".agent-confirm-card"));
        });

        model.Call("get_effective_policy", new() { ["orderItemId"] = Black40ItemId, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = Black40ItemId });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        Send(cut, "黑色那雙，沒穿過");

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".agent-confirm-card");
            Assert.Contains("黑色 / 40", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("黑色 / 41", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("換尺碼", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("第 1 版", card.TextContent, StringComparison.Ordinal);
            Assert.Single(cut.FindAll("[data-action=confirm]"));
            Assert.Single(cut.FindAll("[data-action=cancel]"));

            var policy = cut.Find(".agent-policy-card");
            Assert.Contains("簽收 7 天內", policy.TextContent, StringComparison.Ordinal);
            Assert.Contains("示範售後說明", policy.TextContent, StringComparison.Ordinal);
            Assert.Contains("簽收第 3 天", policy.TextContent, StringComparison.Ordinal);

            // 按下確認或取消之前，輸入框停用。
            Assert.True(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        });
        Assert.Equal(0, RequestCount());
        Assert.Equal(2, MetricCount(ProcessMetricActions.AgentSendMessage));
    }

    [Fact]
    public void ConfirmCard_Confirm_ShowsRequestNoAndLink()
    {
        var cut = ReachConfirmation();

        model.Reply("已為您送出換尺碼申請。");
        cut.Find("[data-action=confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".agent-confirm-card");
            Assert.Matches(RequestNoPattern, card.TextContent);
            var link = card.QuerySelector("a[href='/shop/after-sales']");
            Assert.NotNull(link);
            Assert.Contains("到我的申請查看", link!.TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-action=confirm]"));
            Assert.False(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        });
        Assert.Equal(1, RequestCount());
        Assert.Equal(1, MetricCount(ProcessMetricActions.AgentConfirm));
    }

    [Fact]
    public void ConfirmCard_Cancel_RecordsRejectAndCreatesNothing()
    {
        var cut = ReachConfirmation();

        model.Reply("好的，已為您取消。");
        cut.Find("[data-action=cancel]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("已取消", cut.Find(".agent-confirm-card").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-action=confirm]"));
        });
        Assert.Equal(0, RequestCount());
        Assert.Equal(1, MetricCount(ProcessMetricActions.AgentReject));
    }

    [Fact]
    public void ConfirmCard_ConfirmFailsBeforeTurn_ShowsNotCreatedWithExplanation()
    {
        var cut = ReachConfirmation();
        // 另一個分頁開了新對話：這段會話已結束，Runner 拒絕（拋 InvalidOperationException），這一輪沒有結果。
        using (var db = Factory.CreateDbContext())
        {
            db.AgentSessionRecord.ExecuteUpdate(s => s.SetProperty(x => x.IsClosed, true));
        }

        cut.Find("[data-action=confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("沒有建立申請，請看下方的說明。", cut.Find(".agent-confirm-card").TextContent, StringComparison.Ordinal);
            Assert.Contains(cut.FindAll(".chat-system"),
                x => x.TextContent.Contains("這次沒有完成送出，請稍後再試或到「我的申請」確認。", StringComparison.Ordinal));
        });
        Assert.Equal(0, RequestCount());
    }

    [Fact]
    public void Bubble_RendersModelMarkupAsSafeHtml()
    {
        model.Reply("**換貨說明**<script>alert('x')</script><img src=x onerror=alert(1)>");
        var cut = Render<ShopAssistantView>();

        Send(cut, "請問怎麼換貨");

        cut.WaitForAssertion(() =>
        {
            var bubble = cut.Find(".chat-bubble-agent");
            Assert.NotNull(bubble.QuerySelector("strong"));
            Assert.Empty(cut.FindAll("script"));
            Assert.Empty(cut.FindAll("img"));
            Assert.DoesNotContain("<script", cut.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Backstage_ShowsWithheldFieldsLine()
    {
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋，請問是哪一雙？");
        var cut = Render<ShopAssistantView>();

        Send(cut, "我想換鞋");

        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find("[data-role=backstage]");
            var withheld = panel.QuerySelectorAll(".backstage-row").Single(x => x.TextContent.Contains("未送出：", StringComparison.Ordinal));
            Assert.Contains("地址", withheld.TextContent, StringComparison.Ordinal);
            Assert.Contains("手機", withheld.TextContent, StringComparison.Ordinal);
            Assert.Contains("資料庫", withheld.QuerySelector(".backstage-source")!.TextContent, StringComparison.Ordinal);
            Assert.Contains("等待你回覆", panel.QuerySelector(".backstage-stop")!.TextContent, StringComparison.Ordinal);
        });

        // 「看原始資料」展開成純文字的 <pre>。
        var row = cut.FindAll("[data-role=backstage] .backstage-row").First(x => x.TextContent.Contains("未送出：", StringComparison.Ordinal));
        Assert.Null(row.QuerySelector("pre"));
        row.QuerySelector("[data-action=raw]")!.Click();
        cut.WaitForAssertion(() =>
        {
            var raw = cut.FindAll("[data-role=backstage] .backstage-row")
                .First(x => x.TextContent.Contains("未送出：", StringComparison.Ordinal))
                .QuerySelector("pre");
            Assert.NotNull(raw);
            Assert.Contains("\"status\"", raw!.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Backstage_Toggle_HidesPanel()
    {
        var cut = Render<ShopAssistantView>();
        cut.WaitForElement("[data-role=backstage]");

        cut.Find("[data-action=toggle-backstage]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-role=backstage]")));
        Assert.Equal("false", cut.Find("[data-action=toggle-backstage]").GetAttribute("aria-checked"));
        var saved = JSInterop.VerifyInvoke("localStorage.setItem");
        Assert.Equal("0", saved.Arguments[1]);

        cut.Find("[data-action=toggle-backstage]").Click();
        cut.WaitForElement("[data-role=backstage]");
    }

    [Fact]
    public void Backstage_SavedPreferenceOff_StartsHidden()
    {
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("0");

        var cut = Render<ShopAssistantView>();

        cut.WaitForElement("[data-action=toggle-backstage]");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-role=backstage]")));
    }

    [Fact]
    public void Photo_RequestShowsUploadArea()
    {
        model.Call("request_photo_upload", new() { ["orderItemId"] = White41ItemId, ["reason"] = "請拍攝開膠處" });
        var cut = Render<ShopAssistantView>();

        Send(cut, "白色那雙開膠了");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-role=photo-upload] input[type=file]")));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "crack.jpg"));
        cut.WaitForAssertion(() => Assert.Contains("crack.jpg", cut.Find("[data-role=photo-upload]").TextContent, StringComparison.Ordinal));

        model.Reply("收到照片了，會轉給客服核驗。");
        cut.Find("[data-action=send-photos]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-role=photo-upload]"));
            Assert.Contains(cut.FindAll(".chat-bubble-user"), x => x.TextContent.Contains("（已上傳 1 張照片）", StringComparison.Ordinal));
            Assert.Contains(cut.FindAll(".chat-bubble-agent"), x => x.TextContent.Contains("收到照片了", StringComparison.Ordinal));
        });
        Assert.Contains(model.Requests[^1], m => m.Role == ChatRole.User && m.Text == "（已上傳 1 張照片）");
        Assert.Equal(1, MetricCount(ProcessMetricActions.AgentUploadPhoto));
    }

    [Fact]
    public void Restore_ThenConfirm_CreatesExactlyOne()
    {
        var first = ReachConfirmation();

        // 重新整理頁面：新的元件實例，從持久化的會話還原對話與未決的確認卡。
        var restored = Render<ShopAssistantView>();
        restored.WaitForAssertion(() =>
        {
            Assert.Contains("黑色那雙 40 碼想換 41", restored.Find(".chat-bubble-user").TextContent, StringComparison.Ordinal);
            Assert.Single(restored.FindAll(".agent-confirm-card [data-action=confirm]"));
        });

        model.Reply("已為您送出換尺碼申請。");
        restored.Find("[data-action=confirm]").Click();
        restored.WaitForAssertion(() => Assert.Matches(RequestNoPattern, restored.Find(".agent-confirm-card").TextContent));

        // 舊分頁上同一張卡再按一次：不會重複建立。
        first.Find("[data-action=confirm]").Click();
        first.WaitForAssertion(() => Assert.Empty(first.FindAll("[data-action=confirm]")));

        Assert.Equal(1, RequestCount());
    }

    /// <summary>
    /// 送出鈕不可依「有沒有輸入」停用：預設按鈕停用時瀏覽器不會以 Enter 送出表單，
    /// 打完字立刻按 Enter（伺服器還沒重繪）會被吃掉（實機發現）。空白訊息由程式忽略。
    /// </summary>
    [Fact]
    public void Composer_EmptyDraft_SendStaysEnabledAndIgnoresBlank()
    {
        var cut = Render<ShopAssistantView>();

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-action=send]").HasAttribute("disabled")));
        cut.Find("[data-role=composer] input").Input("   ");
        cut.Find("[data-role=composer]").Submit();

        Assert.Equal(0, model.CallCount);
        Assert.Empty(cut.FindAll(".chat-bubble-user"));
    }

    [Fact]
    public void AiNotConfigured_ShowsNotice()
    {
        aiSettings.ApiKey = string.Empty;

        var cut = Render<ShopAssistantView>();

        cut.WaitForAssertion(() => Assert.Contains("AI 服務尚未設定，請洽管理員", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-action=send]").HasAttribute("disabled"));

        cut.Find("[data-role=composer]").Submit();

        Assert.Equal(0, model.CallCount);
        Assert.Equal(0, MetricCount(ProcessMetricActions.AgentSendMessage));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            permissionContext.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Send(IRenderedComponent<ShopAssistantView> cut, string text)
    {
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-role=composer] input").HasAttribute("disabled")));
        cut.Find("[data-role=composer] input").Input(text);
        cut.Find("[data-role=composer]").Submit();
    }

    /// <summary>單一回合直接走到確認卡：查規則 → 提出建立申請（需確認）。</summary>
    private IRenderedComponent<ShopAssistantView> ReachConfirmation()
    {
        model.Call("get_effective_policy", new() { ["orderItemId"] = Black40ItemId, ["issueType"] = "size_exchange" });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var cut = Render<ShopAssistantView>();

        Send(cut, "黑色那雙 40 碼想換 41，沒穿過");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-action=confirm]")));
        return cut;
    }

    private Dictionary<string, object?> SizeExchangeArgs() => new()
    {
        ["orderItemId"] = Black40ItemId,
        ["issueType"] = "size_exchange",
        ["targetSize"] = "41",
        ["isWorn"] = false,
        ["description"] = "尺寸偏小，想換大一碼",
    };

    private int RequestCount()
    {
        using var db = Factory.CreateDbContext();
        return db.AfterSalesRequest.Count();
    }

    private int MetricCount(string action)
    {
        using var db = Factory.CreateDbContext();
        return db.ProcessMetric.Count(x => x.Action == action && x.Channel == AfterSalesChannel.Agent);
    }

    private void SeedPolicies()
    {
        using var db = Factory.CreateDbContext();
        var effectiveFrom = DateTime.Now.AddDays(-90);
        db.AfterSalesPolicy.AddRange(
            new AfterSalesPolicy
            {
                Code = "SIZE-EXCHANGE",
                Type = AfterSalesType.SizeExchange,
                Category = ProductCategories.All,
                Title = "換尺碼",
                Content = "簽收 7 天內、商品未穿過，可申請換同款其他尺碼。",
                Source = "示範售後說明",
                Version = 1,
                EffectiveFrom = effectiveFrom,
                WindowDays = 7,
                RequiresUnworn = true,
                CreatedAt = effectiveFrom,
            },
            new AfterSalesPolicy
            {
                Code = "QUALITY-ISSUE",
                Type = AfterSalesType.QualityIssue,
                Category = ProductCategories.All,
                Title = "品質問題",
                Content = "開膠等品質問題，簽收 180 天內可申請，需先上傳照片。",
                Source = "示範售後說明",
                Version = 1,
                EffectiveFrom = effectiveFrom,
                WindowDays = 180,
                RequiresPhoto = true,
                CreatedAt = effectiveFrom,
            });
        db.SaveChanges();
    }
}
