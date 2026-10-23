# 系統備份與還原 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.99
- 最後核對日期：2026/10/23

## 一、目標與範圍

讓系統每天自動產生一份**可以還原**的完整備份，管理員可隨時立即備份、下載做異地保存；還原由部署人員停站後以腳本執行。

- **範圍**：備份內容與格式（zip＋manifest）、排程作業 `SystemBackup`、依份數自動刪除、管理頁 `/backups`（立即備份、清單、下載、刪除）、下載端點、還原腳本 `scripts/Restore-Backup.ps1`、備份目錄的啟動驗證。
- **非範圍**：線上還原（網站執行中替換資料庫檔不安全）；備份加密；備份到雲端或遠端主機（由維運流程把下載的檔案搬走）；日誌檔與網站程式本身；`DownloadPath`／`UploadPath`（腳手架不寫入要長期保存的檔案，附件一律放 `ProjectFilePath`）。各項不備份的理由見[備份與還原操作手冊](../operations/備份與還原操作手冊.md) §1。

使用者決定（2026/10/04）：備份資料庫、專案附件、例外堆疊檔、Token 原始檔與金鑰環，預設不含 AI 對話內容；不加密；新增 `BackupPath`、管理員可下載；每天 02:00、保留 7 份。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/backups` | 系統管理 → 系統設定與維運 → 系統備份（`Menu.json` id=37）| 僅管理員（`CheckIsAdmin`；權限鍵 `角色_系統備份` 不進角色矩陣）| 系統管理員 |
| `GET /api/backups/{檔名}/download` | — | Cookie 驗證＋從資料庫確認是管理員 | 系統管理員 |

## 三、畫面與欄位

- 工具列：立即備份（確認後放進排程框架的佇列，與排程共用作業鎖；執行中按鈕停用、每 5 秒自動更新）、重新整理。
- 摘要：下次自動備份、上次執行（結果、時間、訊息）、保留份數。
- 表格（新到舊）：檔名、建立時間、大小、系統版本、觸發（排程／補跑／手動）、內容（各資料夾檔案數、打包時已消失的檔案數）；每列「下載」「刪除」（危險確認）。

## 四、內部系統運作

- **備份**（`Web/Backup/SystemBackupService`）：
  1. 清掉上一次中斷留下的 `.work/` 與 `*.partial`（呼叫端持有作業鎖）。
  2. 估算大小（資料庫＋`-wal`＋各資料夾，×2）並檢查備份目錄的可用空間。
  3. 資料庫以 `VACUUM INTO` 寫到 `.work/<guid>/BackendDB.db`：不經連線池、不設逾時、唯讀連線；WAL 模式下取得一致快照且不擋寫入。
  4. `PRAGMA quick_check` 不是 ok 就不產出備份；讀最後一筆 migration 與數量、計算 SHA-256。
  5. 依序打包 `db/BackendDB.db`、`files/ProjectFile`、`files/Exception`、`files/TokenUsage`、`files/Keys`（`IncludeAiCallLogs` 時加 `files/AiCallLog`）、最後 `manifest.json`，寫到 `*.zip.partial`。
     先資料庫、後檔案；來源檔打不開（被刪或被獨佔）只計數，不中止；檔與檔之間檢查取消。
  6. 原子改名成 `backup-yyyyMMdd-HHmmss[-n].zip`；`finally` 刪掉暫存與 partial。
- **作業** `SystemBackupJob`（`0 2 * * *`）：成功後寫 `Backup.Create`，再依 `KeepCount` 刪舊備份（寫 `Backup.AutoPurge`）；失敗回 `Failure` 且**不刪舊備份**；取消時記為中斷。
- **清單** `BackupStore`：只認根目錄下符合檔名規則的 zip；排序以「時間戳＋同秒序號」比較；下載與刪除都先經檔名與根目錄檢查。清單只存在檔案系統（放資料庫的話還原時會倒回去）。
- **manifest.json**：`formatVersion`、`createdAtUtc`、`systemVersion`、`lastMigration`、`migrationCount`、`databaseEntry`、`databaseSha256`、`folders[]`（zip 資料夾 → `ExternalFileSystem` 設定鍵名與檔案數）、`missingFiles`、`missingFileCount`、`trigger`。
- **還原腳本**：確認網站已停（獨佔開啟資料庫）→ 解壓並核對格式版本與雜湊 → 備份版本比部署的新時中止（`-Force` 略過）→ 現有資料庫與 `-wal`、`-shm` 改名保留 → 刪鎖檔 → 放入資料庫 → 各資料夾換成備份內容（舊的改名保留）、金鑰合併不覆蓋 → 印出回復步驟。腳本不寫死專案名稱（`New-StarterProject.ps1` 會文字替換）。
- **設定**：`ExternalFileSystem:BackupPath`（啟動驗證：完整路徑、不與其他 8 個目錄重疊、不在網站目錄底下）、`BackupSettings { KeepCount = 7, IncludeAiCallLogs = false }`（系統參數「備份」分類）。

## 五、權限與安全

- 管理頁與下載都只限管理員；下載端點每次從資料庫確認，不靠 Cookie 宣告。
- ⚠️ 備份不加密且含金鑰環，拿到的人可以偽造登入 Cookie；文件要求備份目錄權限只給應用程式集區身分與管理員、下載後妥善保管並做異地保存。
- 稽核：`Backup.Create`、`Backup.AutoPurge`、`Backup.Download`（續傳的後續區段不重複記）、`Backup.Delete`；立即備份另記 `Job.Trigger`。

## 六、錯誤與邊界

- 可用空間不足、快照完整性檢查失敗 → 作業回 Failure，訊息說明原因，不產出備份、不刪舊備份。
- 備份途中網站關閉 → 中斷；暫存在 `finally` 清掉，被強制結束時由下一次備份清。
- 打包時檔案被刪除或被獨佔 → 只計數並列在 manifest。
- 03:00 的清理作業若與仍在執行的備份重疊，被刪掉的檔案會記為已消失；建議 IIS 回收時間避開 02:00。
- 衍生專案重新產生過 migration，不能用腳手架（或其他專案）的備份還原；金鑰在 Windows 上以 DPAPI 保護，換主機或換應用程式集區身分時可能解不開。

## 七、驗收與測試

- `SystemBackupTests`：寫入中的 WAL 資料庫快照通過 quick_check 且資料完整、manifest 正確、資料夾內容（含子目錄）、AI 對話預設不含／開啟才含、打不開的檔案只計數、取消與失敗不留殘檔、空間不足先失敗、上次殘檔被清掉、檔名與根目錄檢查、份數刪除（同秒序號）、作業失敗不刪舊備份、備份目錄位置驗證（8 個目錄雙向、大小寫、前綴相近、網站目錄）、續傳只記一次稽核。
- `BackupDownloadTests`：匿名被擋、非管理員 403、管理員 200 與續傳 206、稽核一筆、路徑穿越 404。
- 守門同步：`ScheduledJobWiringTests`、`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`SystemParameterTests`、`PageHelpCatalogTests`、`DocumentationConventionTests`。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Backup/`（`SystemBackupService`、`BackupStore`、`BackupManifest`）、`Scheduling/Jobs/SystemBackupJob.cs`、`Controllers/BackupController.cs`、`Configuration/BackupSettings.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Admins/BackupView.razor(.cs)`、`Datas/Help/backups.md`
- `scripts/Restore-Backup.ps1`；操作：[備份與還原操作手冊](../operations/備份與還原操作手冊.md)；規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「系統備份」
