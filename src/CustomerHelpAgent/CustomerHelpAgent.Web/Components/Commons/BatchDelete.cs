using System.Text;
using AntDesign;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>批次刪除中沒有刪掉的一筆：畫面上的名稱與原因。</summary>
public sealed record BatchDeleteFailure(string Label, string Reason);

/// <summary>批次刪除的結果。<see cref="FirstException"/> 不為 null 表示途中有未預期的例外（已逐筆記錄）。</summary>
public sealed record BatchDeleteOutcome(int Succeeded, IReadOnlyList<BatchDeleteFailure> Failures, Exception? FirstException);

/// <summary>
/// 列表頁「刪除選取」的共用流程（0.9.132 起）：確認 → 逐筆呼叫該頁既有的單筆刪除 → 彙整通知。
///
/// 刻意逐筆呼叫單筆刪除、不另寫批次服務方法：每頁的刪除規則（團隊範圍、不能刪自己、預設角色…）
/// 與每筆一列的稽核紀錄都留在原處，批次不會變成繞過規則的另一條路。
/// 能刪的照刪、刪不掉的列出原因；某一筆丟例外也不中斷其他筆。
/// </summary>
public static class BatchDelete
{
    /// <summary>確認窗與結果通知最多列出幾筆名稱，其餘只給筆數。</summary>
    public const int MaxListed = 10;

    internal const string UnexpectedReason = "發生未預期的錯誤";

    /// <summary>
    /// 由深到淺排序（相同深度維持原順序）：樹狀資料先刪子再刪父，
    /// 同時勾了父與它底下全部的子時，父才不會因為「還有子項」被擋下。
    /// </summary>
    public static IReadOnlyList<T> DeepestFirst<T>(IEnumerable<T> items, Func<T, int> depthOf)
        => items.OrderByDescending(depthOf).ToList();

    /// <summary>
    /// 確認窗：列出筆數與名稱。<paramref name="restorable"/> 為 true（軟刪除）時用一般確認窗並提示去哪裡還原；
    /// 否則用破壞性（紅色）確認窗。<paramref name="note"/> 是額外說明（例如「原始明細檔也會一併移除」）。
    /// </summary>
    public static Task<bool> AskAsync(ModalService modalService, IReadOnlyList<string> labels, bool restorable, string? note = null)
    {
        var content = BuildConfirmContent(labels, restorable, note);
        return restorable
            ? ConfirmDialog.AskAsync(modalService, "確認刪除", content, "刪除")
            : ConfirmDialog.AskDestructiveAsync(modalService, "確認刪除", content, "刪除");
    }

    /// <summary>
    /// 依序刪除每一筆。<paramref name="keyOf"/> 只用於日誌（請給 Id 或檔名，不要給姓名）；
    /// <paramref name="labelOf"/> 是失敗清單上給使用者看的名稱。
    /// </summary>
    public static async Task<BatchDeleteOutcome> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> keyOf,
        Func<T, string> labelOf,
        Func<T, Task<VerifyRecordResult>> deleteOne,
        ILogger logger)
    {
        var succeeded = 0;
        var failures = new List<BatchDeleteFailure>();
        Exception? firstException = null;

        foreach (var item in items)
        {
            try
            {
                var result = await deleteOne(item);
                if (result.Success)
                {
                    succeeded++;
                    continue;
                }

                logger.LogInformation("Batch delete item rejected. ItemKey={ItemKey}, Message={Message}", keyOf(item), result.Message);
                failures.Add(new BatchDeleteFailure(labelOf(item), result.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Batch delete item failed. ItemKey={ItemKey}", keyOf(item));
                firstException ??= ex;
                failures.Add(new BatchDeleteFailure(labelOf(item), UnexpectedReason));
            }
        }

        logger.LogInformation("Batch delete completed. Requested={Requested}, Succeeded={Succeeded}, Failed={Failed}",
            items.Count, succeeded, failures.Count);
        return new BatchDeleteOutcome(succeeded, failures, firstException);
    }

    /// <summary>
    /// 全部成功：一般成功通知。有失敗：可複製、不自動關閉的錯誤通知並列出原因；
    /// 途中有例外時改用 <see cref="ViewNotification.UnexpectedError"/>（附追蹤碼與例外詳細資料）。
    /// </summary>
    public static void Notify(NotificationService notificationService, BatchDeleteOutcome outcome)
    {
        var text = BuildResultText(outcome);
        if (outcome.Failures.Count == 0)
        {
            ViewNotification.Warning(notificationService, text);
        }
        else if (outcome.FirstException is { } ex)
        {
            ViewNotification.UnexpectedError(notificationService, text, ex);
        }
        else
        {
            ViewNotification.Error(notificationService, text);
        }
    }

    internal static string BuildConfirmContent(IReadOnlyList<string> labels, bool restorable, string? note)
    {
        var sb = new StringBuilder();
        sb.Append("確定要刪除以下 ").Append(labels.Count).Append(" 筆紀錄嗎？");
        foreach (var label in labels.Take(MaxListed))
        {
            sb.Append('\n').Append("・").Append(label);
        }

        if (labels.Count > MaxListed)
        {
            sb.Append('\n').Append("…等共 ").Append(labels.Count).Append(" 筆");
        }

        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.Append('\n').Append(note);
        }

        sb.Append('\n').Append(restorable
            ? "刪除後可在工具列的「顯示已刪除」中還原。"
            : "此操作無法復原。");
        return sb.ToString();
    }

    internal static string BuildResultText(BatchDeleteOutcome outcome)
    {
        if (outcome.Failures.Count == 0)
        {
            return $"已刪除 {outcome.Succeeded} 筆";
        }

        var sb = new StringBuilder();
        sb.Append("已刪除 ").Append(outcome.Succeeded).Append(" 筆，").Append(outcome.Failures.Count).Append(" 筆未刪除：");
        foreach (var failure in outcome.Failures.Take(MaxListed))
        {
            sb.Append('\n').Append("・").Append(failure.Label).Append("：").Append(failure.Reason);
        }

        if (outcome.Failures.Count > MaxListed)
        {
            sb.Append('\n').Append("另有 ").Append(outcome.Failures.Count - MaxListed).Append(" 筆未列出");
        }

        return sb.ToString();
    }
}
