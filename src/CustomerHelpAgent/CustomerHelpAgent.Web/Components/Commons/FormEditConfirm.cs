using AntDesign;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 表單對話窗的二次確認樣板（離開／儲存）。
///
/// 兩段提示是一組對偶，拆成兩個檔案只會讓日後有人改了一邊忘了另一邊，
/// 文案也必定漂移成「這個窗問得很兇、那個窗問得很客氣」。
///
/// 參數（Danger／MaskClosable／ZIndex）統一由 <see cref="ConfirmDialog"/> 決定，
/// 這裡只負責這兩段提示的文案。
/// </summary>
public static class FormEditConfirm
{
    /// <summary>
    /// 有未儲存變更卻要離開時詢問。
    /// 回傳 true 表示使用者選擇「放棄」變更，false 表示要「繼續編輯」。
    /// </summary>
    public static Task<bool> AskDiscardChangesAsync(ModalService modalService)
        => ConfirmDialog.AskDestructiveAsync(
            modalService,
            "尚有未儲存的變更",
            "離開後將遺失這些變更，確定要放棄嗎？",
            "放棄變更",
            "繼續編輯");

    /// <summary>
    /// 按下「確定」且確實有變更時詢問。
    /// 回傳 true 表示使用者選擇「儲存」，false 表示要「再檢查」。
    /// </summary>
    public static Task<bool> AskSaveAsync(ModalService modalService)
        => ConfirmDialog.AskAsync(
            modalService,
            "確認儲存",
            "確定要儲存這筆記錄嗎？",
            "儲存",
            "再檢查");
}
