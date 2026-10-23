using AntDesign;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 表單對話窗「確定／取消」的共用流程。
///
/// AntDesign 的 Modal 在呼叫 <c>OnOk</c>／<c>OnCancel</c> **之前**就已經送出
/// <c>VisibleChanged(false)</c>，而且宣告式 <c>&lt;Modal&gt;</c> 無法否決關窗
/// （<c>ModalClosingEventArgs.Cancel</c> 只有 ModalService 建立的窗才管用）。
/// 因此每個 View 的 handler **第一行**都必須先把 Visible 搶回來，再用回傳值決定最終開關。
///
/// 這樣做不會閃爍：<c>VisibleChanged(false)</c> 與 handler 之間沒有任何 await 讓步，
/// 整段流程都在同一個 render batch 內，那個 false 從來不會被畫出來。
/// 副作用是等待確認窗期間大表單窗會**維持開啟**（確認窗疊在上面），
/// 比舊寫法「大窗先消失、確認窗孤零零彈出」好。
///
/// 收斂的對象：舊寫法在每條失敗路徑各補一次 <c>modalVisible = true;</c>（一個 View 重複四次），
/// 只要新增一條早退路徑而忘了補，症狀就是「按確定 → 驗證失敗 → 窗關了 → 輸入全丟」。
/// </summary>
public static class FormModalFlow
{
    /// <summary>
    /// 取消／✕／ESC 的共用流程（遮罩已由 <c>MaskClosable="false"</c> 擋掉）。
    /// 無變更時直接關閉，不打擾使用者。
    /// </summary>
    /// <returns>對話窗是否要**維持開啟**。</returns>
    public static async Task<bool> ConfirmCloseAsync(ModalService modalService, bool isDirty)
    {
        if (isDirty == false)
        {
            return false;
        }

        return await FormEditConfirm.AskDiscardChangesAsync(modalService) == false;
    }

    /// <summary>
    /// 「確定」的共用流程。<paramref name="saveAsync"/> 回傳 true 表示已存檔可以關窗；
    /// 所有失敗與使用者中止的路徑只要 <c>return false</c>，不必再碰 <c>modalVisible</c>。
    ///
    /// 未預期例外一律攔下來：這些寫入操作若讓例外逸出，會直接拆掉 Blazor circuit，
    /// 使用者只看到畫面斷線、日誌上也留不下痕跡。
    /// </summary>
    /// <returns>對話窗是否要**維持開啟**。</returns>
    public static async Task<bool> RunOkAsync(
        Func<Task<bool>> saveAsync,
        ILogger logger,
        NotificationService notificationService,
        string unexpectedErrorMessage)
    {
        try
        {
            return await saveAsync() == false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while saving from a form modal.");
            ViewNotification.UnexpectedError(notificationService, unexpectedErrorMessage, ex);

            // 出錯不關窗，使用者的輸入還在。
            return true;
        }
    }
}
