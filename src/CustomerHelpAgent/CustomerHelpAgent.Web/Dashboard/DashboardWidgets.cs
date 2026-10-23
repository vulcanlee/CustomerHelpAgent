using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Web.Components.Views.Dashboard;

namespace CustomerHelpAgent.Web.Dashboard;

/// <summary>
/// 首頁的一個小工具（0.9.106 起）。
/// </summary>
/// <param name="Id">唯一代號（也用在 HTML 的 <c>data-widget</c>）。</param>
/// <param name="Title">卡片標題。</param>
/// <param name="Order">排序，小的在前；同順序依 <paramref name="Id"/>。</param>
/// <param name="PermissionKey">需要的頁面權限鍵（例如 <c>MagicObjectHelper.角色_專案項目</c>）；null＝登入就看得到。</param>
/// <param name="AdminOnly">只有管理員看得到。</param>
/// <param name="ComponentType">小工具元件；只有看得到的才會被建立（沒權限的不建立、不查資料）。</param>
/// <param name="Icon">卡片標題前的 classic Material Icons 名稱（見速查表 §6.1）。</param>
public sealed record DashboardWidgetDescriptor(string Id, string Title, int Order, string? PermissionKey, bool AdminOnly, Type ComponentType, string Icon);

/// <summary>所有登記的小工具（singleton），依權限過濾與排序。</summary>
public sealed class DashboardWidgetCatalog
{
    public DashboardWidgetCatalog(IEnumerable<DashboardWidgetDescriptor> widgets)
    {
        All = widgets.OrderBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        var duplicate = All.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"首頁小工具的代號「{duplicate.Key}」登記了兩次。");
        }
    }

    public IReadOnlyList<DashboardWidgetDescriptor> All { get; }

    /// <summary>這個人看得到的小工具：管理員專屬的只給管理員；有權限鍵的要有該頁權限（管理員一律有）。</summary>
    public IReadOnlyList<DashboardWidgetDescriptor> VisibleTo(bool isAdmin, Func<string, bool> hasPagePermission)
        => All.Where(x => (!x.AdminOnly || isAdmin) && (x.PermissionKey is null || isAdmin || hasPagePermission(x.PermissionKey))).ToList();
}

public static class DashboardServiceCollectionExtensions
{
    /// <summary>登記一個首頁小工具。新增小工具＝寫一個元件＋在 <see cref="AddDashboard"/> 加一行。</summary>
    public static IServiceCollection AddDashboardWidget<TComponent>(
        this IServiceCollection services, string id, string title, int order, string icon, string? permissionKey = null, bool adminOnly = false)
        where TComponent : IComponent
        => services.AddSingleton(new DashboardWidgetDescriptor(id, title, order, permissionKey, adminOnly, typeof(TComponent), icon));

    /// <summary>首頁儀表板與內建的三個小工具。</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddDashboardWidget<MyNotificationsWidget>("notifications", "我的通知", 10, "notifications");
        services.AddDashboardWidget<MyAccountWidget>("account", "我的帳號", 20, "account_circle");
        services.AddDashboardWidget<ScheduledJobsWidget>("scheduled-jobs", "排程作業", 30, "schedule", adminOnly: true);
        services.AddSingleton<DashboardWidgetCatalog>();
        return services;
    }
}
