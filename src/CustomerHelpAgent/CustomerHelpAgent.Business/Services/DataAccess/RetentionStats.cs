namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 「資料保留與清理」頁一列的資料量（0.9.133 起）。
/// <paramref name="Expired"/>：已超過保留期限、下次自動清理會刪掉的筆數；null 表示這類資料目前不自動清理（保留天數為 0）。
/// 各服務算 <paramref name="Expired"/> 時與自己的清理方法共用同一個條件，兩邊不會各說各話。
/// </summary>
public sealed record RetentionStats(int Total, DateTime? Oldest, int? Expired);
