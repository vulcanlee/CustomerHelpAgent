# 資料庫用量 PRD

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.4.28
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供管理員一個頁面（`/database-usage`）檢視 SQLite 資料庫的實際磁碟占用、頁面配置狀況，以及各資料表的用量分佈。

在此頁面之前，`/system-health` 只回報「可否連線」與「待套用 migration 數」，看不出檔案有多大、WAL 累積了多少、有多少空間可回收，也看不出哪張表最占空間 —— 要判斷這些只能登入伺服器翻檔案。

- 範圍：六張用量卡片、WAL 說明橫幅、估算免責說明、各資料表的筆數／資料量估算／索引數表格。
- 非範圍：任何會寫入資料庫的操作（VACUUM、WAL checkpoint）、歷史趨勢、容量告警、自動清理。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/database-usage` | 系統管理 › 監控與診斷 › 資料庫用量 | 已登入且為管理員（`IsAdmin`） | 系統管理員／維運 |

權限採**管理員專屬**設計，與「日誌檢視」相同：`MagicObjectHelper.角色_資料庫用量` 有定義並登記於 `SidebarMenuService.MenuPermissionMap`（id 62），但**刻意未列入** `RolePermissionService.GetRoleListPermissionAllName()`。因此不會種出 `Permission` 資料列、角色矩陣不顯示、任何角色都無法被授予，只有管理員短路能通過。`CustomerHelpAgent.Tests/AdminOnlyPermissionTests.cs` 會擋下日後「順手補齊」的修改。

## 三、畫面與欄位

**六張用量卡片**

| 卡片 | 值 | 副標 |
| --- | --- | --- |
| 磁碟占用總計 | 三個檔案加總 | 主檔 + WAL + SHM 三個檔案 |
| 主資料庫檔 | `.db` 檔大小 | BackendDB.db |
| 預寫日誌 | `-wal` 檔大小 | BackendDB.db-wal（不存在時顯示「目前無此檔案（已 checkpoint）」） |
| 共享記憶體索引 | `-shm` 檔大小 | BackendDB.db-shm |
| 已配置 | `page_count × page_size` | `{N} 頁 × {size} B，含尚未併回主檔的頁` |
| 可回收 | `freelist_count × page_size` | `{N} 個閒置頁，VACUUM 可釋放` |

**說明橫幅**：解釋 WAL 比主檔大是正常現象（尚未 checkpoint 的交易紀錄），不代表資料量暴漲。

**免責說明**：「資料量估算」不是磁碟占用量，它是各欄位內容的位元組加總，不含索引、頁面碎片與已配置的閒置頁。

**資料表表格**：資料表｜紀錄筆數｜資料量估算｜索引數，四欄皆可排序，預設依表名遞增。**包含系統表** —— `__EFMigrationsHistory`、`__EFMigrationsLock`、`sqlite_sequence` 同樣占用頁面，不列出會讓加總對不上。0.9.41 時共 14 張；現行模型有 24 張實體資料表，加上述 3 張系統表共 27 張。

**本頁沒有任何操作按鈕**，純閱讀，不提供會寫入資料庫的動作。

> **降級狀態**：報表尚未載入時顯示「正在讀取資料庫用量...」；
> `report.ErrorMessage` 非空時顯示紅色錯誤區塊；另有 `report.Message` / `report.Warnings`
> 以黃色提示區塊呈現（例如 WAL 模式下的估算誤差）。

## 四、內部系統運作

1. `DatabaseUsageView.OnInitializedAsync`：先 `AuthenticationStateHelper.Check`，再 `CheckIsAdmin`；非管理員設定 `RoleMessage`、寫一筆 `Permission.Denied` 稽核（目標 `/database-usage`）並中止，**權限通過前不觸碰資料庫**。
2. `IDatabaseUsageService.GetReportAsync` 透過 `context.Database.OpenConnectionAsync()` 取得連線後：
   - 由**已開啟連線的 `DataSource`**（即 `sqlite3_db_filename` 解析出的絕對路徑）定位三個檔案並讀取大小。不從組態推導，確保量到的必定是 EF 實際在用的檔案。
   - `PRAGMA page_size` / `page_count` / `freelist_count` 取得頁面統計。
   - `sqlite_master` 列出所有 `type = 'table'` 的資料表，並以單一 `GROUP BY` 查詢一次取回全部索引數。
   - 逐表以 `PRAGMA table_info` 取欄位，組出位元組加總查詢，**一次掃描同時取回筆數與加總**。
3. 完成後記錄耗時、掃描表數與磁碟總量至日誌。

## 五、限制與已知取捨

- **`LENGTH` 的位元組陷阱**：SQLite 的 `LENGTH(文字欄)` 回傳的是**字元數不是位元組數**，中文欄位會少算到三分之一（實測 `'中文測試'` → `LENGTH` 為 4、轉 BLOB 後為 12）。實作一律使用 `LENGTH(CAST(欄位 AS BLOB))`，並由 `DatabaseUsageServiceTests.Estimate_ChineseText_ShouldCountBytesNotCharacters` 守住。
- **NULL 欄位必須逐欄 `COALESCE`**：`NULL + 任何值` 仍是 `NULL`，而 `SUM()` 會跳過 `NULL` 列 —— 少了逐欄處理會「悄悄少算」而不是報錯。
- **數值欄位的估算不精確**：INTEGER／REAL 轉 BLOB 會先經過文字表示，量到的是位數而非實際儲存的 1～8 位元組 varint。這本來就是估算值。不應改用 `CASE typeof(...)` 硬套固定長度 —— 那同樣是猜測，只會讓 SQL 產生邏輯更複雜。
- **沒有 dbstat**：本專案綁的 SQLite（3.49.1）未編入 `dbstat` 虛擬表（已實測確認回傳 `no such table: dbstat`），而那是取得每表實際頁數的唯一途徑。`sqlite_dbpage` 同樣不可用。
- **位元組估算是每張表全表掃描**，成本與資料庫總大小成正比且無索引可用。目前資料庫為數百 KB 等級 故可忽略；服務會記錄每次量測耗時，日後真的變慢才有數據可依據。
- **`Microsoft.Data.Sqlite` 的 async 是假的**：`ExecuteScalarAsync` 等方法是同步工作包在已完成的 Task 裡。因此 `CancellationToken` 無法中止已在進行的掃描，以逾時來設限並不會真的生效。
- **已配置可能大於主檔大小**：`page_count` 回報的是「此連線所見的資料庫大小」，包含仍只存在於 WAL、尚未 checkpoint 回主檔的頁。這正是副標「含尚未併回主檔的頁」的意思，不是錯誤。
- **路由守衛只到「已登入」**：0.9.41 起本頁繼承 `Pages/_Imports.razor` 的 `[Authorize]`，未登入者在 HTTP 層就被導去登入頁；「管理員專屬」仍由 `OnInitializedAsync` 內的 `CheckIsAdmin()` 判斷，判定完成前不渲染任何內容，與 `/system-health`、`/logs` 一致。
- 索引數計入具名索引與 `UNIQUE` 隱含產生的 `sqlite_autoindex_*`；不計 rowid B-tree 與 `INTEGER PRIMARY KEY`（前者就是資料表本身，後者是 rowid 別名，皆不額外占空間）。

## 六、驗收與測試

對應測試檔 `CustomerHelpAgent.Tests/DatabaseUsageServiceTests.cs`（10 支）：涵蓋各資料表筆數統計、
用量估算與單位換算，以及 `Estimate_ChineseText_ShouldCountBytesNotCharacters`
（中文須以**位元組**而非字元計算，否則估算會嚴重偏低）。

---

## 七、相關文件

- 選單與權限機制：`docs/prd/首頁與導覽-prd.md`、`docs/security/認證授權與權限機制.md`
- 同一子功能表的另一頁：`docs/prd/日誌檢視-prd.md`
- 健康監控頁的資料庫檢查：`docs/prd/系統健康監控-prd.md`

> 返回 [prd 索引](README.md)
