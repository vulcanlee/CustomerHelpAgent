namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 目前使用者對紀錄的存取範圍：是否管理員、以及授權團隊清單（0.9.105 起已含所有下屬部門）。
/// <see cref="Denied"/>（0.9.105 起）：已登入卻解析不到是誰 —— 什麼都看不到（以前退回「只看公開」，分類的反向規則下反而全部看得到）。
/// 判斷一律經 <c>RecordTeamScope</c>。
/// </summary>
public sealed record RecordAccessScope(bool IsAdmin, IReadOnlyList<string> Teams, bool Denied = false)
{
    public static RecordAccessScope None { get; } = new(false, [], Denied: true);
}

/// <summary>
/// 解析目前使用者的紀錄存取範圍。需同時支援 Blazor（CurrentUserService）
/// 與 Web API／檔案下載（HttpContext claims）兩種情境。
/// </summary>
public interface IRecordAccessScopeProvider
{
    Task<RecordAccessScope> GetAsync();
}
