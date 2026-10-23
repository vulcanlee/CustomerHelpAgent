# 資料保留與清理 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.133
- 最後核對日期：2026/10/21

## 一、目標與範圍

讓管理員在**一頁**看到系統裡每一種會自動刪除的資料：現在多少、佔多少空間、最舊到何時、保留規則、下一次自動清理會刪掉多少，以及負責的排程；需要時立即清理，或跳到可以細部刪除的頁面。

- **範圍**：管理頁 `/data-retention`（唯讀總覽＋「立即清理」＋「前往」＋「修改」連結）；各清理服務新增與清理共用條件的統計方法；`?deleted=1`（五個軟刪除頁直接顯示已刪除）與系統參數頁 `?group=`（捲到分類）。
- **非範圍**：在這一頁修改保留天數（只在系統參數頁）；在這一頁挑選個別紀錄刪除（在各頁）；修改清理排程時間（設定檔）；日誌檔與公告的清理（日誌由 NLog 處理，公告不自動刪除，只顯示資訊）；Web API。

0.9.134 起加上「自訂清除」：使用者自己選「只留最近 N 天」或「刪除某一天之前」，預覽後確認刪除，不改系統參數。

使用者決定（2026/10/21）：新增一頁、不擴充排程作業頁；每種資料一列，全部列出（已刪除資料分 5 列，共用一個排程）；顯示筆數、檔案空間、最舊一筆、下次會清掉幾筆；「立即清理」＋「前往」，系統備份只有「前往」；保留天數只顯示，連到系統參數修改。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/data-retention` | 系統管理 → 系統設定與維運 → 資料保留與清理（`Menu.json` id=68）| 僅管理員（`CheckIsAdmin`；權限鍵 `角色_資料保留與清理` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- 工具列：重新整理。排程總開關關閉、排程設定有誤時顯示警示。
- 表格（固定 14 列，依序）：已刪除的專案／分類／團隊／使用者／角色、系統例外紀錄、Token 用量、AI 對話紀錄、稽核紀錄、站內通知、系統備份、排程執行紀錄、日誌檔、公告。
- 欄位：資料（附一併處理的檔案）、目前數量、檔案空間、最舊一筆、保留規則（＋「修改」→ `/system-parameters?group=資料保留|備份`）、下次會清掉、自動清理（白話頻率、停用標籤、上次時間與結果訊息、下次執行；已刪除的後四列只註明與專案同一個排程）、操作（「立即清理」「自訂清除」「前往」，0.9.134 起固定在右側）。
- 自訂清除窗（0.9.134 起，適用例外、Token、AI 對話、稽核、站內通知、排程執行紀錄與已刪除的五種；備份、日誌檔、公告沒有）：二選一「只留最近 N 天」（1～3650）／「刪除這一天之前（不含當天，不可晚於今天）」，即時預覽會刪幾筆；「清除」→ 紅色確認 → 在頁面上同步刪除 → 通知刪了幾筆（已刪除資料附留下幾筆）。
- 一列讀取失敗只在那一列顯示「讀取失敗：例外類型」，其他列照常。

## 四、內部系統運作

- **彙總** `Web/DataRetention/DataRetentionOverviewService`（scoped）：組合 `ScheduledJobOverviewService`、各資料服務的 `GetRetentionStatsAsync`、`BackupStore`、`INLogFilePathResolver` 與各保留設定（`IOptionsMonitor<T>.CurrentValue`，含系統參數覆寫）；每列各自 try/catch。
- **與清理同一個條件**：各服務把清理的 Where 抽成 `private static Expression<Func<T,bool>>`，清理與統計共用；門檻由 `Scheduling/RetentionCutoffs`（本地或 UTC 依資料表）或服務的 `ExpiryThreshold`（Token、AI 對話：當天 0 點往前）計算，清理作業同步改用。
  - 站內通知兩條規則取 Id 聯集，不重複計。
  - 已刪除資料的數字是「最多」：support、預設角色、仍在使用的角色、還有下屬的部門會被清理留到下次。
  - 系統備份：`max(0, 份數 + 1 − KeepCount)`（下一次備份成功後刪除）。日誌檔：最後修改超過 `NLogRetention.MaxArchiveDays`（30，對照 nlog.config）的檔案數，約略值。
- **檔案空間** `DirectorySizeProbe`：遞迴加總，略過無權限與 reparse point；各資料夾在背景並行、整體 10 秒上限，超過或出錯顯示「至少 X」；已刪除專案的附件大小由資料庫 `ProjectFile.FileSize` 加總。
- **立即清理**：先重新計算一次（確認窗用最新數字）→ 紅色確認窗 → `ScheduledJobOverviewService.TryTrigger`（與排程作業頁「立即執行」同一個佇列與作業鎖）→ 稽核。作業停用或總開關關閉時仍可執行一次；保留規則為 0、已在佇列或執行中、找不到作業時停用按鈕並在提示說明原因。進行中每 5 秒自動更新。
- **自訂清除**：`CustomPurgeCutoff` 依 `RetentionTimeBasis` 換門檻（例外、已刪除資料用本地；稽核、通知、執行紀錄用 UTC；Token、AI 對話以日計），「刪除某天之前」是那天本地 0 點。預覽用各服務的 `GetRetentionStatsAsync(門檻)`，刪除用同一個條件的明確門檻方法；已刪除資料經 `SoftDeletePurgeService.PurgeExpiredTypeAsync` 只清一種、保護規則不變；站內通知只依建立時間。Token 用量分批刪除（`PurgeBeforeBatchedAsync`，與自動過期共用）。
- **白話頻率** `Scheduling/CronDescriber`：每天／每週X／每月 d 日／每小時第 m 分，其他寫法原樣顯示 cron。

## 五、權限與安全

- 只限管理員；畫面不顯示任何紀錄內容，只有數量與時間。
- 稽核：「立即清理」寫 `Job.Trigger`（detail `job=…; source=data-retention; expected=N`），清理本身仍由各作業寫 `*.AutoPurge`。「自訂清除」有刪到資料時寫各自的 `*.Purge`（例外、Token、AI 對話、稽核、五種已刪除資料）、`Notification.Purge`、`Job.PurgeRuns`，detail `source=data-retention; mode=keepDays|beforeDate; value=…; rows=N`。

## 六、錯誤與邊界

- 設定驗證失敗（`OptionsValidationException`）：只影響讀那個設定的列，顯示「讀取失敗」；排程設定錯誤沿用排程作業頁的警示。
- 資料夾未設定顯示「未設定」、還不存在顯示「0 B」。
- 按下時排程剛好開始執行或規則剛被改成 0：提示「現在不能清理」，不放進佇列。

## 七、驗收與測試

- 統計與清理一致：`RetentionJobTests`（例外、稽核、Token、AI 對話，資料放在本地與 UTC 相差的 8 小時內）、`SoftDeletePurgeTests`（五種、附件大小）、`NotificationTests`（兩條規則不重複計）、`ScheduledJobFrameworkTests`（執行中的不算）。
- `CustomPurgeCutoffTests`（驗證、三種時間基準的換算）、`SoftDeletePurgeTests`（`PurgeExpiredTypeAsync` 只清一種、保護規則）。
- `DataRetentionOverviewServiceTests`（真正的主機：自訂清除對 8 種資料「預覽＝實際刪除」、已刪除資料只清那一種、備份／日誌／公告沒有自訂清除；14 列順序、沒有讀取失敗、負責作業、備份與沒有作業的列沒有「立即清理」、五個頁面支援 `?deleted=1`、確認窗文字）、`CronDescriberTests`、`DirectorySizeProbeTests`、`NLogRetentionTests`。
- 守門同步：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`PageAuthorizationTests`、`LoggingConventionTests`。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/DataRetention/`（`DataRetentionOverviewService`、`DirectorySizeProbe`）、`Scheduling/RetentionCutoffs.cs`、`Scheduling/CronDescriber.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/RetentionStats.cs` 與各服務的 `GetRetentionStatsAsync`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Admins/DataRetentionView.razor(.cs)`、`Datas/Help/data-retention.md`
- 規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「資料保留與清理」；相關 PRD：[排程作業](排程作業-prd.md)、[系統參數](系統參數-prd.md)、[系統備份與還原](系統備份與還原-prd.md)
