namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 解析使用者的有效團隊清單（用於列級權控）。
/// 指派的團隊 = 直接綁在使用者的 UserTeam ∪ 其角色的 DefaultTeamsJson（聯集、去重）；
/// 有效團隊（0.9.105 起）= 指派的團隊再加上它們所有的下屬部門（上層看得到下屬的資料）。
/// </summary>
public interface IEffectiveTeamResolver
{
    Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId);

    /// <summary>直接指派的團隊（不含展開的下屬部門），個人資料頁分開顯示用（0.9.105 起）。</summary>
    Task<IReadOnlyList<string>> GetAssignedTeamNamesAsync(int userId);

    /// <summary>
    /// 反向查詢：有效團隊包含 <paramref name="teamName"/> 的使用者 Id（0.9.100 起，發通知給整個團隊用）。
    /// 與 <see cref="GetEffectiveTeamNamesAsync"/> 同一個定義（測試以兩邊互相對照）：使用者 u 在結果裡 ⇔ 團隊名稱在 u 的有效團隊裡
    /// ⇔ u 被指派了這個團隊或它的任一個上層部門（0.9.105 起）。
    /// </summary>
    Task<IReadOnlyList<int>> GetUserIdsInTeamAsync(string teamName);
}
