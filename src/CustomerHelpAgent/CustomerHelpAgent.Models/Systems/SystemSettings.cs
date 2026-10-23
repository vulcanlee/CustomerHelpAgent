namespace CustomerHelpAgent.Models.Systems;

public class SystemSettings
{
    public ConnectionStrings ConnectionStrings { get; set; } = new();
    public SystemInformation SystemInformation { get; set; } = new();
    public ExternalFileSystem ExternalFileSystem { get; set; } = new();
    public UploadSettings Upload { get; set; } = new();
}

public class UploadSettings
{
    /// <summary>
    /// 允許上傳的副檔名白名單（例如 ".pdf"）。
    /// 留空表示採用 <c>UploadFileTypePolicy</c> 的內建預設清單。
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [];
}

public class ConnectionStrings
{
    public string SQLiteDefaultConnection { get; set; } = string.Empty;

}
public class SystemInformation
{
    public string SystemVersion { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string SystemDescription { get; set; } = string.Empty;
}
public class ExternalFileSystem
{
    public string DatabasePath { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string UploadPath { get; set; } = string.Empty;
    public string ProjectFilePath { get; set; } = string.Empty;

    /// <summary>
    /// 系統例外紀錄的堆疊檔案存放目錄。每一種例外只在首次發生時寫一個檔，
    /// 檔案生命週期一律經由 ExceptionStackFileStore 處理。
    /// </summary>
    public string ExceptionPath { get; set; } = string.Empty;

    /// <summary>
    /// Token 用量紀錄的原始 usage JSON 存放目錄。每次 LLM 呼叫一個檔，
    /// 檔案生命週期一律經由 TokenUsageRawStore 處理。
    /// ⚠️ 只存 usage 結構，絕不存提示詞或模型回應內文（完整內容只存在 <see cref="AiCallLogPath"/>）。
    /// </summary>
    public string TokenUsagePath { get; set; } = string.Empty;

    /// <summary>
    /// AI 對話紀錄的內容檔存放目錄（0.9.72 起）：每次 AI 呼叫一個檔，內含完整請求與回應。
    /// 結構為 <c>{root}/{yyyyMM}/{CallId}.json</c>；檔案生命週期一律經由 AiCallLogFileStore 處理。
    /// ⚠️ 內容含日誌、例外堆疊與使用者帳號，只供管理員查閱；
    /// <b>絕不可放在 DownloadPath 底下</b>（那裡以靜態檔案對外提供）。
    /// </summary>
    public string AiCallLogPath { get; set; } = string.Empty;

    /// <summary>
    /// 稽核紀錄的詳細內容檔存放目錄（0.9.131 起）：修改前後差異、完整查詢條件這類長內容，
    /// 結構為 <c>{root}/{yyyyMM}/{Guid}.txt</c>；檔案生命週期一律經由 AuditContentFileStore 處理。
    /// ⚠️ 內容含舊值→新值（可能有 Email、姓名）與搜尋原文，只供管理員查閱；
    /// <b>絕不可放在 DownloadPath 底下</b>（那裡以靜態檔案對外提供）。
    /// </summary>
    public string AuditContentPath { get; set; } = string.Empty;

    /// <summary>
    /// ASP.NET Core Data Protection 的金鑰環存放目錄。
    ///
    /// ⚠️ **這個目錄不見了，等於全站使用者立刻被登出** —— 登入 Cookie 是用這裡的金鑰
    /// 加密的，金鑰換一批就全部解不開。0.9.39 之前完全沒有設定，金鑰落在使用者設定檔下，
    /// 在 IIS 應用程式集區未載入使用者設定檔時會退化成「只存在記憶體」，
    /// 於是每次回收都換一批。詳見正式部署與安全檢查清單。
    /// </summary>
    public string DataProtectionKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// 系統備份 zip 的存放目錄（0.9.99 起）：資料庫快照、專案附件、例外堆疊檔、Token 原始檔、稽核內容檔與 Data Protection 金鑰環。
    ///
    /// ⚠️ 備份含全部資料與金鑰環，拿到的人可以偽造登入 Cookie：目錄權限只給應用程式集區身分與管理員。
    /// 啟動驗證不允許它與其他任何資料目錄重疊（尤其不可放在 <see cref="DownloadPath"/> 底下 —— 那裡以靜態檔案對外提供），
    /// 也不可放在網站目錄底下。
    /// </summary>
    public string BackupPath { get; set; } = string.Empty;
}

public class BootstrapSettings
{
    public string SupportAccount { get; set; } = "support";
    public string SupportName { get; set; } = "support";
    public string SupportEmail { get; set; } = "support";
    public string SupportPassword { get; set; } = "support";
}
