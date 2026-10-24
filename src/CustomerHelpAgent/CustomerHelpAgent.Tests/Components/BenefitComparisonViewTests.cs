using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Benefit;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 後台「效益對照」（/benefit-comparison，M6）：依情境家族並排傳統、單一 Agent、多 Agent 的實測指標（相對傳統的差異百分比），
/// 缺流程時提示要準備哪個劇本；假設值有「修改」權限才能改，改值立即重算月度推估（儲存前不寫資料庫）；
/// 明細列出每次執行，回放標「不計」。真服務（in-memory SQLite）；登入者沿用基底的顧客，依測試授予權限。
/// </summary>
public sealed class BenefitComparisonViewTests : ShopViewTestBase
{
    public BenefitComparisonViewTests()
    {
        var audit = new AuditLogService(Factory, NullLogger<AuditLogService>.Instance);
        Services.AddSingleton<IAuditLogService>(audit);
        // 錯誤通知的「複製」鈕要讀執行環境。
        Services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ToastCopyButtonTests.TestWebHostEnvironment { EnvironmentName = "Production" });
        Services.AddSingleton<ISystemIdentity>(new TestSystemIdentity());
        Services.AddSingleton(new BenefitMetricsService(Factory, NullLogger<BenefitMetricsService>.Instance));
        Services.AddSingleton(sp => new DemoAssumptionService(
            Factory, TimeProvider.System, audit, sp.GetRequiredService<CurrentUserService>(), NullLogger<DemoAssumptionService>.Instance));
    }

    [Fact]
    public void Page_RequiresPermission()
    {
        var denied = Render<BenefitComparisonView>();
        denied.WaitForAssertion(() => Assert.Contains(MagicObjectHelper.你沒有權限存取此頁面, denied.Markup, StringComparison.Ordinal));
        Assert.Empty(denied.FindAll(".benefit-comparison"));

        // 只有「檢視」動作鍵也進得來（角色矩陣只勾檢視時產生的就是這種鍵）。
        Grant(PermissionKey.For(MagicObjectHelper.角色_效益對照, PermissionActions.View));
        var allowed = Render<BenefitComparisonView>();
        allowed.WaitForElement(".benefit-comparison");
        Assert.DoesNotContain(MagicObjectHelper.你沒有權限存取此頁面, allowed.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cards_ShowDeltaPercent()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();

        var cut = Render<BenefitComparisonView>();

        var steps = cut.WaitForElement(".benefit-family[data-family='換尺碼'] .benefit-flow-card[data-flow='single'] .benefit-metric[data-metric='顧客操作步數']");
        Assert.Contains("3 → 1", steps.TextContent, StringComparison.Ordinal);
        var delta = steps.QuerySelector(".benefit-delta")!;
        Assert.Contains("−67%，改善", delta.TextContent, StringComparison.Ordinal);
        Assert.Contains("is-better", delta.ClassName, StringComparison.Ordinal);

        // 傳統是基準，不標差異；傳統不呼叫 AI，AI 成本無法算百分比。
        var traditional = cut.Find(".benefit-family[data-family='換尺碼'] .benefit-flow-card[data-flow='traditional']");
        Assert.Empty(traditional.QuerySelectorAll(".benefit-delta"));
        var cost = cut.Find(".benefit-family[data-family='換尺碼'] .benefit-flow-card[data-flow='single'] .benefit-metric[data-metric='AI 成本（每次）']");
        Assert.Contains("NT$0.50", cost.TextContent, StringComparison.Ordinal);
        Assert.Empty(cost.QuerySelectorAll(".benefit-delta"));

        // 長條圖：每個家族都有，三根依序代表三種流程，缺資料的那根寫「無資料」。
        var chart = cut.Find(".benefit-family[data-family='換尺碼'] .benefit-chart svg");
        Assert.Equal(2, chart.QuerySelectorAll("path.benefit-bar").Length);
        Assert.Contains("無資料", chart.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFlow_ShowsPrepareHint()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();

        var cut = Render<BenefitComparisonView>();

        var multi = cut.WaitForElement(".benefit-family[data-family='換尺碼'] .benefit-flow-card[data-flow='multi']");
        Assert.Contains("尚無資料（請在展示控制台準備劇本 A-M）", multi.TextContent, StringComparison.Ordinal);

        // 完全沒有執行的家族也照列，三欄都提示要準備的劇本。
        var twoInOne = cut.Find(".benefit-family[data-family='一句兩事']");
        Assert.Contains("準備劇本 B-T", twoInOne.TextContent, StringComparison.Ordinal);
        Assert.Contains("準備劇本 B）", twoInOne.TextContent, StringComparison.Ordinal);
        Assert.Contains("準備劇本 H", twoInOne.TextContent, StringComparison.Ordinal);
        // 沒有任何計入的執行時不畫一排全是「無資料」的空圖。
        Assert.Empty(twoInOne.QuerySelectorAll(".benefit-chart"));
        Assert.NotEmpty(cut.FindAll(".benefit-family[data-family='換尺碼'] .benefit-chart"));
    }

    [Fact]
    public void Assumptions_EditRequiresPermission()
    {
        // 只有檢視：看得到假設值（唯讀），沒有輸入框與儲存鈕。
        Grant(PermissionKey.For(MagicObjectHelper.角色_效益對照, PermissionActions.View));
        var readOnly = Render<BenefitComparisonView>();
        var panel = readOnly.WaitForElement(".benefit-assumptions");
        Assert.Contains("假設", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("1,000", panel.TextContent, StringComparison.Ordinal);
        Assert.Empty(readOnly.FindAll(".benefit-assumption-input"));
        Assert.Empty(readOnly.FindAll(".benefit-save-button"));

        // 加上「修改」：出現輸入框與儲存鈕。
        Grant(PermissionKey.For(MagicObjectHelper.角色_效益對照, PermissionActions.Edit));
        var editable = Render<BenefitComparisonView>();
        editable.WaitForElement(".benefit-assumptions");
        Assert.Equal(7, editable.FindAll(".benefit-assumption-input").Count);
        Assert.Single(editable.FindAll(".benefit-save-button"));
    }

    [Fact]
    public async Task Assumptions_Change_RecomputesEstimate()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();

        var cut = Render<BenefitComparisonView>();

        // 預設假設：傳統每案 10 + 2×5 = 20 分、AI 每案 2 + 0.5×5 = 4.5 分；每月 1,000 件、每分鐘 NT$8、AI 每案 NT$0.5。
        // 每月節省成本 = 15.5 × 1,000 × 8 − 0.5 × 1,000 = 123,500。
        var savedCost = cut.WaitForElement(".benefit-estimate-card[data-estimate='saved-cost']");
        Assert.Contains("NT$123,500", savedCost.TextContent, StringComparison.Ordinal);
        Assert.Contains("推估（依假設值）", cut.Find(".benefit-estimate").TextContent, StringComparison.Ordinal);

        // 每月案件量改成 2,000：立即重算（15.5 × 2,000 × 8 − 0.5 × 2,000 = 247,000），尚未儲存。
        cut.Find(".benefit-assumption-input[data-field='MonthlyCaseVolume'] input").Change("2000");

        cut.WaitForAssertion(() => Assert.Contains(
            "NT$247,000",
            cut.Find(".benefit-estimate-card[data-estimate='saved-cost']").TextContent,
            StringComparison.Ordinal));
        using var db = Factory.CreateDbContext();
        Assert.Empty(db.DemoAssumption);
    }

    [Fact]
    public async Task Detail_ReplayRunMarkedNotCounted()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();
        var replayRun = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, Today(12, 0), isReplay: true);
        var replayCase = Guid.NewGuid();
        await AddMetricsAsync(
            Customer(replayRun, replayCase, ProcessMetricActions.AgentSendMessage, Today(12, 0)),
            Customer(replayRun, replayCase, ProcessMetricActions.AgentRequestCreated, Today(12, 1)));

        var cut = Render<BenefitComparisonView>();

        // 回放不列入平均：單一 Agent 仍是 1 次有效、另註明排除 1 次回放。
        var single = cut.WaitForElement(".benefit-family[data-family='換尺碼'] .benefit-flow-card[data-flow='single']");
        Assert.Contains("有效 1 次", single.TextContent, StringComparison.Ordinal);
        Assert.Contains("回放 1", single.TextContent, StringComparison.Ordinal);

        Assert.Empty(cut.FindAll("tr.benefit-run-row"));
        cut.Find(".benefit-detail-toggle").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tr.benefit-run-row").Count));
        var rows = cut.FindAll("tr.benefit-run-row");
        var replayRow = Assert.Single(rows, x => x.TextContent.Contains($"#{replayRun}", StringComparison.Ordinal));
        Assert.Contains("回放（不計）", replayRow.TextContent, StringComparison.Ordinal);
        Assert.Contains("不計", replayRow.QuerySelector(".benefit-run-status")!.TextContent, StringComparison.Ordinal);
        Assert.All(rows.Where(x => x != replayRow), x => Assert.DoesNotContain("不計", x.TextContent, StringComparison.Ordinal));

        // 不顯示顧客個資：只有示範帳號代號。
        Assert.DoesNotContain("顧客一", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(DemoSeedData.Customer1, replayRow.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_Failure_ShowsErrorWithoutCrash()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        using (var db = Factory.CreateDbContext())
        {
            // 讀假設值一定失敗：頁面要顯示錯誤訊息，不可因為還沒有資料而丟例外。
            db.Database.ExecuteSqlRaw("DROP TABLE DemoAssumption;");
        }

        var cut = Render<BenefitComparisonView>();

        var error = cut.WaitForElement(".benefit-load-error");
        Assert.Contains("讀取效益對照時發生未預期的錯誤", error.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".benefit-comparison"));
    }

    [Fact]
    public async Task Others_ShowChannelAggregate()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        var caseKey = Guid.NewGuid();
        await AddMetricsAsync(
            new ProcessMetric { CaseKey = caseKey, Channel = AfterSalesChannel.Traditional, Actor = ProcessActor.Customer, Action = ProcessMetricActions.OpenForm, UserId = 1, OccurredAt = Today(9, 0) },
            new ProcessMetric { CaseKey = caseKey, Channel = AfterSalesChannel.Traditional, Actor = ProcessActor.Customer, Action = ProcessMetricActions.Submit, UserId = 1, OccurredAt = Today(9, 2) });

        var cut = Render<BenefitComparisonView>();

        var row = cut.WaitForElement(".benefit-others tr.benefit-other-row");
        Assert.Contains("傳統表單", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("NT$0.00", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("實測", cut.Find(".benefit-others").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_AsksConfirmation_ThenSaves()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        var cut = Render<ContainerFragment>();
        cut.WaitForElement(".benefit-assumptions");

        cut.Find(".benefit-assumption-input[data-field='MonthlyCaseVolume'] input").Change("2000");
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.benefit-save-button").HasAttribute("disabled")));
        cut.Find("button.benefit-save-button").Click();

        // 先跳確認窗；確認前不寫資料庫。
        var ok = cut.WaitForElement(".ant-modal-confirm .ant-btn-primary");
        Assert.Contains("儲存假設值", cut.Find(".ant-modal-confirm").TextContent, StringComparison.Ordinal);
        using (var before = Factory.CreateDbContext())
        {
            Assert.Empty(before.DemoAssumption);
        }

        ok.Click();

        cut.WaitForAssertion(() =>
        {
            using var db = Factory.CreateDbContext();
            Assert.Equal(2000, Assert.Single(db.DemoAssumption).MonthlyCaseVolume);
        });
    }

    [Fact]
    public void Save_PermissionRevoked_DoesNotSave()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        var cut = Render<ContainerFragment>();
        cut.WaitForElement(".benefit-assumptions");
        cut.Find(".benefit-assumption-input[data-field='MonthlyCaseVolume'] input").Change("2000");
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.benefit-save-button").HasAttribute("disabled")));
        var save = cut.Find("button.benefit-save-button");

        // 畫面還留著按鈕，但權限已經不在：送出時要在程式裡再擋一次，連確認窗都不出現。
        var user = Services.GetRequiredService<CurrentUserService>().CurrentUser;
        user.IsAdmin = false;
        user.RoleList.RemoveAll(x => x.StartsWith(MagicObjectHelper.角色_效益對照, StringComparison.Ordinal));
        save.Click();

        Assert.Empty(cut.FindAll(".ant-modal-confirm"));
        using var db = Factory.CreateDbContext();
        Assert.Empty(db.DemoAssumption);
    }

    [Fact]
    public async Task Save_InvalidValue_ShowsValidationMessage()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        var cut = Render<ContainerFragment>();
        cut.WaitForElement(".benefit-assumptions");

        // 輸入框有下限，這裡繞過它直接送負數（模擬竄改的用戶端），驗證訊息由服務決定。
        var staffCost = cut.FindComponents<AntDesign.InputNumber<double>>()[5];
        await cut.InvokeAsync(() => staffCost.Instance.ValueChanged.InvokeAsync(-1));
        cut.Find("button.benefit-save-button").Click();
        cut.WaitForElement(".ant-modal-confirm .ant-btn-primary").Click();

        var error = cut.WaitForElement(".benefit-assumption-error");
        Assert.Equal("客服每分鐘成本必須大於或等於 0。", error.TextContent.Trim());
        using var db = Factory.CreateDbContext();
        Assert.Empty(db.DemoAssumption);
    }

    [Fact]
    public async Task Export_Excel_DownloadsFileAndWritesAudit()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();
        var cut = Render<BenefitComparisonView>();
        cut.WaitForElement(".benefit-family");

        // 兩顆匯出鈕都在工具列上（有檢視權限就看得到）。
        var buttons = cut.FindAll("button.toolbar-icon-button");
        Assert.Contains(buttons, x => x.TextContent.Contains("匯出 PDF", StringComparison.Ordinal));
        buttons.Single(x => x.TextContent.Contains("匯出 Excel", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = Factory.CreateDbContext();
            var audit = Assert.Single(db.AuditLog.Where(x => x.Action == AuditActions.Demo.BenefitExport).ToList());
            Assert.StartsWith("format=xlsx; range=", audit.Detail, StringComparison.Ordinal);
        });
        var download = Assert.Single(JSInterop.Invocations, x => x.Identifier == "appFileDownload.downloadFromStream");
        Assert.Matches(@"^CustomerHelpAgent\.Web-benefit-\d{8}-\d{8}\.xlsx$", (string)download.Arguments[0]!);
    }

    [Fact]
    public async Task Export_ExcelInvalidOperation_ShowsGenericError()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();
        // Excel 路徑上的 InvalidOperationException 與中文字型無關：要給一般的未預期錯誤，不可誤報「缺少內建中文字型」。
        JSInterop.SetupVoid("appFileDownload.downloadFromStream", _ => true).SetException(new InvalidOperationException("download failed"));
        var container = Render<AntDesign.AntContainer>();
        var cut = Render<BenefitComparisonView>();
        cut.WaitForElement(".benefit-family");

        cut.FindAll("button.toolbar-icon-button").Single(x => x.TextContent.Contains("匯出 Excel", StringComparison.Ordinal)).Click();

        container.WaitForAssertion(() =>
        {
            var description = container.Find(".ant-notification-notice-description").TextContent;
            Assert.Contains("匯出時發生未預期的錯誤", description, StringComparison.Ordinal);
            Assert.DoesNotContain("中文字型", description, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Estimate_AiRunsWithoutModelCalls_ShowsGuidanceAndUnattributedWarning()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync(aiCallLogged: false);

        var cut = Render<BenefitComparisonView>();

        // AI 對話紀錄沒寫入：用量對不到案件，AI 的已完成執行沒有模型呼叫 → 不推估，並說明原因與處理方式。
        var insufficient = cut.WaitForElement(".benefit-estimate .benefit-insufficient");
        Assert.Contains("AI 已完成的執行找不到模型呼叫紀錄（AI 對話紀錄未啟用、寫入失敗或已過期），無法估算 AI 成本", insufficient.TextContent, StringComparison.Ordinal);
        Assert.Contains("AI 對話紀錄", insufficient.QuerySelector(".benefit-insufficient-calls-missing")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".benefit-estimate-card"));
        Assert.Contains(
            "有 1 次 AI 呼叫對應不到案件，AI 成本可能偏低",
            cut.Find(".benefit-estimate .benefit-unattributed").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estimate_UnattributedCalls_ShowsWarning()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();
        // 範圍內另有兩次對不到案件的呼叫（例如 AI 對話紀錄已過期）：推估照算，但提醒 AI 成本可能偏低。
        await AddCallAsync(Guid.NewGuid(), Today(13, 0), 0.2, withCallLog: false);
        await AddCallAsync(Guid.NewGuid(), Today(13, 1), 0.3, withCallLog: false);

        var cut = Render<BenefitComparisonView>();

        cut.WaitForElement(".benefit-estimate-card[data-estimate='saved-cost']");
        Assert.Contains(
            "有 2 次 AI 呼叫對應不到案件，AI 成本可能偏低",
            cut.Find(".benefit-estimate .benefit-unattributed").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estimate_AllCallsAttributed_NoUnattributedWarning()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();

        var cut = Render<BenefitComparisonView>();

        cut.WaitForElement(".benefit-estimate-card[data-estimate='saved-cost']");
        Assert.Empty(cut.FindAll(".benefit-unattributed"));
    }

    [Fact]
    public async Task Page_AndExport_ShareFamilyAndFlowStructure()
    {
        Grant(MagicObjectHelper.角色_效益對照);
        await SeedSizeExchangeAsync();
        var cut = Render<BenefitComparisonView>();
        cut.WaitForElement(".benefit-family");

        var today = DateTime.Today;
        var comparison = await Services.GetRequiredService<BenefitMetricsService>().GetComparisonAsync(today, today.AddDays(1));
        var tables = BenefitExport.BuildReport(new BenefitExportRequest(
                comparison, new DemoAssumptionInput(0, 0, 0, 0, 0, 0, 1), false, null, ScenarioFlow.SingleAgent, null, string.Empty, string.Empty, string.Empty, today))
            .Measured.Where(x => x.Header[0] == "指標").ToList();

        var pageFamilies = cut.FindAll(".benefit-family").ToList();
        Assert.Equal(pageFamilies.Select(x => x.GetAttribute("data-family")), tables.Select(x => x.Heading));
        for (var i = 0; i < tables.Count; i++)
        {
            var keys = pageFamilies[i].QuerySelectorAll(".benefit-flow-card").Select(x => x.GetAttribute("data-flow")!).ToList();
            var expected = tables[i].Header.Skip(1)
                .Select(label => BenefitMetricDisplay.FlowKey(Enum.GetValues<ScenarioFlow>().Single(f => BenefitMetricDisplay.FlowLabel(f) == label)))
                .ToList();
            Assert.Equal(expected, keys);
        }
    }

    /// <summary>
    /// 換尺碼：傳統（A-T，10:00）3 步（開啟表單不算）、客服開啟到核准 10 分、端到端 19 分（從 10:01 選商品算起，開啟表單不算起點）；
    /// 單一 Agent（A，11:00）1 步、客服 2 分、端到端 7 分、AI 成本 NT$0.5。多 Agent（A-M）沒有執行。
    /// <paramref name="aiCallLogged"/> 為 false 時 AI 呼叫沒有對話紀錄（對不到案件）。
    /// </summary>
    private async Task SeedSizeExchangeAsync(bool aiCallLogged = true)
    {
        var traditional = await AddRunAsync("A-T", "換尺碼", ScenarioFlow.Traditional, Today(10, 0));
        var traditionalCase = Guid.NewGuid();
        await AddMetricsAsync(
            Customer(traditional, traditionalCase, ProcessMetricActions.OpenForm, Today(10, 0), AfterSalesChannel.Traditional),
            Customer(traditional, traditionalCase, ProcessMetricActions.SelectOrderItem, Today(10, 1), AfterSalesChannel.Traditional),
            Customer(traditional, traditionalCase, ProcessMetricActions.SelectType, Today(10, 2), AfterSalesChannel.Traditional),
            Customer(traditional, traditionalCase, ProcessMetricActions.Submit, Today(10, 4), AfterSalesChannel.Traditional),
            Staff(traditional, traditionalCase, ProcessMetricActions.OpenRequest, Today(10, 10), 1, AfterSalesChannel.Traditional),
            Staff(traditional, traditionalCase, ProcessMetricActions.Approve, Today(10, 20), 1, AfterSalesChannel.Traditional));

        var single = await AddRunAsync("A", "換尺碼", ScenarioFlow.SingleAgent, Today(11, 0));
        var singleCase = Guid.NewGuid();
        await AddMetricsAsync(
            Customer(single, singleCase, ProcessMetricActions.AgentSendMessage, Today(11, 0)),
            Customer(single, singleCase, ProcessMetricActions.AgentRequestCreated, Today(11, 2)),
            Staff(single, singleCase, ProcessMetricActions.OpenRequest, Today(11, 5), 2),
            Staff(single, singleCase, ProcessMetricActions.Approve, Today(11, 7), 2));
        await AddCallAsync(singleCase, Today(11, 1), 0.5, aiCallLogged);
    }

    private static DateTime Today(int hour, int minute) => DateTime.Today.AddHours(hour).AddMinutes(minute);

    private async Task<int> AddRunAsync(string code, string family, ScenarioFlow flow, DateTime startedAt, bool isReplay = false)
    {
        await using var db = Factory.CreateDbContext();
        var run = new DemoScenarioRun
        {
            ScenarioCode = code,
            Family = family,
            Flow = flow,
            Account = DemoSeedData.Customer1,
            IsReplay = isReplay,
            StartedAt = startedAt,
        };
        db.DemoScenarioRun.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task AddMetricsAsync(params ProcessMetric[] metrics)
    {
        await using var db = Factory.CreateDbContext();
        db.ProcessMetric.AddRange(metrics);
        await db.SaveChangesAsync();
    }

    /// <summary>一次 AI 呼叫：用量一列，<paramref name="withCallLog"/> 時加一列對話紀錄（沒有就對不到案件，算「對應不到」）。</summary>
    private async Task AddCallAsync(Guid conversationId, DateTime at, double costTwd, bool withCallLog = true)
    {
        var callId = Guid.NewGuid();
        await using var db = Factory.CreateDbContext();
        db.TokenUsageLog.Add(new TokenUsageLog
        {
            OccurredAt = at,
            Operation = TokenUsageOperations.AfterSalesAgent,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "AzureOpenAI",
            Model = "gpt-test",
            InputCount = 100,
            OutputCount = 10,
            TotalCount = 110,
            Success = true,
            CostUsd = costTwd / 30,
            CostTwd = costTwd,
            CallId = callId,
        });
        if (withCallLog)
        {
            db.AiCallLog.Add(new AiCallLog
            {
                CallId = callId,
                OccurredAt = at,
                Operation = TokenUsageOperations.AfterSalesAgent,
                Provider = "AzureOpenAI",
                Model = "gpt-test",
                Success = true,
                ConversationId = conversationId,
            });
        }

        await db.SaveChangesAsync();
    }

    private static ProcessMetric Customer(int runId, Guid caseKey, string action, DateTime at, AfterSalesChannel channel = AfterSalesChannel.Agent)
        => new() { CaseKey = caseKey, Channel = channel, Actor = ProcessActor.Customer, Action = action, UserId = 1, OccurredAt = at, DemoRunId = runId };

    private static ProcessMetric Staff(int runId, Guid caseKey, string action, DateTime at, int requestId, AfterSalesChannel channel = AfterSalesChannel.Agent)
        => new() { CaseKey = caseKey, Channel = channel, Actor = ProcessActor.Staff, Action = action, UserId = 1, AfterSalesRequestId = requestId, OccurredAt = at, DemoRunId = runId };

    private sealed class TestSystemIdentity : ISystemIdentity
    {
        public string Name => "測試系統";
        public string Description => string.Empty;
        public string Version => "1.0.0 (2026/10/24)";
    }

    /// <summary>同時渲染 AntContainer，確認窗才會出現。</summary>
    public sealed class ContainerFragment : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<AntDesign.AntContainer>(0);
            builder.CloseComponent();
            builder.OpenComponent<BenefitComparisonView>(1);
            builder.CloseComponent();
        }
    }

    private void Grant(string key)
    {
        using var context = Factory.CreateDbContext();
        var role = context.MyUser.Where(x => x.Id == CustomerId).Select(x => x.RoleViewId).Single()!.Value;
        var permission = new Permission { Key = key, DisplayName = key };
        context.Permission.Add(permission);
        context.SaveChanges();
        context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = role, PermissionId = permission.Id });
        context.SaveChanges();
    }
}
