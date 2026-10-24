using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Blazor 路徑的資料服務**必須注入 <see cref="IDbContextFactory{TContext}"/>**，
/// 不可直接注入 <see cref="BackendDBContext"/>。
///
/// 原因：在 Blazor Server，DI scope ＝ SignalR circuit，存活時間等同使用者整個連線
/// （數分鐘到數小時），不是一次 HTTP 請求。scoped 的 DbContext 會造成
/// 追蹤實體只增不減（記憶體成長、讀到過期資料），兩個元件事件重疊時還會拋
/// 「A second operation was started on this context」。
///
/// 0.4.36 之前正是如此，專案因而長出 <c>CleanTrackingHelper</c> 這條慣例
/// —— 全專案 47 處呼叫，每個新模組都得記得照抄，忘記就出錯。
/// 改用工廠後那條慣例已整條退場；這個測試防止它以任何形式回流。
/// </summary>
public sealed class DataAccessServiceLifetimeTests
{
    public static TheoryData<Type> DataAccessServices =>
    [
        typeof(CategoryService),
        typeof(AfterSalesPolicyService),
        typeof(ShopOrderService),
        typeof(AfterSalesDecisionService),
        typeof(AfterSalesRequestService),
        typeof(AgentTraceService),
        typeof(AgentSessionStore),
        typeof(CustomerMemoryService),
        typeof(AgentRecordingService),
        typeof(PreReviewRunService),
        typeof(WorkflowCheckpointService),
        typeof(ProcessMetricService),
        typeof(TeamService),
        typeof(RoleViewService),
        typeof(ProjectService),
        typeof(MyUserService),
        typeof(ExceptionLogService),
        typeof(TokenUsageLogService),
        typeof(AiCallLogService),
        typeof(AuditLogQueryService),
        // 稽核寫入端（0.9.131 起）：換頁稽核是射後不理，共用 scoped context 會和頁面初始化撞在一起。
        typeof(AuditLogService),
        typeof(ScheduledJobRunService),
        typeof(SoftDeletePurgeService),
        typeof(SystemParameterService),
        typeof(NotificationQueryService),
        typeof(AnnouncementService),
        typeof(PromptTemplateService),
    ];

    [Theory]
    [MemberData(nameof(DataAccessServices))]
    public void DataAccessService_ShouldInjectDbContextFactory_NotScopedContext(Type serviceType)
    {
        var parameters = serviceType.GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(
            parameters,
            p => p.ParameterType == typeof(BackendDBContext));

        Assert.Contains(
            parameters,
            p => p.ParameterType == typeof(IDbContextFactory<BackendDBContext>));
    }

    /// <summary>
    /// <c>CleanTrackingHelper</c> 已於 0.4.36 隨工廠遷移移除。
    /// 若它以任何形式回到 Business 組件，代表 scoped DbContext 的問題又出現了。
    /// </summary>
    [Fact]
    public void CleanTrackingHelper_ShouldNoLongerExist()
    {
        var businessAssembly = typeof(CategoryService).Assembly;

        Assert.DoesNotContain(
            businessAssembly.GetTypes(),
            t => t.Name.Contains("CleanTracking", StringComparison.Ordinal));
    }
}
