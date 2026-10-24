using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>效益對照頁的假設值（M6）；用於「假設／推估」區塊，不是實測。</summary>
public sealed record DemoAssumptionView(
    double QueueWaitMinutesTraditional,
    double QueueWaitMinutesAi,
    double ContactsPerCaseTraditional,
    double ContactsPerCaseAi,
    double MinutesPerContact,
    double StaffCostPerMinuteTwd,
    int MonthlyCaseVolume,
    DateTime? UpdatedAt,
    int? UpdatedByUserId);

/// <summary>要儲存的假設值（不含更新時間與更新者）。</summary>
public sealed record DemoAssumptionInput(
    double QueueWaitMinutesTraditional,
    double QueueWaitMinutesAi,
    double ContactsPerCaseTraditional,
    double ContactsPerCaseAi,
    double MinutesPerContact,
    double StaffCostPerMinuteTwd,
    int MonthlyCaseVolume);

/// <summary>儲存結果；<see cref="Message"/> 是給畫面顯示的中文，<see cref="Assumption"/> 為儲存後（失敗時為目前）的值。</summary>
public sealed record DemoAssumptionSaveResult(bool Success, string Message, DemoAssumptionView Assumption);

/// <summary>
/// 假設值讀寫（M6，Scoped）：固定單列（<see cref="DemoAssumption.SingletonId"/>）；沒有資料時回傳預設值。
/// 儲存前驗證：分鐘 0～<see cref="MaxMinutes"/>、聯繫次數 0～<see cref="MaxContacts"/>、成本 ≥ 0、月案件量 1～<see cref="MaxMonthlyCaseVolume"/>。
/// 儲存成功寫稽核 <see cref="AuditActions.Demo.AssumptionChanged"/>；重置示範資料不會刪除這一列。
/// </summary>
public sealed class DemoAssumptionService
{
    public const double MaxMinutes = 10080;
    public const double MaxContacts = 50;
    public const int MinMonthlyCaseVolume = 1;
    public const int MaxMonthlyCaseVolume = 1_000_000;

    /// <summary>預設值（尚未儲存過時 <see cref="GetAsync"/> 回傳的同一組）；頁面讀取失敗時也用它當初始值。</summary>
    public static DemoAssumptionView DefaultView { get; } = ToView(new DemoAssumption(), null, null);

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<DemoAssumptionService> logger;

    public DemoAssumptionService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<DemoAssumptionService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>目前的假設值；沒有儲存過則回傳預設值（<see cref="DemoAssumptionView.UpdatedAt"/> 為 null）。</summary>
    public async Task<DemoAssumptionView> GetAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var row = await context.DemoAssumption.AsNoTracking().FirstOrDefaultAsync(x => x.Id == DemoAssumption.SingletonId);
        return row is null ? DefaultView : ToView(row, row.UpdatedAt, row.UpdatedByUserId);
    }

    public async Task<DemoAssumptionSaveResult> SaveAsync(DemoAssumptionInput input)
    {
        var error = Validate(input);
        if (error is not null)
        {
            logger.LogInformation("Demo assumption rejected by validation.");
            return new DemoAssumptionSaveResult(false, error, await GetAsync());
        }

        var user = currentUserService.CurrentUser;
        int? userId = user.Id > 0 ? user.Id : null;
        var before = await GetAsync();

        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var row = await context.DemoAssumption.FirstOrDefaultAsync(x => x.Id == DemoAssumption.SingletonId);
            if (row is null)
            {
                row = new DemoAssumption { Id = DemoAssumption.SingletonId };
                context.DemoAssumption.Add(row);
            }

            row.QueueWaitMinutesTraditional = input.QueueWaitMinutesTraditional;
            row.QueueWaitMinutesAi = input.QueueWaitMinutesAi;
            row.ContactsPerCaseTraditional = input.ContactsPerCaseTraditional;
            row.ContactsPerCaseAi = input.ContactsPerCaseAi;
            row.MinutesPerContact = input.MinutesPerContact;
            row.StaffCostPerMinuteTwd = input.StaffCostPerMinuteTwd;
            row.MonthlyCaseVolume = input.MonthlyCaseVolume;
            row.UpdatedAt = timeProvider.GetLocalNow().DateTime;
            row.UpdatedByUserId = userId;
            await context.SaveChangesAsync();
        }

        logger.LogInformation("Demo assumption saved.");
        await auditLogService.WriteAsync(
            AuditActions.Demo.AssumptionChanged,
            actorUserId: userId,
            actorAccount: user.Account,
            targetType: nameof(DemoAssumption),
            targetId: DemoAssumption.SingletonId.ToString(CultureInfo.InvariantCulture),
            detail: Diff(before, input));
        return new DemoAssumptionSaveResult(true, "假設值已儲存。", await GetAsync());
    }

    private static string? Validate(DemoAssumptionInput x)
    {
        if (!InRange(x.QueueWaitMinutesTraditional, 0, MaxMinutes)
            || !InRange(x.QueueWaitMinutesAi, 0, MaxMinutes)
            || !InRange(x.MinutesPerContact, 0, MaxMinutes))
        {
            return $"分鐘數必須介於 0 到 {MaxMinutes:0} 之間。";
        }

        if (!InRange(x.ContactsPerCaseTraditional, 0, MaxContacts) || !InRange(x.ContactsPerCaseAi, 0, MaxContacts))
        {
            return $"聯繫次數必須介於 0 到 {MaxContacts:0} 之間。";
        }

        if (!InRange(x.StaffCostPerMinuteTwd, 0, double.MaxValue))
        {
            return "客服每分鐘成本必須大於或等於 0。";
        }

        if (x.MonthlyCaseVolume < MinMonthlyCaseVolume || x.MonthlyCaseVolume > MaxMonthlyCaseVolume)
        {
            return $"每月案件量必須介於 {MinMonthlyCaseVolume} 到 {MaxMonthlyCaseVolume:N0} 之間。";
        }

        return null;
    }

    private static bool InRange(double value, double min, double max)
        => double.IsFinite(value) && value >= min && value <= max;

    /// <summary>只列出有變動的欄位：<c>Field=舊→新</c>。</summary>
    private static string Diff(DemoAssumptionView before, DemoAssumptionInput after)
    {
        var parts = new List<string>();
        void Add(string name, double oldValue, double newValue)
        {
            if (oldValue != newValue)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name}={oldValue}→{newValue}"));
            }
        }

        Add(nameof(after.QueueWaitMinutesTraditional), before.QueueWaitMinutesTraditional, after.QueueWaitMinutesTraditional);
        Add(nameof(after.QueueWaitMinutesAi), before.QueueWaitMinutesAi, after.QueueWaitMinutesAi);
        Add(nameof(after.ContactsPerCaseTraditional), before.ContactsPerCaseTraditional, after.ContactsPerCaseTraditional);
        Add(nameof(after.ContactsPerCaseAi), before.ContactsPerCaseAi, after.ContactsPerCaseAi);
        Add(nameof(after.MinutesPerContact), before.MinutesPerContact, after.MinutesPerContact);
        Add(nameof(after.StaffCostPerMinuteTwd), before.StaffCostPerMinuteTwd, after.StaffCostPerMinuteTwd);
        Add(nameof(after.MonthlyCaseVolume), before.MonthlyCaseVolume, after.MonthlyCaseVolume);
        return parts.Count == 0 ? "no change" : string.Join("; ", parts);
    }

    private static DemoAssumptionView ToView(DemoAssumption x, DateTime? updatedAt, int? updatedBy) => new(
        x.QueueWaitMinutesTraditional, x.QueueWaitMinutesAi, x.ContactsPerCaseTraditional, x.ContactsPerCaseAi,
        x.MinutesPerContact, x.StaffCostPerMinuteTwd, x.MonthlyCaseVolume, updatedAt, updatedBy);
}
