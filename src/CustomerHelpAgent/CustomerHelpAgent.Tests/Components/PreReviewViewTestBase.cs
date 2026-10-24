using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Agents.StaffAssistant;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 審核頁與 AI 初審面板的 bUnit 測試基底：在顧客商店基底上加授予「售後審核」權限，
/// 並註冊審核頁需要的初審服務與協調器（AI 節點用假的，這裡的測試不會真的跑 Workflow），
/// 以及「AI 助手」分頁的交接摘要與客服 AI 助手（模型換成腳本化的 <see cref="Model"/>）。
/// </summary>
public abstract class PreReviewViewTestBase : ShopViewTestBase
{
    protected PreReviewViewTestBase()
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
        var time = TimeProvider.System;
        // 錯誤通知帶「複製」按鈕，需要這兩個服務。
        Services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ToastCopyButtonTests.TestWebHostEnvironment());
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CustomerHelpAgent.Web.Configuration.SecuritySettings()));
        Services.AddSingleton(new AfterSalesPolicyService(
            Factory, new RecordingAuditLogService(), new CurrentUserService(), TestDemoControl.Off(Factory), time, NullLogger<AfterSalesPolicyService>.Instance));
        Services.AddSingleton(new PreReviewRunService(Factory, time, NullLogger<PreReviewRunService>.Instance));
        Services.AddSingleton(new PreReviewSignal(NullLogger<PreReviewSignal>.Instance));
        Services.AddSingleton(new PreReviewQueue(NullLogger<PreReviewQueue>.Instance));
        Services.AddSingleton(sp =>
        {
            var fakes = new PreReviewFakes();
            var runs = sp.GetRequiredService<PreReviewRunService>();
            var requests = sp.GetRequiredService<AfterSalesRequestService>();
            var decisions = sp.GetRequiredService<AfterSalesDecisionService>();
            var signal = sp.GetRequiredService<PreReviewSignal>();
            var factory = new PreReviewWorkflowFactory(
                runs,
                requests,
                sp.GetRequiredService<AfterSalesPolicyService>(),
                sp.GetRequiredService<ShopOrderService>(),
                decisions,
                new FakePhotoReviewer(fakes),
                new FakeHistoryReviewer(fakes),
                new FakeSummarizer(fakes),
                signal,
                time,
                NullLoggerFactory.Instance);
            var checkpoints = new WorkflowCheckpointService(Factory, time, NullLogger<WorkflowCheckpointService>.Instance);
            var runner = new PreReviewWorkflowRunner(
                runs,
                requests,
                checkpoints,
                factory,
                signal,
                NullLogger<PreReviewWorkflowRunner>.Instance);
            return new AfterSalesReviewCoordinator(
                runs,
                checkpoints,
                requests,
                decisions,
                runner,
                sp.GetRequiredService<PreReviewQueue>(),
                signal,
                sp.GetRequiredService<CurrentUserService>(),
                NullLogger<AfterSalesReviewCoordinator>.Instance);
        });

        // 客服 AI 助手（AI 助手分頁）：模型換成腳本化的 Model，示範模式預設即時（Demo 可切回放）。
        Services.AddSingleton(new AgentTraceService(Factory, time, NullLogger<AgentTraceService>.Instance));
        Services.AddSingleton(sp => new HandoverSummaryBuilder(sp.GetRequiredService<AgentTraceService>(), sp.GetRequiredService<AfterSalesRequestService>()));
        Services.AddSingleton(Demo);
        Services.AddSingleton<IPermissionChecker>(new PermissionChecker(Factory.CreateDbContext(), NullLogger<PermissionChecker>.Instance));
        Services.AddSingleton<IAiSystemPromptProvider>(new StubSystemPromptProvider());
        Services.AddSingleton<IOptionsMonitor<AfterSalesAgentSettings>>(new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()));
        Services.AddSingleton(sp => new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(AiSettings),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            Usage,
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            sp.GetRequiredService<CurrentUserService>(),
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (_, _) => ModelOverride ?? Model,
        });
        Services.AddSingleton(sp => new StaffAssistantRunner(
            sp.GetRequiredService<AgentChatClientFactory>(),
            sp.GetRequiredService<IAiSystemPromptProvider>(),
            sp.GetRequiredService<AfterSalesRequestService>(),
            sp.GetRequiredService<AfterSalesPolicyService>(),
            sp.GetRequiredService<ShopOrderService>(),
            sp.GetRequiredService<PreReviewRunService>(),
            sp.GetRequiredService<HandoverSummaryBuilder>(),
            sp.GetRequiredService<IPermissionChecker>(),
            sp.GetRequiredService<CurrentUserService>(),
            Demo,
            sp.GetRequiredService<IOptionsMonitor<AfterSalesAgentSettings>>(),
            time,
            NullLoggerFactory.Instance));
    }

    /// <summary>客服 AI 助手的模型（腳本化）。</summary>
    internal ScriptedAgentModel Model { get; } = new();

    /// <summary>有設定時改用這個模型（例如一直等到被取消的模型）。</summary>
    internal Microsoft.Extensions.AI.IChatClient? ModelOverride { get; set; }

    /// <summary>AI 設定；清空金鑰即「AI 未設定」。</summary>
    internal AiSettings AiSettings { get; } = new()
    {
        Provider = nameof(AiProvider.AzureOpenAI),
        Endpoint = "https://unit-test.openai.azure.com",
        ApiKey = "key",
        Model = "my-deployment",
    };

    internal RecordingTokenUsageRecorder Usage { get; } = new();

    /// <summary>示範模式開啟（預設即時）；切回放用 <see cref="DemoControlService.SetModeAsync"/>。</summary>
    protected DemoControlService Demo => demo ??= TestDemoControl.Create(Factory, enabled: true);

    private DemoControlService? demo;

    /// <summary>送出一筆換尺碼申請，並把管道改成 <paramref name="channel"/>（直接改資料列，不經 Agent）。</summary>
    protected async Task<int> AddRequestAsync(int orderItemId, AfterSalesChannel channel)
    {
        var submitted = await Services.GetRequiredService<AfterSalesRequestService>().SubmitAsync(CustomerId, new SubmitAfterSalesInput
        {
            CaseKey = Guid.NewGuid(),
            OrderItemId = orderItemId,
            Type = AfterSalesType.SizeExchange,
            TargetSize = "42",
            IsWorn = false,
            Description = "尺寸偏小",
        }, []);
        var id = submitted.RequestId!.Value;
        await using var context = Factory.CreateDbContext();
        await context.AfterSalesRequest.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Channel, channel));
        return id;
    }

    /// <summary>為申請建立一輪初審並設成指定狀態；<paramref name="summary"/> 有給就存成摘要結論（含建議）。</summary>
    protected async Task<PreReviewRun> SeedRunAsync(
        int requestId,
        PreReviewStatus status,
        PreReviewSummary? summary = null,
        params (PreReviewNode Node, NodeStatus Status, string? Note)[] nodes)
    {
        var runs = Services.GetRequiredService<PreReviewRunService>();
        var run = await runs.CreateAsync(requestId) ?? throw new InvalidOperationException("no request");
        foreach (var (node, nodeStatus, note) in nodes)
        {
            await runs.SetNodeAsync(run.Id, node, nodeStatus, note);
        }

        if (summary is not null)
        {
            var finding = new PreReviewFinding(
                PreReviewNode.Summary,
                FindingStatus.Ok,
                summary.Headline,
                JsonSerializer.Serialize(summary, PreReviewJsonContext.Default.PreReviewSummary));
            await runs.SaveFindingAsync(run.Id, PreReviewNode.Summary, JsonSerializer.Serialize(finding, PreReviewJsonContext.Default.PreReviewFinding));
            await runs.SaveSummaryAsync(run.Id, summary.Recommendation, summary.Warning is not null);
        }

        await runs.SetStatusAsync(run.Id, status);
        return run;
    }
}
