using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>售後政策服務：以新增版本取代就地編輯，並提供「某時點生效規則」查詢。</summary>
public class AfterSalesPolicyService
{
    private const string StaleBaseVersionMessage = "規則已被其他人更新，請重新整理後再新增版本。";

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AfterSalesPolicyService> logger;

    public AfterSalesPolicyService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<AfterSalesPolicyService> logger)
    {
        this.contextFactory = contextFactory;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private static AfterSalesPolicyModel ToModel(AfterSalesPolicy x) => new()
    {
        Id = x.Id,
        Code = x.Code,
        Type = x.Type,
        Category = x.Category,
        Title = x.Title,
        Content = x.Content,
        Source = x.Source,
        Version = x.Version,
        EffectiveFrom = x.EffectiveFrom,
        EffectiveTo = x.EffectiveTo,
        WindowDays = x.WindowDays,
        RequiresUnworn = x.RequiresUnworn,
        RequiresPhoto = x.RequiresPhoto,
    };

    public async Task<PolicyLookupResult> GetEffectiveAsync(AfterSalesType type, string productCategory, DateTime at)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.AfterSalesPolicy
            .AsNoTracking()
            .Where(x => x.Type == type
                && (x.Category == productCategory || x.Category == ProductCategories.All)
                && x.EffectiveFrom <= at
                && (x.EffectiveTo == null || at < x.EffectiveTo))
            .OrderBy(x => x.Code).ThenBy(x => x.Version).ThenBy(x => x.Id)
            .ToListAsync();

        var policies = rows.Select(ToModel).ToList();
        var status = policies.Count switch
        {
            0 => PolicyLookupStatus.NoPolicy,
            1 => PolicyLookupStatus.Found,
            _ => PolicyLookupStatus.Conflict,
        };
        if (status == PolicyLookupStatus.Conflict)
        {
            logger.LogWarning("Multiple effective policies found. PolicyType={PolicyType}, Count={Count}", type, policies.Count);
        }

        return new PolicyLookupResult(status, policies);
    }

    public async Task<List<AfterSalesPolicyModel>> GetVersionsAsync(bool includeExpired)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = timeProvider.GetLocalNow().DateTime;
        IQueryable<AfterSalesPolicy> source = context.AfterSalesPolicy.AsNoTracking();
        if (!includeExpired)
        {
            source = source.Where(x => x.EffectiveTo == null || now < x.EffectiveTo);
        }

        var rows = await source.OrderBy(x => x.Code).ThenByDescending(x => x.Version).ThenBy(x => x.Id).ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    /// <summary>取得某一版規則（含已失效的版本；客服審核時要看申請當時套用的那一版）；不存在回 null。</summary>
    public async Task<AfterSalesPolicyModel?> GetByIdAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var row = await context.AfterSalesPolicy.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return row is null ? null : ToModel(row);
    }

    public async Task<VerifyRecordResult> AddVersionAsync(AfterSalesPolicyModel draft)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        try
        {
            var now = timeProvider.GetLocalNow().DateTime;
            var user = currentUserService.CurrentUser;
            int? userId = user.Id > 0 ? user.Id : null;

            await using var transaction = await context.Database.BeginTransactionAsync();
            var existing = await context.AfterSalesPolicy
                .Where(x => x.Code == draft.Code)
                .OrderBy(x => x.Version).ThenBy(x => x.Id)
                .ToListAsync();

            var currentVersions = existing.Where(x => x.EffectiveTo == null || x.EffectiveTo > now).ToList();

            // 基準版本 = 開窗預填來源的版本；與目前生效版本不同代表這段期間別人已新增過版本。null（全新代碼）不檢查。
            if (draft.BaseVersion is { } baseVersion
                && baseVersion != currentVersions.Select(x => (int?)x.Version).Max())
            {
                logger.LogInformation("Policy version rejected because the base version is stale. PolicyCode={PolicyCode}, BaseVersion={BaseVersion}", draft.Code, baseVersion);
                return VerifyRecordResultFactory.Build(false, StaleBaseVersionMessage);
            }

            foreach (var old in currentVersions)
            {
                old.EffectiveTo = now;
            }

            var nextVersion = existing.Count == 0 ? 1 : existing.Max(x => x.Version) + 1;
            var row = new AfterSalesPolicy
            {
                Code = draft.Code,
                Type = draft.Type,
                Category = draft.Category,
                Title = draft.Title,
                Content = draft.Content,
                Source = draft.Source,
                Version = nextVersion,
                EffectiveFrom = now,
                EffectiveTo = null,
                WindowDays = draft.WindowDays,
                RequiresUnworn = draft.RequiresUnworn,
                RequiresPhoto = draft.RequiresPhoto,
                CreatedAt = now,
                CreatedBy = userId,
            };
            context.AfterSalesPolicy.Add(row);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            logger.LogInformation("Policy version added. PolicyCode={PolicyCode}, Version={Version}", row.Code, row.Version);
            await auditLogService.WriteAsync(
                AuditActions.Shop.PolicyAddVersion,
                success: true,
                actorUserId: userId,
                actorAccount: userId is null ? null : user.Account,
                targetType: "AfterSalesPolicy",
                targetId: row.Id.ToString(),
                detail: $"code={row.Code}, version={row.Version}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add policy version. PolicyCode={PolicyCode}", draft.Code);
            return VerifyRecordResultFactory.Build(false, "新增售後規則版本失敗。", ex);
        }
    }
}
