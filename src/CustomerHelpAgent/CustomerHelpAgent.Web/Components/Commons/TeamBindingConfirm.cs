using AntDesign;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 「團隊欄位沒設定就要儲存」的確認對話窗樣板。
///
/// 分類與專案的編輯畫面都有團隊欄位，未設定時的影響不同（一個是變成公用分類、
/// 一個是變成公開紀錄），但提醒的時機、按鈕文案與取消後的行為必須一致，
/// 因此把對話窗設定收斂在這裡，呼叫端只負責提供「會有什麼影響」這句話。
///
/// 這是提醒而非破壞性動作，走 <see cref="ConfirmDialog.AskAsync"/>（不加 Danger）。
/// </summary>
public static class TeamBindingConfirm
{
    /// <summary>
    /// 詢問使用者是否要在團隊欄位未妥善設定的情況下儲存。
    /// 回傳 true 表示使用者選擇「仍要儲存」，false 表示要「回去編輯」。
    /// </summary>
    public static Task<bool> AskAsync(ModalService modalService, string content)
        => ConfirmDialog.AskAsync(
            modalService,
            "確認團隊設定",
            content,
            "仍要儲存",
            "回去編輯");
}
