using System.Text;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 已刪除資料清理（0.9.97 起）：專案、分類、團隊、使用者、角色被刪除超過 <see cref="SoftDeleteSettings.PurgeAfterDays"/> 天（預設 90）就永久刪除。
/// 0＝不自動永久刪除。<c>DeletedAt</c> 是本地時間，門檻用本地時鐘算。
///
/// 稽核：每種資料這次有刪到才寫一筆彙總（<c>Project/Category/Team/User/Role.AutoPurge</c>，筆數、天數、觸發方式與被刪的 <c>#Id 名稱</c>，上限 1000 字）。
/// ⚠️ 取消時先寫稽核再丟出取消：被永久刪除的業務資料不可以沒有任何紀錄。
/// 只有真正的錯誤才回報失敗；略過、使用中、受保護、附件檔刪不掉都只列在訊息裡（否則健康監控會一直紅燈）。
/// </summary>
public sealed class SoftDeletePurgeJob : IScheduledJob
{
    public const string JobName = "SoftDeletePurge";

    /// <summary>稽核 Detail 的上限（與執行紀錄訊息一致）。</summary>
    internal const int MaxDetailLength = 1000;

    private static readonly Dictionary<string, (string Action, string Label)> Types = new(StringComparer.Ordinal)
    {
        ["Project"] = (AuditActions.Project.AutoPurge, "專案"),
        ["Category"] = (AuditActions.Category.AutoPurge, "分類"),
        ["Team"] = (AuditActions.Team.AutoPurge, "團隊"),
        ["MyUser"] = (AuditActions.User.AutoPurge, "使用者"),
        ["RoleView"] = (AuditActions.Role.AutoPurge, "角色"),
    };

    private readonly SoftDeletePurgeService purgeService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<SoftDeleteSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SoftDeletePurgeJob> logger;

    public SoftDeletePurgeJob(
        SoftDeletePurgeService purgeService,
        IAuditLogService auditLogService,
        IOptionsMonitor<SoftDeleteSettings> options,
        TimeProvider timeProvider,
        ILogger<SoftDeletePurgeJob> logger)
    {
        this.purgeService = purgeService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.PurgeAfterDays;
        if (days <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動永久刪除。");
        }

        var cutoff = RetentionCutoffs.SoftDelete(days, timeProvider);
        var result = await purgeService.PurgeExpiredAsync(cutoff, cancellationToken);

        foreach (var type in result.Types.Where(x => x.Removed.Count > 0))
        {
            var (action, _) = Types[type.EntityType];
            await auditLogService.WriteAsync(
                action, success: true, targetType: type.EntityType, targetId: "*",
                detail: BuildDetail(type, days, context.Trigger));
        }

        logger.LogInformation(
            "Soft-deleted record purge completed. Rows={Rows}, RetentionDays={RetentionDays}, Cancelled={Cancelled}",
            result.Types.Sum(x => x.Removed.Count), days, result.Cancelled);

        // 稽核已寫入，才把取消交回執行器（記為「中斷」）。
        if (result.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var message = BuildMessage(result, days);
        return result.AnyFailed ? ScheduledJobResult.Failure(message) : ScheduledJobResult.Success(message);
    }

    /// <summary><c>rows=N; days=D; trigger=T; items=#1 名稱, #2 名稱 …(+K)</c>，不超過 <see cref="MaxDetailLength"/> 字。</summary>
    internal static string BuildDetail(SoftDeletePurgeTypeResult type, int days, string trigger)
    {
        var detail = new StringBuilder($"rows={type.Removed.Count}; days={days}; trigger={trigger}; items=");
        var listed = 0;
        foreach (var record in type.Removed)
        {
            var part = $"{(listed > 0 ? ", " : string.Empty)}#{record.Id} {record.Name}";
            var remaining = type.Removed.Count - listed - 1;
            var suffixReserve = remaining > 0 ? $" …(+{remaining})".Length : 0;
            if (detail.Length + part.Length + suffixReserve > MaxDetailLength)
            {
                break;
            }

            detail.Append(part);
            listed++;
        }

        if (listed < type.Removed.Count)
        {
            detail.Append($" …(+{type.Removed.Count - listed})");
        }

        return detail.Length <= MaxDetailLength ? detail.ToString() : detail.ToString(0, MaxDetailLength);
    }

    private static string BuildMessage(SoftDeletePurgeResult result, int days)
    {
        var removed = string.Join("、", result.Types.Select(x => $"{Types[x.EntityType].Label} {x.Removed.Count}"));
        var message = new StringBuilder($"永久刪除超過 {days} 天的已刪除資料：{removed}。");

        var skipped = result.Types.Sum(x => x.Skipped);
        var inUse = result.Types.Sum(x => x.InUse);
        var protectedCount = result.Types.Sum(x => x.Protected);
        var fileFailures = result.Types.Sum(x => x.FileDeleteFailures);
        if (skipped > 0)
        {
            message.Append($"略過 {skipped} 筆（期間被還原或變更）。");
        }

        if (inUse > 0)
        {
            message.Append($"{inUse} 個角色仍被使用者當作主要角色，留到下次。");
        }

        if (protectedCount > 0)
        {
            message.Append($"{protectedCount} 筆受保護（support 帳號或預設角色）不刪。");
        }

        if (fileFailures > 0)
        {
            message.Append($"{fileFailures} 個附件檔沒刪掉。");
        }

        var failed = result.Types.Where(x => x.Failed).Select(x => Types[x.EntityType].Label).ToList();
        if (failed.Count > 0)
        {
            message.Append($"清除失敗：{string.Join("、", failed)}，詳細內容請看「系統例外紀錄」。");
        }

        return message.ToString();
    }
}
