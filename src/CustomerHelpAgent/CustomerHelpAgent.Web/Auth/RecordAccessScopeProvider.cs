using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 解析目前使用者的紀錄存取範圍：
/// - Blazor 互動情境：使用已填入的 <see cref="CurrentUserService"/>。
/// - Web API／檔案下載（JWT/Cookie）情境：以 <see cref="RequestActorResolver"/> 取出使用者 Id（Cookie 在 Sid、JWT 在 NameIdentifier），載入使用者與其有效團隊。
/// - 未登入：「非管理員、無團隊」，只看得到公開紀錄。
/// - ⚠️ 已登入卻解析不到是誰（0.9.105 起）：<see cref="RecordAccessScope.None"/>，什麼都看不到。
///   以前退回「只看公開」—— JWT 身分的 Id 不在 Sid，整個 Web API 等於不過濾；分類的反向規則下更是全部看得到。
/// </summary>
public sealed class RecordAccessScopeProvider : IRecordAccessScopeProvider
{
    private readonly CurrentUserService currentUserService;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly BackendDBContext context;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly ILogger<RecordAccessScopeProvider> logger;

    public RecordAccessScopeProvider(
        CurrentUserService currentUserService,
        IHttpContextAccessor httpContextAccessor,
        BackendDBContext context,
        IEffectiveTeamResolver effectiveTeamResolver,
        ILogger<RecordAccessScopeProvider> logger)
    {
        this.currentUserService = currentUserService;
        this.httpContextAccessor = httpContextAccessor;
        this.context = context;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.logger = logger;
    }

    public async Task<RecordAccessScope> GetAsync()
    {
        var currentUser = currentUserService.CurrentUser;
        if (currentUser.IsAuthenticated)
        {
            return new RecordAccessScope(currentUser.IsAdmin, currentUser.TeamList ?? []);
        }

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            if (RequestActorResolver.Resolve(principal).UserId is { } id)
            {
                var user = await context.MyUser
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == id);

                if (user is not null)
                {
                    var teams = await effectiveTeamResolver.GetEffectiveTeamNamesAsync(id);
                    return new RecordAccessScope(user.IsAdmin, teams);
                }

                logger.LogWarning("Record access denied because the signed-in user was not found. UserId={UserId}", id);
                return RecordAccessScope.None;
            }

            logger.LogWarning("Record access denied because the signed-in principal carries no user id.");
            return RecordAccessScope.None;
        }

        return new RecordAccessScope(false, []);
    }
}
