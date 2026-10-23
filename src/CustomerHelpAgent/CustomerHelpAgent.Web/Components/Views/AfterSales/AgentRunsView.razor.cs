using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 後台「Agent 執行紀錄」（/agent-runs）：列出每一輪售後 Agent 執行（新到舊），點列開窗看與顧客幕後面板相同的步驟時間軸。
/// 唯讀：不編輯、不刪除。回放的那一輪（<see cref="AgentRunSummary.IsReplay"/>，1.0.5 起）在「模式」欄標「回放」。
/// </summary>
public partial class AgentRunsView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public AgentTraceService AgentTraceService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<AgentRunsView> Logger { get; set; } = default!;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    private List<AgentRunSummary> runs = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    private bool modalVisible;
    private AgentRunSummary? current;
    private IReadOnlyList<AgentTraceEvent> steps = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing agent runs view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Agent runs initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_Agent執行紀錄) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/agent-runs");
            Logger.LogWarning("Agent runs denied because current user has not this role permission.");
            return;
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var result = await AgentTraceService.GetRunsAsync(
            new DataRequest { CurrentPage = _pageIndex, PageSize = _pageSize, Take = _pageSize });
        runs = result.Result.ToList();
        _total = result.Count;
        Logger.LogDebug("Agent runs reloaded. Count={Count}", _total);
    }

    private async Task OnTableChange(QueryModel<AgentRunSummary> args)
    {
        _pageIndex = args.PageIndex;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await ReloadAsync();
        ViewNotification.Warning(NotificationService, "已更新最新資料");
    }

    private async Task OnRowClickAsync(RowData<AgentRunSummary> row)
    {
        try
        {
            var views = await AgentTraceService.GetStepsAsync(row.Data.Id);
            steps = views
                .Select(x => new AgentTraceEvent(x.Kind, x.Source, x.PlainText, x.RawJson, x.OccurredAt))
                .ToList();
            current = row.Data;
            modalVisible = true;
            Logger.LogInformation("Opened agent run modal. RunId={RunId}, StepCount={StepCount}", row.Data.Id, steps.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open agent run. RunId={RunId}", row.Data.Id);
            ViewNotification.UnexpectedError(NotificationService, "開啟執行紀錄時發生未預期的錯誤，請稍後再試。", ex);
        }
    }

    private void CloseModal()
    {
        modalVisible = false;
        current = null;
        steps = [];
    }

    private static string StopText(AgentStopReason? reason)
        => reason is { } value ? AgentStopLabels.Text(value) : "進行中或未正常結束";
}
