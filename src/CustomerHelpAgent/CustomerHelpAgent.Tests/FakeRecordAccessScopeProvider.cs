using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用的存取範圍替身：直接回傳指定的「是否管理員 + 授權團隊」，
/// 不需要湊出 Cookie / JWT 兩套驗證情境。
/// </summary>
internal sealed class FakeRecordAccessScopeProvider(bool isAdmin, IReadOnlyList<string> teams)
    : IRecordAccessScopeProvider
{
    public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
}
