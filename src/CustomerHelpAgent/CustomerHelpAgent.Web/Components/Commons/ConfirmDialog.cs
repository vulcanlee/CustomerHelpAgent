using AntDesign;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 小型確認窗的唯一入口。
///
/// 為什麼要收斂：各檢視自己寫 <c>ConfirmAsync</c> 時，該帶的參數必定會漏。
/// 0.9.25 之前就出現過 —— 例外紀錄與 Token 用量的六個「刪除／清空」全都少了
/// <c>OkButtonProps.Danger</c> 與 <c>MaskClosable = false</c>：長得像一般提醒，
/// 而且**誤點遮罩就直接執行了破壞性動作**。這種漏法不會壞、不會紅，只有使用者踩到才知道。
///
/// 視覺上也只能靠這裡分辨輕重：AntDesign 的 Confirm 會把 <c>ConfirmOptions.ClassName</c>
/// 無條件覆寫掉，個別確認窗無法掛自訂 class，所以「破壞性要長得不一樣」是靠
/// <c>OkButtonProps.Danger</c> 產生的 <c>.ant-btn-dangerous</c> 當 CSS hook
/// （見 <c>OverlayStyles.razor</c> 的 <c>.ant-modal-confirm:has(.ant-btn-dangerous)</c>）。
/// 少設一次 Danger，那個窗就不會轉紅調。
///
/// 具體文案仍由呼叫端提供 ——「清除 30 天未再發生的紀錄」這種訊息有資訊價值，
/// 不該為了收斂而被壓成罐頭句子。樣板只負責統一參數與按鈕行為。
/// </summary>
public static class ConfirmDialog
{
    /// <summary>
    /// 確認窗必須疊在表單對話窗之上。
    /// 0.9.25 起表單窗在等待確認期間會維持開啟（見 <see cref="FormModalFlow"/>），
    /// 沿用預設的 1000 就只剩 DOM 先後可以決定疊放順序。
    /// </summary>
    internal const int AboveFormModal = 1100;

    /// <summary>
    /// 破壞性動作（刪除、清空、放棄變更）的確認。
    /// 一律 <c>Danger</c> ＋ <c>MaskClosable = false</c>：誤點遮罩不該執行不可復原的動作。
    /// 回傳 true 表示使用者確認執行。
    /// </summary>
    public static Task<bool> AskDestructiveAsync(
        ModalService modalService,
        string title,
        string content,
        string okText,
        string cancelText = "取消")
    {
        return modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = title,
            Content = content,
            OkText = okText,
            CancelText = cancelText,
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
            ZIndex = AboveFormModal,
        });
    }

    /// <summary>
    /// 非破壞性的確認與提醒（要不要這樣儲存、要不要套用某個設定）。
    /// 刻意不加 <c>Danger</c>：把每個提醒都染紅，紅色就不再代表任何事。
    /// 回傳 true 表示使用者選擇繼續。
    /// </summary>
    public static Task<bool> AskAsync(
        ModalService modalService,
        string title,
        string content,
        string okText,
        string cancelText = "取消")
    {
        return modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = title,
            Content = content,
            OkText = okText,
            CancelText = cancelText,
            MaskClosable = false,
            ZIndex = AboveFormModal,
        });
    }

    /// <summary>
    /// 軟刪除的「刪除這一筆」確認（0.9.94 起：專案、分類、團隊）。資料可以還原，所以不用破壞性的紅色樣式，
    /// 但仍維持 <c>MaskClosable = false</c>，並明確告訴使用者去哪裡還原。
    /// </summary>
    public static Task<bool> AskSoftDeleteRecordAsync(ModalService modalService)
        => AskAsync(
            modalService,
            "確認刪除",
            "確定要刪除這筆紀錄嗎？刪除後可在工具列的「顯示已刪除」中還原。",
            "刪除");

    /// <summary>
    /// 實體刪除的「刪除這一筆」確認。0.9.95 起內建頁面全部改為軟刪除、改用 <see cref="AskSoftDeleteRecordAsync"/>；
    /// 保留給衍生專案中真正無法復原的刪除（CRUD 樣板與守門測試仍引用）。
    /// </summary>
    public static Task<bool> AskDeleteRecordAsync(ModalService modalService)
        => AskDestructiveAsync(
            modalService,
            "確認刪除",
            "確定要刪除這筆紀錄嗎？此操作無法復原。",
            "刪除");
}
