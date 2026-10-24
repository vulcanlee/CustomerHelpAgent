using System.Globalization;
using System.Security.Claims;
using AntDesign;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Components.Views.Admins;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 展示控制台（/demo-console，1.0.5 起）：管理員專屬；示範模式關閉時只顯示提示、全部停用。
/// 真服務（in-memory SQLite）：故障開關存檔、一鍵「準備此劇本」（重置 → 故障 → 模式 → 票券 → 導向切換頁）、設定金劇本，
/// 每個變更都以登入的管理員寫稽核（真的 <see cref="AuditLogService"/>）。
/// 確認窗靠同時渲染的 <see cref="AntContainer"/> 出現，測試按下確認鈕。
/// </summary>
public sealed class DemoConsoleViewTests : ShopViewTestBase
{
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "DemoConsoleTests-" + Guid.NewGuid().ToString("N"));
    private readonly DemoSwitchTicketService tickets = new(TimeProvider.System, NullLogger<DemoSwitchTicketService>.Instance);
    private readonly AgentRecordingService recordings;
    private DemoControlService demoControl;
    private int adminId;
    private string adminStamp = string.Empty;

    public DemoConsoleViewTests()
    {
        SeedAdminAndCustomer2();
        recordings = new AgentRecordingService(Factory, TimeProvider.System, NullLogger<AgentRecordingService>.Instance);
        demoControl = TestDemoControl.Create(Factory, enabled: true);

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var audit = new AuditLogService(Factory, NullLogger<AuditLogService>.Instance);

        Services.AddSingleton<IAuditLogService>(audit);
        Services.AddSingleton(tickets);
        Services.AddSingleton(recordings);
        Services.AddSingleton(new DemoScenarioRunService(Factory, TimeProvider.System, NullLogger<DemoScenarioRunService>.Instance));
        Services.AddSingleton(sp => demoControl);
        Services.AddSingleton(sp => new DemoResetService(
            Factory,
            sp.GetRequiredService<DemoControlService>(),
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = demoControl.IsEnabled, DemoUserPassword = "demo1234" }),
            TimeProvider.System,
            audit,
            sp.GetRequiredService<CurrentUserService>(),
            [],
            NullLogger<DemoResetService>.Instance));
        Services.AddSingleton(sp => new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = "key",
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            new RecordingTokenUsageRecorder(),
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            audit,
            sp.GetRequiredService<CurrentUserService>(),
            NullLoggerFactory.Instance));
        UseAdmin();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    [Fact]
    public void Console_NonAdmin_Denied()
    {
        // 基底的登入者是一般顧客。
        Services.AddSingleton<AuthenticationStateProvider>(new StaticAuthenticationStateProvider(Principal(CustomerId, CustomerStampFromDb())));

        var cut = RenderConsole();

        cut.WaitForAssertion(() => Assert.Contains(MagicObjectHelper.你沒有權限存取此頁面, cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".demo-console"));
    }

    [Fact]
    public void Console_DemoDisabled_ShowsNotice()
    {
        demoControl = TestDemoControl.Create(Factory, enabled: false);

        var cut = RenderConsole();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".demo-console-disabled")));
        Assert.Contains("示範模式未開啟", cut.Find(".demo-console-disabled").TextContent, StringComparison.Ordinal);
        var prepareButtons = cut.FindAll("button.demo-console-prepare");
        Assert.Equal(DemoScenarios.All.Count, prepareButtons.Count);
        Assert.All(prepareButtons, button => Assert.True(button.HasAttribute("disabled")));
        Assert.All(cut.FindAll(".demo-console-fault button.ant-switch"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.All(cut.FindAll("button.demo-console-identity"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.True(cut.Find("button.demo-console-reset").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Console_ToggleFault_PersistsAndShowsBadge()
    {
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".demo-console-fault")));
        Assert.Empty(cut.FindAll(".demo-console-fault-badge"));

        cut.Find($".demo-console-fault[data-fault='{DemoFault.OrderServiceTimeout}'] button.ant-switch").Click();

        cut.WaitForAssertion(() => Assert.Contains("訂單系統逾時", cut.Find(".demo-console-fault-badge").TextContent, StringComparison.Ordinal));
        Assert.True(demoControl.IsFaultActive(DemoFault.OrderServiceTimeout));

        // 存進資料庫：另一個實例載入後看得到。
        var reloaded = TestDemoControl.Create(Factory, enabled: true);
        await reloaded.LoadAsync();
        Assert.Equal([DemoFault.OrderServiceTimeout], reloaded.ActiveFaults);

        var audit = await LatestAuditAsync(AuditActions.Demo.FaultChanged);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Equal($"fault={DemoFault.OrderServiceTimeout}; state=on", audit.Detail);
    }

    [Fact]
    public async Task Console_PrepareScenario_ResetsSetsFaultsAndNavigatesToSwitch()
    {
        await demoControl.SetFaultAsync(DemoFault.PolicyConflict, true);
        var scenario = DemoScenarios.All.Single(x => x.Code == "F1");
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));

        cut.Find($".demo-console-scenario[data-code='{scenario.Code}'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "所有業務資料");

        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));

        // 重置：測試基底的訂單不見了，換成示範訂單。
        await using (var db = Factory.CreateDbContext())
        {
            var orderNos = await db.SalesOrder.Select(x => x.OrderNo).ToListAsync();
            Assert.Contains("SO-DEMO-0001", orderNos);
            Assert.DoesNotContain("SO-TEST-0001", orderNos);
        }

        // 故障：只剩這個劇本的故障；模式維持即時。
        Assert.Equal(scenario.Faults.ToHashSet(), demoControl.ActiveFaults.ToHashSet());
        Assert.Equal(DemoMode.Live, demoControl.Mode);

        // 票券：建立者是管理員、切換成劇本帳號、切換後到線上客服。
        Assert.True(tickets.TryRedeem(TicketOf(Navigation.Uri), out var ticket));
        Assert.Equal(new DemoSwitchTicket(adminId, DemoSwitchPurpose.SwitchIdentity, scenario.Account, "/shop/assistant"), ticket);

        var reset = await LatestAuditAsync(AuditActions.Demo.Reset);
        Assert.True(reset.Success);
        Assert.Equal(adminId, reset.ActorUserId);
        var faultAudits = await AuditsAsync(AuditActions.Demo.FaultChanged);
        Assert.Contains($"fault={DemoFault.PolicyConflict}; state=off", faultAudits);
        Assert.Contains($"fault={DemoFault.OrderServiceTimeout}; state=on", faultAudits);
    }

    /// <summary>M5：劇本 H 是多 Agent 模式：卡片標示模式，確認窗講明，切換身分後到 <c>/shop/assistant?mode=handoff</c>。</summary>
    [Fact]
    public async Task Console_PrepareScenarioH_StartsHandoffMode()
    {
        var scenario = DemoScenarios.All.Single(x => x.Code == "H");
        Assert.Equal(AgentMode.Handoff, scenario.Mode);
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));
        Assert.Contains("多 Agent", cut.Find(".demo-console-scenario[data-code='H'] .demo-console-scenario-mode").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".demo-console-scenario[data-code='A'] .demo-console-scenario-mode"));

        cut.Find(".demo-console-scenario[data-code='H'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "多 Agent 模式");

        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));
        Assert.True(tickets.TryRedeem(TicketOf(Navigation.Uri), out var ticket));
        Assert.Equal(new DemoSwitchTicket(adminId, DemoSwitchPurpose.SwitchIdentity, DemoSeedData.Customer1, "/shop/assistant?mode=handoff"), ticket);
        Assert.Equal(DemoMode.Live, demoControl.Mode);
    }

    /// <summary>M5：劇本 E（長期記憶）：customer1、單一模式；台詞跨三段對話，講解重點講清楚開新對話與到記憶頁刪除的時機。</summary>
    [Fact]
    public void ScenarioE_Defined()
    {
        var e = DemoScenarios.All.Single(x => x.Code == "E");
        Assert.Equal(DemoSeedData.Customer1, e.Account);
        Assert.Equal(AgentMode.Single, e.Mode);
        Assert.Empty(e.Faults);
        Assert.Equal(["我平常鞋子穿 41 碼，以後記得喔", "想換一雙鞋的尺寸", "想換一雙鞋的尺寸"], e.CustomerLines);
        var points = string.Join("\n", e.TalkingPoints);
        Assert.Contains("記憶確認卡", points, StringComparison.Ordinal);
        Assert.Contains("開新對話", points, StringComparison.Ordinal);
        Assert.Contains("我的 AI 記憶", points, StringComparison.Ordinal);
        Assert.Contains("41 碼", points, StringComparison.Ordinal);

        // H 是 B 的多 Agent 版本：同一個帳號、同樣的台詞；其餘劇本維持單一模式。代碼不重複。
        var b = DemoScenarios.All.Single(x => x.Code == "B");
        var h = DemoScenarios.All.Single(x => x.Code == "H");
        Assert.Equal(b.CustomerLines, h.CustomerLines);
        Assert.Equal(b.Account, h.Account);
        Assert.All(DemoScenarios.All.Where(x => x.Code is not ("H" or "A-M")), x => Assert.Equal(AgentMode.Single, x.Mode));
        Assert.Equal(DemoScenarios.All.Count, DemoScenarios.All.Select(x => x.Code).Distinct().Count());
    }

    /// <summary>M6：準備劇本會建立劇本執行並設為目前執行（在重置之後），之後的埋點蓋上這個編號。</summary>
    [Fact]
    public async Task PrepareScenario_CreatesRun_AndStampsMetrics()
    {
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));

        cut.Find(".demo-console-scenario[data-code='A'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "所有業務資料");
        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));

        await using var db = Factory.CreateDbContext();
        var run = await db.DemoScenarioRun.SingleAsync();
        Assert.Equal("A", run.ScenarioCode);
        Assert.Equal("換尺碼", run.Family);
        Assert.Equal(ScenarioFlow.SingleAgent, run.Flow);
        Assert.Equal(DemoSeedData.Customer1, run.Account);
        Assert.False(run.IsReplay);
        Assert.Equal(run.Id, demoControl.ActiveRunId);

        var metrics = new ProcessMetricService(Factory, demoControl, TimeProvider.System, NullLogger<ProcessMetricService>.Instance);
        await metrics.RecordAsync(Guid.NewGuid(), AfterSalesChannel.Agent, ProcessActor.Customer, ProcessMetricActions.Submit, CustomerId);
        await using var check = Factory.CreateDbContext();
        Assert.Equal(run.Id, (await check.ProcessMetric.OrderByDescending(x => x.Id).FirstAsync()).DemoRunId);
    }

    [Fact]
    public async Task PrepareTraditionalScenario_NavigatesToForm()
    {
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));

        cut.Find(".demo-console-scenario[data-code='A-T'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "售後申請表單");
        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));

        Assert.True(tickets.TryRedeem(TicketOf(Navigation.Uri), out var ticket));
        Assert.Equal(new DemoSwitchTicket(adminId, DemoSwitchPurpose.SwitchIdentity, DemoSeedData.Customer1, "/shop/after-sales/new"), ticket);
        await using var db = Factory.CreateDbContext();
        Assert.Equal(ScenarioFlow.Traditional, (await db.DemoScenarioRun.SingleAsync()).Flow);
    }

    /// <summary>
    /// 傳統表單劇本準備後直接打開售後申請表單：第一句台詞要從表單開始，不可叫展示者先繞去「我的訂單」
    /// （多出來的瀏覽步驟會讓傳統的步數與時間因展示者而異）。
    /// </summary>
    [Fact]
    public void TraditionalScenarios_FirstLineStartsOnForm()
    {
        var traditional = DemoScenarios.All.Where(x => x.Flow == ScenarioFlow.Traditional).ToList();

        Assert.Equal(["A-T", "B-T"], traditional.Select(x => x.Code));
        Assert.All(traditional, x =>
        {
            Assert.StartsWith("在售後申請表單", x.CustomerLines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("我的訂單", x.CustomerLines[0], StringComparison.Ordinal);
        });
        Assert.Equal(DemoConsoleView.ShopAfterSalesFormUrl, DemoConsoleView.CustomerUrlFor(traditional[0]));
    }

    /// <summary>傳統表單劇本不用 AI：確認窗不講「呼叫 AI 模型」，也不提醒 AI 尚未設定。</summary>
    [Fact]
    public void PrepareConfirmText_Traditional_HasNoAiWording()
    {
        var scenario = DemoScenarios.All.Single(x => x.Code == "B-T");

        var text = DemoConsoleView.BuildPrepareConfirmText(scenario, replayRequested: true, golden: null, aiError: "未設定");

        Assert.Contains("傳統表單流程，不使用 AI", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AI 模型", text, StringComparison.Ordinal);
        Assert.DoesNotContain("錄製", text.Split('\n').Single(x => x.StartsWith("3.", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayMode_RunMarkedIsReplay()
    {
        var golden = await CreateRecordingAsync("劇本 A 的金劇本");
        Assert.True(await recordings.SetGoldenAsync(golden, "A"));
        await demoControl.SetModeAsync(DemoMode.Replay, golden);

        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));
        cut.Find(".demo-console-scenario[data-code='A'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "劇本 A 的金劇本");
        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));

        await using var db = Factory.CreateDbContext();
        var run = await db.DemoScenarioRun.SingleAsync();
        Assert.True(run.IsReplay);
        Assert.Equal(run.Id, demoControl.ActiveRunId);
    }

    [Fact]
    public async Task Console_PrepareScenario_ReplayWithGolden_SetsReplayToGolden()
    {
        var other = await CreateRecordingAsync("另一段錄製");
        var golden = await CreateRecordingAsync("劇本 A 的金劇本");
        Assert.True(await recordings.SetGoldenAsync(golden, "A"));
        await demoControl.SetModeAsync(DemoMode.Replay, other);

        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));
        cut.Find(".demo-console-scenario[data-code='A'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "劇本 A 的金劇本");

        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));
        Assert.Equal(DemoMode.Replay, demoControl.Mode);
        Assert.Equal(golden, demoControl.ActiveRecordingId);
        var audit = await LatestAuditAsync(AuditActions.Demo.ModeChanged);
        Assert.Equal($"mode=Replay; recordingId={golden}", audit.Detail);
    }

    [Fact]
    public async Task Console_PrepareScenario_ReplayWithoutGolden_TellsPresenterAndStaysLive()
    {
        var other = await CreateRecordingAsync("沒有劇本的錄製");
        await demoControl.SetModeAsync(DemoMode.Replay, other);

        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.demo-console-prepare")));
        cut.Find(".demo-console-scenario[data-code='B'] button.demo-console-prepare").Click();
        ConfirmDestructive(cut, expectedText: "還沒有金劇本");

        cut.WaitForAssertion(() => Assert.Equal("/Auths/DemoSwitch", new Uri(Navigation.Uri).AbsolutePath), TimeSpan.FromSeconds(10));
        Assert.Equal(DemoMode.Live, demoControl.Mode);
        Assert.Null(demoControl.ActiveRecordingId);
        Assert.Equal("mode=Live", (await LatestAuditAsync(AuditActions.Demo.ModeChanged)).Detail);
    }

    [Fact]
    public async Task Console_SetGolden_MarksRecording()
    {
        var id = await CreateRecordingAsync("剛買的鞋尺碼不合適");
        var cut = RenderConsole();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr.demo-console-recording-row")));
        Assert.Empty(cut.Find("tr.demo-console-recording-row").QuerySelectorAll(".status-pill"));

        cut.FindAll("tr.demo-console-recording-row button").First(x => x.TextContent.Contains("設為金劇本", StringComparison.Ordinal)).Click();
        cut.WaitForElement(".demo-console-golden-picker [data-code='A'] button.demo-console-golden-choice").Click();

        cut.WaitForAssertion(() => Assert.Equal("金劇本", cut.Find("tr.demo-console-recording-row .status-pill").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".demo-console-golden-picker"));
        var golden = await recordings.GetGoldenAsync("A");
        Assert.NotNull(golden);
        Assert.Equal(id, golden!.Id);
        var audit = await LatestAuditAsync(AuditActions.Demo.RecordingGolden);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Equal(id.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        Assert.Equal($"recordingId={id}; scenario=A", audit.Detail);
    }

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    /// <summary>檢視與 AntContainer 一起渲染，確認窗才有地方出現。</summary>
    private IRenderedComponent<ContainerFragment> RenderConsole()
        => Render<ContainerFragment>();

    /// <summary>等紅色確認窗出現、檢查內文，再按確認。</summary>
    private static void ConfirmDestructive(IRenderedComponent<ContainerFragment> cut, string expectedText)
    {
        var ok = cut.WaitForElement(".ant-modal-confirm .ant-btn-dangerous");
        Assert.Contains(expectedText, cut.Find(".ant-modal-confirm").TextContent, StringComparison.Ordinal);
        ok.Click();
    }

    private static string TicketOf(string uri)
    {
        var query = new Uri(uri).Query;
        Assert.StartsWith("?ticket=", query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(query["?ticket=".Length..]);
    }

    private async Task<int> CreateRecordingAsync(string firstLine)
    {
        var key = Guid.NewGuid();
        await recordings.AppendTurnAsync(key, CustomerId, AgentRecordedTurnKind.Send, firstLine);
        await using var db = Factory.CreateDbContext();
        return await db.AgentRecording.Where(x => x.SessionKey == key).Select(x => x.Id).SingleAsync();
    }

    private async Task<AuditLog> LatestAuditAsync(string action)
    {
        await using var db = Factory.CreateDbContext();
        return await db.AuditLog.AsNoTracking().Where(x => x.Action == action).OrderByDescending(x => x.Id).FirstAsync();
    }

    private async Task<List<string?>> AuditsAsync(string action)
    {
        await using var db = Factory.CreateDbContext();
        return await db.AuditLog.AsNoTracking().Where(x => x.Action == action).OrderBy(x => x.Id).Select(x => x.Detail).ToListAsync();
    }

    private void SeedAdminAndCustomer2()
    {
        using var db = Factory.CreateDbContext();
        var role = db.MyUser.Where(x => x.Id == CustomerId).Select(x => x.RoleViewId).Single();
        var template = db.MyUser.AsNoTracking().Single(x => x.Id == CustomerId);
        var admin = new MyUser
        {
            Account = "presenter",
            Name = "展示者",
            Salt = template.Salt,
            Password = template.Password,
            Status = true,
            IsAdmin = true,
            RoleViewId = role,
        };
        var customer2 = new MyUser
        {
            Account = DemoSeedData.Customer2,
            Name = "顧客二",
            Salt = template.Salt,
            Password = template.Password,
            Status = true,
            RoleViewId = role,
        };
        db.MyUser.AddRange(admin, customer2);
        db.SaveChanges();
        adminId = admin.Id;
        adminStamp = admin.SecurityStamp;
    }

    private void UseAdmin()
        => Services.AddSingleton<AuthenticationStateProvider>(new StaticAuthenticationStateProvider(Principal(adminId, adminStamp)));

    private string CustomerStampFromDb()
    {
        using var db = Factory.CreateDbContext();
        return db.MyUser.AsNoTracking().Single(x => x.Id == CustomerId).SecurityStamp;
    }

    private static ClaimsPrincipal Principal(int userId, string stamp)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Sid, userId.ToString(CultureInfo.InvariantCulture)),
                new Claim(MagicObjectHelper.SecurityStampClaimType, stamp),
            ],
            "Test"));

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    /// <summary>AntContainer（確認窗的落點）＋展示控制台。</summary>
    public sealed class ContainerFragment : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<AntContainer>(0);
            builder.CloseComponent();
            builder.OpenComponent<DemoConsoleView>(1);
            builder.CloseComponent();
        }
    }
}
