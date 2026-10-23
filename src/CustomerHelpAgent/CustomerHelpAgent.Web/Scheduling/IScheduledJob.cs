namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 排程作業（0.9.96 起）。週期性的背景工作一律實作這個介面並以
/// <see cref="ScheduledJobServiceCollectionExtensions.AddScheduledJob{TJob}"/> 註冊，不要再自己寫 BackgroundService 計時器 ——
/// 那樣沒有執行紀錄、IIS 每次回收都會重跑、重疊回收時兩個行程各跑一次。
///
/// 作業類別註冊為 scoped，每次執行都在新的 scope 解析，可以放心注入 scoped 服務（DbContext、稽核）。
/// 框架負責：排程時間、補跑、跨行程只跑一次、執行紀錄、錯誤追蹤碼與系統例外紀錄。作業只要把事情做完並回報結果。
///
/// ⚠️ 刪檔一律經對應的 file store（速查表 §6.6、§6.7）；長時間的作業要分批並在批次之間檢查 <c>cancellationToken</c>。
/// </summary>
public interface IScheduledJob
{
    Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}

/// <summary>一次執行的情境。</summary>
/// <param name="RunId">執行紀錄的 Id。</param>
/// <param name="Trigger">觸發方式（<c>JobRunTriggers</c>）。</param>
/// <param name="ScheduledForUtc">對應的排程時段；手動執行為 null。</param>
/// <param name="TriggeredByAccount">手動執行的操作者帳號；排程與補跑為 null。</param>
public sealed record ScheduledJobContext(int RunId, string Trigger, DateTime? ScheduledForUtc, string? TriggeredByAccount, int? TriggeredByUserId = null);

/// <summary>
/// 作業的結果。服務層若已經自己處理並記錄了錯誤（例如回傳 null），作業要回 <see cref="Failure"/> ——
/// 只回一段文字的話會被記成成功；也不要再丟例外，否則同一個錯誤會在系統例外紀錄出現兩筆。
/// 真正沒預料到的例外直接讓它丟出，框架會記錄並附上錯誤追蹤碼。
/// </summary>
public sealed record ScheduledJobResult(bool Succeeded, string? Message)
{
    public static ScheduledJobResult Success(string? message = null) => new(true, message);

    public static ScheduledJobResult Failure(string message) => new(false, message);
}
