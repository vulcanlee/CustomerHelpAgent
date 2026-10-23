using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 後台「Agent 執行紀錄」（/agent-runs）：唯讀列出每一輪 Agent 執行，點列開窗看與幕後面板相同的步驟時間軸。
/// 登入者沿用基底的顧客，依測試需要授予或不授予「Agent 執行紀錄」權限。
/// </summary>
public sealed class AgentRunsViewTests : ShopViewTestBase
{
    private readonly AgentTraceService traceService;

    public AgentRunsViewTests()
    {
        traceService = new AgentTraceService(Factory, TimeProvider.System, NullLogger<AgentTraceService>.Instance);
        Services.AddSingleton(traceService);
    }

    [Fact]
    public async Task AgentRuns_ListsRunsAndOpensSteps()
    {
        GrantPermission();
        var runId = await traceService.StartRunAsync(Guid.NewGuid(), CustomerId);
        await traceService.AppendStepAsync(runId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "顧客說：想換尺碼", null);
        await traceService.AppendStepAsync(runId, AgentStepKind.ToolResult, AgentDataSource.Database, "查到 2 筆訂單", """{"count":2}""");
        await traceService.EndRunAsync(runId, AgentStopReason.AwaitingConfirmation, 2, 1);

        var cut = Render<AgentRunsView>();

        var row = cut.WaitForElement("tr.agent-runs-row");
        Assert.Contains("顧客一", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("等待你確認申請內容", row.TextContent, StringComparison.Ordinal);

        row.Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".backstage-row").Count));
        Assert.Contains("顧客說：想換尺碼", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("查到 2 筆訂單", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void AgentRuns_WithoutPermission_ShowsDenied()
    {
        var cut = Render<AgentRunsView>();

        cut.WaitForAssertion(() => Assert.Contains(MagicObjectHelper.你沒有權限存取此頁面, cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("tr.agent-runs-row"));
    }

    private void GrantPermission()
    {
        using var context = Factory.CreateDbContext();
        var role = context.MyUser.Where(x => x.Id == CustomerId).Select(x => x.RoleViewId).Single()!.Value;
        var permission = new Permission { Key = MagicObjectHelper.角色_Agent執行紀錄, DisplayName = MagicObjectHelper.角色_Agent執行紀錄 };
        context.Permission.Add(permission);
        context.SaveChanges();
        context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = role, PermissionId = permission.Id });
        context.SaveChanges();
    }
}
