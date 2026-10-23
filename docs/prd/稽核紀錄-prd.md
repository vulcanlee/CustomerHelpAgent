# 稽核紀錄 PRD

- 文件版本：1.18
- 文件狀態：已實作
- 現行系統版本：1.0.3
- 首次實作版本：0.9.42
- 最後核對日期：2026/10/23

## 一、目標與範圍

`AuditLog` 從 0.4.8 起就在寫「誰、何時、對什麼、做了什麼、結果如何」，但一直**只有寫入端**：
有資料表、有服務、有測試，沒有畫面也沒有選單項。客戶問「誰把這個角色的權限改掉了」時，
只能直接開 SQLite 檔查。0.9.42 補上查詢畫面，讓這條軌跡拿得出來。

- 範圍：管理員專屬的稽核事件檢視／查詢／明細／CSV 匯出／清除與清空。
- 非範圍：**不提供 Web API**（內部管理頁）；不做告警通知；
  不修改既有的稽核寫入點。
- 相關：寫入端的事件涵蓋範圍見 [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、
  [登入與帳號流程](登入與帳號流程-prd.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/audit-logs` | 系統管理 → 帳號與權限 → id=35「稽核紀錄」 | **管理員專屬**（`CheckIsAdmin()`；權限鍵不上架角色矩陣）| 系統管理員、稽核人員 |

權限鍵常數為 `MagicObjectHelper.角色_稽核紀錄`，值 `"稽核紀錄"`。
與同群組的「使用者管理」「角色管理」一致，**刻意不列入** `RolePermissionService.GetRoleListPermissionAllName()`
—— 因此不會種出 `Permission` 資料列、任何角色都無法被授予，只有管理員短路能通過。
`AdminOnlyPermissionTests` 會擋下「順手補齊」的改動。

## 三、畫面與欄位

### 3.1 工具列

- 篩選：發生時間範圍（兩個 `DatePicker`）、動作下拉、結果下拉（不限／成功／失敗）、
  操作者帳號、來源 IP（0.9.123 起，前綴比對，沒有 IP 的舊紀錄不符合；匯出 CSV 同條件）、關鍵字（比對動作／操作者／目標類型／目標識別／摘要／來源 IP，0.9.117 起含 IP）。
- **動作下拉的選項取自資料庫的 `SELECT DISTINCT Action`**，不寫死清單：0.9.42 立頁時動作代碼散落在
  15 個呼叫點的字串字面值裡，沒有集中的常數來源，任何手寫清單都會在下次新增稽核事件時默默過期
  （0.9.78 起已收斂到 `AuditActions`，下拉仍讀 DISTINCT，見 §七）。
- 動作（`ToolbarIconButton`）：查詢 `search`、重新整理 `refresh`、匯出 CSV `file_download`、
  清除 N 天前 `history`、清空全部 `delete_forever`。匯出 CSV 0.9.78 起寫一筆 `Audit.Export` 稽核。
- 兩個破壞性動作以 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 匯出的 CSV 為 **UTF-8 含 BOM**（走 `TextDownloadPayload.Utf8WithBom`，`ExportEncodingConventionTests` 守門）。

### 3.2 清單欄位

| 欄位 | 說明 |
| --- | --- |
| 發生時間 | 可排序，預設遞減。**顯示為伺服器本地時間**（資料庫存 UTC）|
| 結果 | 成功綠／失敗紅的 `Tag` |
| 動作 | 動作代碼，依第一節（`Login`／`User`／`Role`／`Permission`／`Audit`，以及後來加入的 `Project`／`Category`／`Team`／`Password`／`Backup` 等各類；未列出的為預設色）上色 |
| 操作者 | 帳號（Id=N）；系統或匿名事件顯示「（系統／匿名）」|
| 來源 IP | `ClientIp`（0.9.117 起）；排程作業與舊紀錄顯示「—」。CSV 加在最後一欄「來源IP」|
| 目標 | `TargetType#TargetId`，兩者皆空顯示破折號 |
| 摘要 | `Detail`，過長以 `Ellipsis` 截斷 |
| 操作 | 「查看」開明細窗 |

### 3.3 明細窗

列出本地時間、**UTC 原值**、結果、動作代碼、操作者、來源 IP、目標類型、目標識別與完整摘要。
UTC 原值一併呈現是刻意的：跨時區協查或要與日誌檔的時間戳對照時會用到。

**詳細內容（0.9.131 起）**：有內容檔的紀錄在摘要下方多一個「詳細內容」區塊 —— 修改的前後差異（`欄位: 舊值 → 新值`）
或查詢的完整條件（含搜尋原文）。內容檔存在 `SystemSettings:ExternalFileSystem:AuditContentPath`（`{yyyyMM}/{Guid}.txt`，月份依 UTC），
資料表只存相對路徑 `ContentFile`；清單查詢不讀檔，開明細窗時才經 `AuditLogQueryService.GetContentAsync` 讀取，檔案不在時顯示說明。
內容檔不進 CSV、不進日誌；清除 N 天前、清空全部與自動清理都會連同內容檔一起刪（`AuditContentFileStore` 是唯一入口），系統備份也會打包（`files/AuditContent`）。

尺寸與內容樣式寫在 `Components/Commons/OverlayStyles.razor` 的 `.audit-log-modal`，
**不是** `AuditLogView.razor.css` —— AntDesign 的 `Modal` 渲染在元件 DOM 之外，
scoped CSS 連 `::deep` 都打不到。

## 四、⚠️ 時區：本頁最關鍵的設計

`AuditLog.OccurredAt` 是**全專案唯一以 UTC 寫入**的時間欄位（`AuditLogService` 用 `DateTime.UtcNow`，
與帳號鎖定的 `LockoutEndUtc` 一致）。相對地，`ExceptionLog` 與 `TokenUsageLog` 寫的都是
`DateTime.Now`（本地時間），所以那兩頁全程不做換算。

**照抄那兩頁會讓整條時間軸偏掉一個時區（台灣 8 小時）。** 換算集中在 `AuditLogQueryService`：

| 邊界 | 方向 | 作法 |
| --- | --- | --- |
| DatePicker → 查詢條件 | 本地 → UTC | `AuditLogQueryService.ToUtc`（`SpecifyKind(Local).ToUniversalTime()`）|
| 資料庫 → 清單／明細／CSV | UTC → 本地 | `AuditLogQueryService.ToLocal`（`SpecifyKind(Utc).ToLocalTime()`）|
| 清除門檻 | — | `DateTime.UtcNow.AddDays(-days)`，**不是** `DateTime.Now` |

`AuditLogAdapterModel.OccurredAt` 拿到時**已經是本地時間**，呼叫端不可以再轉一次。

`AuditLogQueryServiceTests` 有三條測試釘住這三個邊界；把換算拿掉會立刻轉紅（已實測）。

**已知限制**：Blazor Server 的 `ToLocalTime()` 取的是**伺服器**時區，不是瀏覽器時區。
與 `SystemHealthPage`、日誌檢視、例外紀錄、Token 用量四頁的口徑一致，刻意不另做 JS interop。

## 五、清除與保存

- 「清除 N 天前」的 N 讀 `LogRetentionSettings:AuditLogDays`（0.9.78 起，原為程式常數 365）；設 0 停用自動清理時退回 365。
  預設 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，稽核軌跡清的是責任證據。
- **自動清理**（0.9.78 起）：排程作業「稽核紀錄清理」（0.9.96 起預設每天 03:00；之前是 `LogRetentionWorker` 啟動時與每日）刪除早於 N 天（UTC）的紀錄，
  刪到資料時寫一筆 `Audit.AutoPurge`（操作者顯示「（系統／匿名）」）。有法規或內控保存要求時請調大天數或設 0。
- **清除與清空動作本身會各寫一筆稽核紀錄**（`Audit.Purge` / `Audit.ClearAll`，含操作者與筆數）。
  寫入排在刪除之後，所以那一筆會留在清空後的資料表裡。
  ⚠️ 這是刻意的：稽核軌跡可被管理員一鍵抹除，若不記錄，「誰抹除了稽核紀錄」會是系統裡唯一查不到的事。
- 稽核寫入失敗只記錯誤、不向使用者報錯 —— 不應推翻「已經刪除」這個事實。

## 六、資料來源

0.9.78 起所有動作代碼集中在 `CustomerHelpAgent.Business/Helpers/AuditActions.cs`（0.9.78 時約 55 個，現行 140 個），呼叫點一律引用常數，
由 `AuditConventionTests` 守門；0.9.78 新增的事件（分類／團隊／專案增刪改、附件、登出、SSO、自行改密碼、JWT 刷新失敗、
Blazor 頁面權限拒絕、各頁匯出與維護、自動清理）見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) §6.3 LOG-14。
**來源 IP（0.9.117 起）** 不由呼叫端傳入：`AuditLogService` 寫入時向 `IClientIpProvider` 取，
實作 `Web/Auth/ClientIpProvider` 依情境決定 —— SSR 頁面（登入／登出／兩步驟驗證）與 Web API 讀 `HttpContext.Connection.RemoteIpAddress`；
Blazor 互動 circuit 由 `ApplicationCircuitHandler.OnConnectionUpAsync` 在連線建立與重連時寫入（circuit 內沒有可靠的 HttpContext）；
排程作業兩者皆無，存 null。IPv4-mapped IPv6 轉回 IPv4。部署在反向代理後面時必須設定 `ForwardedHeaders`，否則記到的是代理的 IP。

**0.9.131 起的記錄範圍**：除了改變狀態的動作與安全事件，也記使用者**開了哪些頁、查了什麼、看了哪一筆、改了什麼**：

| 動作代碼 | 來源與內容 |
| --- | --- |
| `Page.View` | `ApplicationCircuitHandler`：已登入使用者每換一頁一筆（只改 query string 不算），進入的第一頁在第一次連線建立時寫；`TargetId` 是路徑（不含 query string），Detail `from=上一頁`（第一頁為 `from=entry`）|
| `Category.Search` / `Project.Search` / `Team.Search` / `User.Search` / `Role.Search` | 五個主要資料頁按「搜尋」（翻頁、排序、清除不記）；Detail 只有短摘要（`hasKeyword=…; showDeleted=…`），完整條件含搜尋原文在內容檔 |
| `Category.View` / `Project.View` / `Team.View` / `User.View` / `Role.View` | 五個主要資料頁按「修改」開窗（查看那一筆）；Detail 是名稱或帳號 |
| `LogViewer.Search` / `ExceptionLog.Search` / `AiCallLog.Search` / `TokenUsage.Search` / `Audit.Search` | 管理診斷頁按「查詢」（日誌檢視打開頁面時的自動查詢不記）；條件同上 |
| `ExceptionLog.View` / `AiCallLog.View` / `TokenUsage.View` / `Audit.View` | 開明細窗。日誌檢視的展開只發生在瀏覽器端，沒有 `LogViewer.View` |

**修改前後差異**：分類、專案、團隊、使用者、角色的修改（Blazor 與 Web API 兩條路徑）以及個人資料改姓名，
由 `AuditDiff` 比對修改前後的純量欄位寫進內容檔；Detail 維持原本的短摘要。主鍵、版本號、建立／更新時間、軟刪除欄位不比；
使用者的密碼只記「已變更」，Salt、SecurityStamp、兩步驟密鑰、Google 綁定、鎖定與失敗次數不比；額外角色、團隊、角色權限與預設團隊列出新增與移除。

**同步到日誌**：`AuditLogService` 每寫一筆就記一行 Debug `Audit recorded. Action=…, Detail=…, ContentFile=…`（只有短摘要與檔名，不含內容檔）。
Debug 預設不寫進檔案，要到「日誌等級設定」調到 Debug 才看得到。

下表為 0.9.42 立頁時的寫入端：

| 動作代碼 | 來源 |
| --- | --- |
| `Login.Success` / `Login.Failed` / `Login.Disabled` / `Login.LockedOut` | `MyUserServiceLogin`（0.9.101 起 `LockedOut` 只記造成鎖定的那一次；鎖定中的嘗試記 `Failed`、detail `reason=Locked`）|
| `User.Create` / `User.Update` / `User.Delete`（0.9.95 起為軟刪除）/ `User.Restore` / `User.Purge` / `User.Unlock`（0.9.101 起，解除登入鎖定）/ `User.Enable` / `User.Disable`（0.9.120 起，清單點狀態標籤；detail `account=…`）| `MyUserService` |
| `Team.Enable` / `Team.Disable` / `Category.Enable` / `Category.Disable` | `TeamService`、`CategoryService` 的 `SetEnabledAsync`（0.9.120 起，清單點狀態標籤；detail `name=…`）|
| `User.ProfileUpdate` | `ProfileService`（使用者在個人資料頁改自己的姓名，0.9.102 起；detail 只寫 `field=Name`，不記姓名內容）|
| `Role.Create` / `Role.Update` / `Role.Delete`（0.9.95 起為軟刪除）/ `Role.Restore` / `Role.Purge` | `RoleViewService` |
| `Job.Trigger` / `Job.Enable` / `Job.Disable` | `ScheduledJobView`（「排程作業」頁的手動操作，0.9.96 起；排程自己跑的結果記在執行紀錄）；「系統備份」頁的立即備份也由 `BackupView` 記 `Job.Trigger` |
| `AiCallLog.AutoPurge` / `TokenUsage.AutoPurge` | 排程作業的自動清除（0.9.96 起） |
| `Backup.Create` / `Backup.AutoPurge` / `Backup.Download` / `Backup.Delete` | 系統備份（0.9.99 起）：`SystemBackupJob`（建立、依份數刪除）、`BackupController`（下載，續傳只記第一段）、`BackupView`（刪除） |
| `Announcement.Create` / `Announcement.Update` / `Announcement.Delete` | `AnnouncementView`（「公告管理」頁，0.9.100 起） |
| `Notification.AutoPurge` | 排程作業「站內通知清理」（0.9.100 起） |
| `Notification.Test` | `NotificationTestService`（「系統健康監控」的發送測試通知，0.9.122 起；detail `recipients=N`，發送失敗加 `; failed`） |
| `SystemParameter.Update` / `SystemParameter.Reset` | `SystemParameterView`（「系統參數」頁的修改與還原，0.9.98 起；Detail `key=…; old=…(來源); new=…(來源)`） |
| `Project.AutoPurge` / `Category.AutoPurge` / `Team.AutoPurge` / `User.AutoPurge` / `Role.AutoPurge` | 排程作業「已刪除資料清理」（0.9.97 起）：每種資料每次一筆彙總，無操作者，`TargetId` 為 `*`，Detail 為 `rows=N; days=D; trigger=T; items=#Id 名稱, …`（上限 1000 字） |
| `Permission.Denied` | `HasPermissionAttribute`（API 動作級授權被拒）；0.9.78 起 Blazor 頁面權限拒絕也記（`AuthenticationStateHelper.RecordPageAccessDeniedAsync`）|
| `Project.FileDownload` | `ProjectFileController` |
| `Shop.Request.Submit` / `Shop.Request.Resubmit` / `Shop.Request.MoreInfo` / `Shop.Request.Decide` / `Shop.Policy.AddVersion` / `Shop.Attachment.Download` | 售後展示（1.0.3 起）：`AfterSalesRequestService`（顧客送件、補件；客服要求補件、核准／駁回）、`AfterSalesPolicyService`（店家新增規則版本）、`AfterSalesFileController`（下載申請照片，成功才記）。Detail 只有編號、類型、狀態等，不含說明、地址、手機 |
| `LogLevel.Apply` / `LogLevel.Restore` | `LogLevelSettingView` |
| `LogViewer.AiAnalyze` / `LogViewer.AiAnalyzeExportPdf` | `LogViewerView` |
| `Audit.Purge` / `Audit.ClearAll` | `AuditLogView`（0.9.42 起）|
| `Email.Test` | `EmailTestService`（系統健康監控頁的寄信測試，0.9.59 起；detail 只有 provider 與成敗，不含收件者）|

## 七、已知限制與規劃中需求

**已知限制**

1. ~~動作代碼沒有單一來源~~ —— 0.9.78 已收斂到 `AuditActions` 並以測試守門。
   下拉選單仍讀資料庫 DISTINCT：舊資料可能有已更名或移除的代碼。
2. **`AuditLog.OccurredAt` 沒有單欄索引**：`ExceptionLog.LastOccurredAt` 與 `TokenUsageLog.OccurredAt`
   在 `BackendDBContext` 都建了索引，`AuditLog` 只有 0.9.102 為個人資料頁加的複合索引 `(ActorUserId, OccurredAt)`
   （以操作者開頭，本頁依時間範圍查詢用不到）。資料量大到影響查詢時應補一支 migration。
3. 時區以伺服器為準（見 §四）。
4. ~~清除與清空沒有保留期限的自動化~~ —— 0.9.78 起每日自動清理，0.9.96 起改由排程作業執行（§五）。

**規劃中需求**（不屬於目前驗收範圍）

- `OccurredAt` 索引 migration。
- 稽核事件的告警（例如同一帳號短時間大量 `Login.Failed`）。

## 八、測試

- `AuditLogServiceTests`、`ClientIpProviderTests`（0.9.117 起）：來源 IP 寫入、IPv4-mapped 轉換、circuit 值優先、無情境時為 null。
- `AuditLogQueryServiceTests`（28 項）：時區三條、過濾（關鍵字含來源 IP、來源 IP 前綴篩選）、排序、同秒分頁不重複、
  總筆數、下拉去重排序、清除門檻以 UTC 計算、清空。
- `AdminOnlyPermissionTests`：`角色_稽核紀錄` 不得出現在角色矩陣。
- `MenuPermissionConsistencyTests`：`AuditLogView.razor.cs` 列於 `AdminOnlyViews`，
  不得使用 `CheckAccessPage`。
- `MenuIconTests`：`fact_check` 已列入允許的 classic Material Icons。
- `DataAccessServiceLifetimeTests`：`AuditLogQueryService` 與 `AuditLogService`（0.9.131 起）必須注入 `IDbContextFactory`。
- 0.9.131 起：`AuditDiffTests`（比對、排除、遮罩、集合差異）、`AuditContentFileStoreTests`（拒絕根目錄外的路徑）、
  `AuditLogServiceTests`（內容檔、Debug 同步不含內容）、`AuditLogQueryServiceTests`（清除與清空連同內容檔）、
  `ViewAuditTests`（查詢摘要不含搜尋原文）、`CategoryServiceTests`／`MyUserServiceAssignmentTests`／`CategoryTeamRepositoryUniquenessTests`（差異內容）。
- `PageAuthorizationTests`：`/audit-logs` 自動納入受保護路由掃描。

> 返回 [PRD 主控台](README.md)
