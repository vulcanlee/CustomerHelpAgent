using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
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
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Auth;
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
    private const string ReplayFirstLine = "剛買的鞋尺碼不合適，想換大一碼";
    private const string ReplaySecondLine = "黑色那雙，沒穿過";

    private static readonly Regex RequestNoPattern = new(@"AS\d{12}");

    private readonly ScriptedAgentModel model = new();
    private readonly AgentPhotoInbox inbox = new();
    private readonly DemoSwitchTicketService tickets = new(TimeProvider.System, NullLogger<DemoSwitchTicketService>.Instance);
    private readonly BackendDBContext permissionContext;

    /// <summary>示範控制；預設關閉。回放／故障測試在第一次解析服務之前換成開啟的實例。</summary>
    private DemoControlService demo;
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
        demo = TestDemoControl.Off(Factory);

        Services.AddSingleton(inbox);
        Services.AddSingleton(_ => demo);
        Services.AddSingleton(tickets);
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
                new AfterSalesPolicyService(Factory, new RecordingAuditLogService(), currentUser, TestDemoControl.Off(Factory), time, NullLogger<AfterSalesPolicyService>.Instance),
                sp.GetRequiredService<AfterSalesRequestService>(),
                new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
                inbox,
                sp.GetRequiredService<DemoControlService>(),
                new AgentRecordingService(Factory, time, NullLogger<AgentRecordingService>.Instance),
                new ReplayCursorStore(),
                currentUser,
                new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()),
                time,
                NullLoggerFactory.Instance)
            {
                ReplayUpdateDelay = TimeSpan.Zero,
            };
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
    public void ConfirmCard_ConflictAfterCreate_ShowsCreated()
    {
        var cut = ReachConfirmation();
        // 申請寫入後、存回會話前，另一個分頁先存回了（版本前進）：這一輪沒有結果，但申請已建立。
        model.Fallback = _ =>
        {
            using var db = Factory.CreateDbContext();
            db.AgentSessionRecord.ExecuteUpdate(s => s.SetProperty(x => x.StateVersion, x => x.StateVersion + 1));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "已為您送出換尺碼申請。"));
        };

        cut.Find("[data-action=confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".agent-confirm-card");
            Assert.Matches(RequestNoPattern, card.TextContent);
            Assert.DoesNotContain("沒有建立申請", card.TextContent, StringComparison.Ordinal);
            Assert.NotNull(card.QuerySelector("a[href='/shop/after-sales']"));
            Assert.Contains(cut.FindAll(".chat-system"),
                x => x.TextContent.Contains("這段對話已在其他分頁更新，請重新整理頁面。", StringComparison.Ordinal));
            Assert.DoesNotContain(cut.FindAll(".chat-system"),
                x => x.TextContent.Contains("這次沒有完成送出", StringComparison.Ordinal));
        });
        Assert.Equal(1, RequestCount());
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

    [Fact]
    public void Assistant_Restore_ShowsSystemEntryAndPolicyCard()
    {
        model.Call("get_effective_policy", new() { ["orderItemId"] = White41ItemId, ["issueType"] = "quality_issue" });
        model.Call("request_photo_upload", new() { ["orderItemId"] = White41ItemId, ["reason"] = "請拍攝開膠處" });
        var first = Render<ShopAssistantView>();
        Send(first, "白色那雙開膠了");
        first.WaitForAssertion(() =>
        {
            Assert.Contains(first.FindAll(".chat-system"), x => x.TextContent.Contains("請在下方上傳照片", StringComparison.Ordinal));
            Assert.Single(first.FindAll(".agent-policy-card"));
        });

        // 重新整理頁面：程式說明仍是系統訊息（不是 Agent 泡泡）、規則卡與幕後面板都還原、上傳區重新出現。
        var restored = Render<ShopAssistantView>();

        restored.WaitForAssertion(() =>
        {
            Assert.Contains("白色那雙開膠了", restored.Find(".chat-bubble-user").TextContent, StringComparison.Ordinal);
            Assert.Contains(restored.FindAll(".chat-system"), x => x.TextContent.Contains("請在下方上傳照片", StringComparison.Ordinal));
            Assert.Empty(restored.FindAll(".chat-bubble-agent"));
            var policy = restored.Find(".agent-policy-card");
            Assert.Contains("品質問題", policy.TextContent, StringComparison.Ordinal);
            Assert.Single(restored.FindAll("[data-role=photo-upload]"));
            var panel = restored.Find("[data-role=backstage]");
            Assert.Contains(panel.QuerySelectorAll(".backstage-row"), x => x.GetAttribute("data-kind") == "ToolResult");
            Assert.Contains("等待你上傳照片", panel.QuerySelector(".backstage-stop")!.TextContent, StringComparison.Ordinal);
        });
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

    [Fact]
    public async Task Replay_ShowsBadgeAndPrefilledReadOnlyInput()
    {
        await RecordAndSwitchToReplayAsync();
        // 回放不需要金鑰：不可出現「AI 服務尚未設定」，也不可擋住送出。
        aiSettings.ApiKey = string.Empty;
        var callsBefore = model.CallCount;

        var cut = Render<ShopAssistantView>();

        cut.WaitForAssertion(() =>
        {
            Assert.Matches(@"^回放中（錄製於 \d{4}/\d{2}/\d{2} \d{2}:\d{2}）$", cut.Find("[data-role=replay-badge]").TextContent.Trim());
            var input = cut.Find("[data-role=composer] input");
            Assert.Equal(ReplayFirstLine, input.GetAttribute("value"));
            Assert.True(input.HasAttribute("readonly"));
            Assert.False(input.HasAttribute("disabled"));
            Assert.False(cut.Find("[data-action=send]").HasAttribute("disabled"));
        });
        Assert.DoesNotContain("AI 服務尚未設定", cut.Markup, StringComparison.Ordinal);

        cut.Find("[data-role=composer]").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("黑 40 還是白 41？", cut.Find(".chat-bubble-agent").TextContent, StringComparison.Ordinal);
            Assert.Equal(ReplaySecondLine, cut.Find("[data-role=composer] input").GetAttribute("value"));
        });
        // 回放不呼叫模型。
        Assert.Equal(callsBefore, model.CallCount);
    }

    [Fact]
    public async Task Replay_ConfirmStep_OnlyRecordedChoiceEnabled()
    {
        await RecordAndSwitchToReplayAsync();
        var cut = Render<ShopAssistantView>();
        SendPrefilled(cut, ReplayFirstLine);
        SendPrefilled(cut, ReplaySecondLine);

        // 錄製時按的是「取消」：只有取消可以按。
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-action=confirm]").HasAttribute("disabled"));
            Assert.False(cut.Find("[data-action=cancel]").HasAttribute("disabled"));
        });

        cut.Find("[data-action=cancel]").Click();

        // 錄製的動作播完：固定說明；不是展示者身分，沒有回控制台的連結。
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("已取消", cut.Find(".agent-confirm-card").TextContent, StringComparison.Ordinal);
            Assert.Contains("回放結束：錄製內容已播完。", cut.Find("[data-role=replay-end]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        });
        Assert.Empty(cut.FindAll("[data-action=back-to-console]"));
        Assert.Equal(0, RequestCount());
    }

    [Fact]
    public async Task Replay_Diverged_PresenterGetsLinkBackToConsole()
    {
        UsePresenterPrincipal();
        await RecordAndSwitchToReplayAsync();
        // 錄製後庫存改變：第二句的工具結果與錄製時不同，回放中止。
        using (var db = Factory.CreateDbContext())
        {
            db.ProductVariant.ExecuteUpdate(s => s.SetProperty(x => x.Stock, 0));
        }

        var cut = Render<ShopAssistantView>();
        SendPrefilled(cut, ReplayFirstLine);
        SendPrefilled(cut, ReplaySecondLine);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("回放中止：與錄製內容不同。", cut.Find("[data-role=replay-end]").TextContent, StringComparison.Ordinal);
            Assert.Contains(cut.FindAll(".chat-system"), x => x.TextContent.Contains("請到展示控制台重置示範資料", StringComparison.Ordinal));
            Assert.True(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        });

        // /demo-console 只限管理員：經切回展示者的票券導過去（切換頁成功後導到 /demo-console）。
        cut.Find("[data-action=back-to-console]").Click();
        var uri = new Uri(Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("/Auths/DemoSwitch", uri.AbsolutePath);
        var ticket = Uri.UnescapeDataString(uri.Query["?ticket=".Length..]);
        Assert.True(tickets.TryRedeem(ticket, out var redeemed));
        Assert.Equal(new DemoSwitchTicket(CustomerId, DemoSwitchPurpose.SwitchBack, null), redeemed);
    }

    [Fact]
    public async Task Replay_PhotoStep_ShowsHintAndNeedsRecordedPhotoCount()
    {
        await RecordAndSwitchToReplayAsync(async (runner, key) =>
        {
            model.Call("request_photo_upload", new() { ["orderItemId"] = White41ItemId, ["reason"] = "請拍攝開膠處" });
            await runner.SendAsync(key, "白色那雙開膠了", _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
            inbox.Add(key, "crack.jpg", [1, 2, 3]);
            model.Reply("收到照片了，會轉給客服核驗。");
            await runner.NotifyPhotosUploadedAsync(key, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
        });
        var cut = Render<ShopAssistantView>();
        SendPrefilled(cut, "白色那雙開膠了");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("請上傳任一張照片以繼續", cut.Find("[data-role=replay-photo-hint]").TextContent.Trim());
            Assert.True(cut.Find("[data-role=composer] input").HasAttribute("disabled"));
        });

        // 錄製時上傳 1 張：選了 2 張不能送出，移除 1 張才可以。
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "a.jpg"),
            InputFileContent.CreateFromBinary([4, 5, 6], "b.jpg"));
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-action=send-photos]").HasAttribute("disabled")));
        cut.Find("[data-role=photo-upload] .chat-photo button").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-action=send-photos]").HasAttribute("disabled")));

        cut.Find("[data-action=send-photos]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll(".chat-bubble-agent"), x => x.TextContent.Contains("收到照片了", StringComparison.Ordinal));
            Assert.Contains("回放結束：錄製內容已播完。", cut.Find("[data-role=replay-end]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Backstage_DefaultFromDemoControl()
    {
        demo = TestDemoControl.Create(Factory, enabled: true);
        await demo.SetBackstageDefaultAsync(false);

        // 本機沒有偏好：照控制台的預設（關）。
        var cut = Render<ShopAssistantView>();
        cut.WaitForElement("[data-action=toggle-backstage]");
        Assert.Empty(cut.FindAll("[data-role=backstage]"));
        Assert.Equal("false", cut.Find("[data-action=toggle-backstage]").GetAttribute("aria-checked"));

        // 本機偏好（開）優先。
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("1");
        var withPreference = Render<ShopAssistantView>();
        withPreference.WaitForElement("[data-role=backstage]");
        Assert.Equal("true", withPreference.Find("[data-action=toggle-backstage]").GetAttribute("aria-checked"));
    }

    [Fact]
    public void Backstage_WarningRenderedRed()
    {
        var now = DateTime.Now;
        var cut = Render<AgentBackstagePanel>(parameters => parameters.Add(x => x.Events,
        [
            new AgentTraceEvent(AgentStepKind.ModelReply, AgentDataSource.Conversation, "Agent 回覆：查不到訂單。", null, now),
            new AgentTraceEvent(AgentStepKind.Warning, AgentDataSource.Program, AgentStopLabels.UncertainConclusionWarning, null, now),
            new AgentTraceEvent(AgentStepKind.Stop, AgentDataSource.Program, AgentStopLabels.Text(AgentStopReason.Answered), null, now),
        ]));

        var rows = cut.FindAll(".backstage-row");
        var warning = rows.Single(x => x.GetAttribute("data-kind") == nameof(AgentStepKind.Warning));
        Assert.True(warning.ClassList.Contains("is-warning"));
        Assert.All(rows.Where(x => x != warning), x => Assert.False(x.ClassList.Contains("is-warning")));
        Assert.True(rows.Single(x => x.GetAttribute("data-kind") == nameof(AgentStepKind.Stop)).ClassList.Contains("is-stop"));

        // 紅色走 theme.css 的別名 token（--shop-step-warning → --app-error），樣式檔不寫色碼。
        var css = File.ReadAllText(Path.Combine(WebRoot(), "Components", "Views", "Shop", "Assistant", "AgentStepTimeline.razor.css"));
        Assert.Matches(@"\.backstage-row\.is-warning\s*\{[^}]*var\(--shop-step-warning\)", css);
        var theme = File.ReadAllText(Path.Combine(WebRoot(), "wwwroot", "theme.css"));
        Assert.Contains("--shop-step-warning: var(--app-error);", theme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backstage_ShowsActiveFaults()
    {
        demo = TestDemoControl.Create(Factory, enabled: true);
        await demo.SetFaultAsync(DemoFault.PolicyConflict, true);

        var cut = Render<ShopAssistantView>();

        cut.WaitForAssertion(() => Assert.Equal("故障模擬中：規則衝突", cut.Find("[data-role=backstage-faults]").TextContent.Trim()));

        // 控制台一改就更新（順序同控制台：訂單 → 物流 → 規則 → 權限）。
        await demo.SetFaultAsync(DemoFault.ShipmentAnomaly, true);
        cut.WaitForAssertion(() => Assert.Equal("故障模擬中：物流異常、規則衝突", cut.Find("[data-role=backstage-faults]").TextContent.Trim()));

        await demo.ClearFaultsAsync();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-role=backstage-faults]")));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            permissionContext.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 示範模式開啟、即時模式下以 Runner 錄一段（預設：問鞋 → 指定黑 40（出確認卡）→ 按「取消」）；
    /// 再開新對話並切到回放這段錄製。之後渲染的頁面接續的是新對話（游標從第一句開始）。
    /// </summary>
    private async Task RecordAndSwitchToReplayAsync(Func<AfterSalesAgentRunner, Guid, Task>? record = null)
    {
        demo = TestDemoControl.Create(Factory, enabled: true);
        Services.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = CustomerId, Account = "customer1" };
        var runner = Services.GetRequiredService<AfterSalesAgentRunner>();
        var key = await runner.StartOrResumeAsync();

        await (record ?? RecordSizeExchangeCanceledAsync)(runner, key);

        int recordingId;
        using (var db = Factory.CreateDbContext())
        {
            recordingId = db.AgentRecording.Where(x => x.SessionKey == key).Select(x => x.Id).Single();
        }

        await demo.SetModeAsync(DemoMode.Replay, recordingId);
        await runner.StartNewAsync();
    }

    private async Task RecordSizeExchangeCanceledAsync(AfterSalesAgentRunner runner, Guid key)
    {
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋：黑色 40 碼、白色 41 碼。請問是黑 40 還是白 41？有穿過嗎？");
        await runner.SendAsync(key, ReplayFirstLine, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

        model.Call("get_effective_policy", new() { ["orderItemId"] = Black40ItemId, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = Black40ItemId });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var second = await runner.SendAsync(key, ReplaySecondLine, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
        var card = Assert.IsType<ConfirmationCard>(second.Confirmation);

        model.Reply("好的，已為您取消。");
        await runner.ConfirmAsync(key, card.ConfirmationId, approve: false, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
    }

    /// <summary>回放中：輸入框已帶入錄製的句子（唯讀），直接按送出。</summary>
    private static void SendPrefilled(IRenderedComponent<ShopAssistantView> cut, string expected)
    {
        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("[data-role=composer] input");
            Assert.Equal(expected, input.GetAttribute("value"));
            Assert.False(input.HasAttribute("disabled"));
        });
        cut.Find("[data-role=composer]").Submit();
    }

    /// <summary>展示者切換成顧客一的 Cookie（帶 <see cref="DemoClaims.PresenterId"/>）；要在第一次解析服務之前呼叫。</summary>
    private void UsePresenterPrincipal()
    {
        using var context = Factory.CreateDbContext();
        var stamp = context.MyUser.AsNoTracking().Single(x => x.Id == CustomerId).SecurityStamp;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Sid, CustomerId.ToString(CultureInfo.InvariantCulture)),
                new Claim(MagicObjectHelper.SecurityStampClaimType, stamp),
                new Claim(DemoClaims.PresenterId, "4242"),
                new Claim(DemoClaims.PresenterStamp, "presenter-stamp"),
            ],
            "Test"));
        Services.AddSingleton<AuthenticationStateProvider>(new StaticAuthenticationStateProvider(principal));
    }

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Web")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web。"), "CustomerHelpAgent.Web");
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

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
