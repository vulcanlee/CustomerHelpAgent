using AntDesign;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 檢視層的通知樣板。
///
/// 五個 CRUD 檢視原本各自重複 9～11 個結構完全相同的 <see cref="NotificationConfig"/> 區塊
/// （共 48 個），只有 Description 與 NotificationType 不同。
/// 收斂到這裡之後，新模組不需要再靠複製貼上取得一致的通知外觀，
/// 也不會出現「某個檢視忘了設 Placement」這種只在該頁看得出來的漂移。
///
/// 錯誤與警告（<see cref="Error"/>、<see cref="UnexpectedError"/>、<see cref="ValidationError"/>、<see cref="Caution"/>）
/// 0.9.129 起：附「複製」按鈕、不自動關閉（使用者按 X 關閉），並在顯示當下寫一筆日誌（帶追蹤碼，/logs 查得到）。
/// 呼叫端原本自己寫的 log 照留：那筆有例外與上下文，這筆記錄的是「使用者實際看到什麼」。
/// </summary>
public static class ViewNotification
{
    private const string SystemTitle = "系統訊息";
    private const string ValidationTitle = "驗證失敗";

    // static 類別拿不到 DI，由 Program.cs 啟動時設定；沒設定（元件測試）就不寫。
    private static ILogger logger = NullLogger.Instance;

    public static void UseLoggerFactory(ILoggerFactory loggerFactory)
        => logger = loggerFactory.CreateLogger(typeof(ViewNotification).FullName!);

    /// <summary>
    /// 一般系統訊息。注意：本專案的「新增成功／修改成功／刪除成功」等訊息
    /// **沿用 Warning 型別**（既有外觀，非筆誤），因此刻意不提供 Success 多載，
    /// 以免有人不小心改掉既有的視覺表現。真正需要使用者注意的警告請用 <see cref="Caution"/>。
    /// </summary>
    public static void Warning(NotificationService notificationService, string description)
        => Open(notificationService, BuildConfig(SystemTitle, description, NotificationType.Warning));

    /// <summary>
    /// 真正的警告（例如「瀏覽器拒絕存取剪貼簿」「部分資料未處理」）：與 <see cref="Warning"/> 同樣是橘色圖示，
    /// 但附「複製」按鈕、不自動關閉，並寫一筆 Warning 日誌。成功訊息請繼續用 <see cref="Warning"/>。
    /// </summary>
    public static void Caution(NotificationService notificationService, string description)
        => OpenCopyable(notificationService, SystemTitle, description, NotificationType.Warning, LogLevel.Warning);

    /// <summary>
    /// 業務規則拒絕（名稱重複、沒有權限…）；日誌記 Warning，Error 等級留給真正的系統故障。
    /// <paramref name="detail"/>：沒有例外物件、但有技術細節可給管理員的失敗（例如 AI 的 HTTP 狀態與上游代碼），
    /// 只在按「複製」時附上，不上畫面、不進日誌。catch 到例外請改用 <see cref="UnexpectedError"/>。
    /// </summary>
    public static void Error(NotificationService notificationService, string description, string? detail = null)
        => OpenCopyable(notificationService, SystemTitle, description, NotificationType.Error, LogLevel.Warning, detail: detail);

    /// <summary>
    /// 未預期例外（catch 到並已 <c>LogError</c>）的錯誤通知：自動附上錯誤追蹤碼（LOG-10），
    /// 讓使用者複製或抄下來回報。「名稱重複」這類一般驗證訊息請用 <see cref="Error"/>，不附追蹤碼。
    ///
    /// <paramref name="exception"/> 必填（0.9.130 起）：管理員（或 <c>ExceptionDetailPolicy</c> 允許時）按「複製」會附上
    /// 完整的 <c>exception.ToString()</c>（類型、訊息、內層例外、堆疊）。畫面與這筆通知日誌都不含它 ——
    /// 例外本身已由呼叫端的 <c>LogError(ex, …)</c> 記錄。
    /// </summary>
    public static void UnexpectedError(NotificationService notificationService, string description, Exception exception)
        => OpenCopyable(notificationService, SystemTitle, description, NotificationType.Error, LogLevel.Error, showTraceCode: true, detail: exception.ToString());

    /// <summary>
    /// 進行中或純資訊性的訊息（例如「正在送出，請稍候」）。
    ///
    /// 之所以與 <see cref="Warning"/> 分開：CRUD 的「新增成功」沿用 Warning 外觀是既有約定，
    /// 但「正在處理中」配上橘色警告圖示會讓人以為出了問題。0.9.6 起因 AI 分析需要階段提示
    /// 而新增，其他頁面的既有外觀不受影響。
    /// </summary>
    public static void Info(NotificationService notificationService, string description)
        => Open(notificationService, BuildConfig(SystemTitle, description, NotificationType.Info));

    /// <summary>
    /// 表單驗證失敗。標題與一般系統訊息區隔，讓使用者知道是自己的輸入有問題。
    /// </summary>
    public static void ValidationError(NotificationService notificationService, string description)
        => OpenCopyable(notificationService, ValidationTitle, description, NotificationType.Error, LogLevel.Warning);

    private static void OpenCopyable(
        NotificationService notificationService,
        string title,
        string description,
        NotificationType notificationType,
        LogLevel logLevel,
        bool showTraceCode = false,
        string? detail = null)
    {
        // 在呼叫當下寫：這時 ScopeContext 裡是這次互動的追蹤碼，日誌的 TraceId 欄位才對得上。
        logger.Log(logLevel, "Notification shown to user. Title={Title}, Kind={NotificationKind}, Text={NotificationText}",
            title, notificationType, description);

        var traceCode = TraceCode.Current;
        Open(notificationService, BuildCopyableConfig(title, description, notificationType, traceCode, DateTime.Now, showTraceCode, detail));
    }

    internal static NotificationConfig BuildConfig(string title, string description, NotificationType notificationType)
        => new()
        {
            Message = title,
            Description = description,
            NotificationType = notificationType,
            Placement = NotificationPlacement.BottomRight
        };

    /// <summary>有「複製」按鈕的版本：Duration = 0（不自動關閉），否則使用者還沒按到複製通知就消失了。</summary>
    internal static NotificationConfig BuildCopyableConfig(
        string title,
        string description,
        NotificationType notificationType,
        string? traceCode,
        DateTime occurredAt,
        bool showTraceCode,
        string? detail = null)
    {
        var shown = showTraceCode ? description + TraceCode.Suffix(traceCode) : description;
        var config = BuildConfig(title, shown, notificationType);
        config.Duration = 0;

        // 每則通知一個 key：AntDesign 重繪通知清單時，「已複製」的狀態不會跑到別則通知的按鈕上。
        var key = Guid.NewGuid();
        config.Btn = builder =>
        {
            builder.OpenComponent<ToastCopyButton>(0);
            builder.SetKey(key);
            builder.AddComponentParameter(1, nameof(ToastCopyButton.Title), title);
            builder.AddComponentParameter(2, nameof(ToastCopyButton.Description), description);
            builder.AddComponentParameter(3, nameof(ToastCopyButton.TraceCode), traceCode);
            builder.AddComponentParameter(4, nameof(ToastCopyButton.OccurredAt), occurredAt);
            builder.AddComponentParameter(5, nameof(ToastCopyButton.Detail), detail);
            builder.CloseComponent();
        };

        return config;
    }

    private static void Open(NotificationService notificationService, NotificationConfig config)
        => _ = notificationService.Open(config);
}
