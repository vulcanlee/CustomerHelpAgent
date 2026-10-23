using CustomerHelpAgent.Models.Others;

namespace CustomerHelpAgent.Business.Services.Other;

public class CurrentUserService
{
    public CurrentUser CurrentUser { get; set; } = new();

    /// <summary>
    /// 目前使用者的資料更新了（0.9.102 起）：每次換頁的登入檢查載入最新資料後，以及使用者在個人資料頁存檔後。
    /// 右上角的姓名與縮寫靠這個事件更新 —— ⚠️ 不能改聽換頁事件，它在新頁面的登入檢查之前就觸發，讀到的還是舊資料。
    /// </summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
