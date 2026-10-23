using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 稽核紀錄的查詢條件。
///
/// 刻意不重用共用的 <see cref="DataRequest"/>：那支是 CRUD 清單頁共用的，
/// 加入時間範圍／動作／成敗只會讓每個頁面都背著用不到的欄位。
/// 作法比照 <see cref="ExceptionLogQuery"/>（也是自己的專用查詢型別）。
///
/// ⚠️ <b>時間一律是本地時間。</b>資料表的 OccurredAt 存的是 UTC，
/// 而畫面的 DatePicker 給的是本地時間，兩者的換算由查詢服務負責，呼叫端不必自己轉。
/// </summary>
public class AuditLogQuery
{
    /// <summary>起始時間（本地時間），比對 OccurredAt。</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>結束時間（本地時間），比對 OccurredAt。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>操作者帳號，部分比對。</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>動作代碼，精確比對；空字串表示不限。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>成敗篩選：true 只看成功、false 只看失敗、null 不限。</summary>
    public bool? Success { get; set; }

    /// <summary>關鍵字，同時比對 動作／帳號／目標類型／目標識別／摘要／來源 IP。</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>來源 IP，前綴比對（輸入前幾段即可，例如 <c>192.168.1</c>）；沒有記錄 IP 的舊紀錄不會符合（0.9.123 起）。</summary>
    public string ClientIp { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}
