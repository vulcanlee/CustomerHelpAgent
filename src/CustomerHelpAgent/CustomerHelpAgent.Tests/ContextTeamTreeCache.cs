using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用的部門樹（0.9.105 起）：每次都從指定的 DbContext 重新讀，不快取 —— 測試改了部門樹馬上就看得到。
/// 快取本身的行為（存活時間、失效）另由 <c>TeamTreeTests</c> 測真的 <see cref="TeamTreeCache"/>。
/// </summary>
internal sealed class ContextTeamTreeCache(BackendDBContext context) : ITeamTreeCache
{
    public int InvalidateCount { get; private set; }

    public async Task<TeamTree> GetAsync()
        => new(await context.Team.AsNoTracking().Select(x => new TeamNode(x.Id, x.Name, x.ParentId)).ToListAsync());

    public void Invalidate() => InvalidateCount++;
}
