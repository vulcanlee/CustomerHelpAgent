# 系統例外紀錄 PRD

- 文件版本：1.9
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.11
- 最後核對日期：2026/10/23

## 一、目標與範圍

記錄系統執行時拋出的例外，讓管理員在使用者回報「系統怪怪的」時，能查出**是哪個使用者、在哪一頁、做了什麼操作、屬於哪個來源**出的錯，並搭配「日誌檢視」一起診斷。

- 範圍：例外的自動捕捉管線、相同例外的合併與次數累加、堆疊全文的檔案存放、管理員專屬的檢視／查詢／刪除／清除／匯出。
- 非範圍：**不提供 Web API**（內部診斷頁）。
- 0.9.78 起：Email 告警（`ExceptionAlertSettings`，預設停用）、自動清除（`LogRetentionSettings:ExceptionLogDays`，預設 90 天）、
  錯誤追蹤碼（`LastTraceId`），見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) §6.3。
  告警通知、自動清除、錯誤追蹤碼、瀏覽器端錯誤（0.9.79）等需求（皆已實作）見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) §六。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-exceptions` | 系統管理 → 監控與診斷 → id=33「系統例外紀錄」（0.9.113 起） | **管理員專屬**（`CheckIsAdmin()`；權限鍵不上架角色矩陣） | 系統管理員 |

## 三、畫面與欄位

### 3.1 工具列

- 篩選：最後發生時間範圍（兩個 `DatePicker`）、來源下拉、使用者帳號、關鍵字（比對類型／訊息／頁面／操作）。
- 動作（`ToolbarIconButton`）：查詢 `search`、重新整理 `refresh`、複製目前查詢結果 `content_copy`（0.9.73）、匯出 CSV `file_download`、清除 N 天未再發生 `history`（N 讀 `LogRetentionSettings:ExceptionLogDays`，0.9.78 起；設 0 時退回 90）、清空全部 `delete_forever`。
- 關鍵字比對類型、訊息、頁面、操作，0.9.78 起也可輸入**錯誤追蹤碼**（完全相同才命中）。
- 刪除、清除、清空、匯出 0.9.78 起各寫一筆稽核（`ExceptionLog.Delete`／`Purge`／`ClearAll`／`Export`）。
- 破壞性動作（單列刪除、清除、清空）以 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 匯出的 CSV 為 **UTF-8 含 BOM**（0.9.16 起；先前少了 BOM，Excel 開啟繁中會亂碼），
  位元組由 `Components/Commons/TextDownloadPayload.Utf8WithBom` 產生。
- **複製目前查詢結果**（0.9.73）：與匯出同範圍（目前條件下的全部、依目前排序，最多 5,000 列，
  兩者共用 `BuildFullQuery()`），每列內容與點列複製相同、以一行 50 個 `=` 分隔；堆疊以
  `ExceptionLogService.GetStackTracesAsync` 一次查出檔名再逐檔讀取。超過 **100 列**先以
  `ConfirmDialog.AskAsync` 確認（「仍要複製」）—— 每列含完整堆疊，比日誌檢視的單行大得多，門檻因此低於其 1,000。
  查無資料時按鈕停用。

### 3.2 表格

伺服器端分頁排序，預設 **最後發生 desc**（使用者回報「剛剛怪怪的」時最需要看剛發生的；次數大的噪音不會永遠佔第一頁）。

**清單只留掃視得到的 6 個欄位**（0.9.12 起；0.9.11 曾有 9 個資料欄、固定寬度合計 1530px，
任何螢幕都會出現水平捲軸，且當時日誌樣板那一欄也叫「操作」，與動作欄同名）：

| 欄位 | 寬 | 說明 |
| --- | --- | --- |
| 最後發生 | 165 | 可排序，預設 desc |
| 次數 | 80 | 可排序；≥10 橘色、≥100 紅色 |
| 類型 | 150 | 顯示型別最後一節，滑過顯示全名 |
| 訊息 | 彈性 | `ex.Message`（最多 1000 字元） |
| 頁面 | 150 | 發生當下的路徑 |
| 使用者 | 110 | 首次遇到的帳號 |
| 操作 | 自動 | **查看**（`visibility`）＋**刪除**（`delete`），以 `<Space><SpaceItem>` 並排 |

**點列複製**（0.9.73）：`Table.OnRowClick` → 讀該列堆疊 → 以 `ExceptionLogClipboardText.Build` 組出
明細窗的 10 個欄位（**含帳號與 UserId**，與明細窗同一個 `FormatAccount`）＋以
「----- 完整堆疊開始／結束 -----」包住的堆疊，經 `appClipboard.copyText` 寫入剪貼簿，右下角提示
「已複製 1 筆例外紀錄到剪貼簿。」。只複製、不開明細窗；列游標為手指（`.exception-log-row-copyable`）。
操作欄外層 `<div @onclick:stopPropagation="true">` 擋掉冒泡，按「查看」「刪除」不會順便複製。
行尾固定 LF。與 AI 例外分析的 `AiExceptionPromptBuilder` 刻意分開：那邊送外部服務須去識別化，這邊是管理員自己貼給開發人員。

### 3.3 明細窗

點「查看」開啟，顯示清單放不下的全部欄位：完整例外類型、訊息、**來源**、頁面、
**操作（日誌訊息樣板）**、記錄器、使用者與 UserId、累計次數、**首次發生**、最後發生、
**最後追蹤碼**（0.9.78 起，最後一次發生時的錯誤追蹤碼，可到「日誌檢視」以它篩出該次操作的所有日誌），
最後接**完整堆疊**（開窗當下才讀檔；檔案不存在時顯示
「堆疊檔案不存在（可能已被清除，或當初寫檔失敗）」）。

明細窗上方有「**AI 分析**」按鈕（0.9.68 起）：把明細窗內容（**不含帳號與 UserId**，只給「有／無登入使用者」）
送給 AI 產生分析報告（管理者摘要、根本原因、修正建議、排查步驟、需要確認的問題），在疊在上面的
`.exception-ai-modal` 對話窗內顯示，可多輪追問（上限 `AiSettings:MaxFollowUpRounds`，預設 10）、
複製整段對話或下載 PDF；AI 視窗不保存報告，關窗即丟棄。堆疊讀取中或 AI 未設定時按鈕停用。
0.9.72 起每一次送出的呼叫（例外明細、堆疊與 AI 回應）另記於管理員專屬的 [AI 對話紀錄](AI對話紀錄-prd.md)，
同一窗的分析與追問共用一個 `ConversationId`，關聯說明為「例外紀錄 #id（類型）」／「例外紀錄 #id，追問第 n 輪」。
細節見 [AI 例外分析](../features/AI例外分析.md)。

⚠️ **明細窗的尺寸與內容樣式都寫在 `Components/Commons/OverlayStyles.razor` 的全域 `<style>`**。
AntDesign 的 `Modal` 會把內容渲染到元件 DOM 範圍之外，`ExceptionLogView.razor.css`
的 scoped CSS（連 `::deep`）都打不到。另注意 `.razor` 檔的 `<style>` 裡 `@media` 必須寫成 `@@media`，
否則 Razor 會當成程式碼轉換而編譯失敗。

## 四、內部系統運作

### 4.1 捕捉管線

```
logger.LogError(ex, "Failed to create category. Name={CategoryName}", name)   ← 全專案既有 catch，零修改
   → ExceptionLogProvider（ILoggerProvider）
   → ExceptionContextAccessor（AsyncLocal，讀出來源／頁面／帳號／UserId）
   → Channel（有界 1000，Wait 模式＋TryWrite：滿載即丟棄並計數）
   → ExceptionLogWriter（BackgroundService，單一消費者；關機時清空佇列，上限 5 秒）
   → ExceptionLogService → SQLite ＋ ExceptionStackFileStore → 檔案系統
```

**為什麼掛在 `ILogger` 管線**：全專案 75 個 `catch (Exception ex)` 多數在服務層記完 `LogError` 後就回傳 `VerifyRecordResult`，**例外不再往上拋**。只掛未處理例外（error boundary、API filter）會完全收不到它們，而那正是「系統怪怪的」的主要來源。

**為什麼要背景寫入**：大語言模型主機逾時會在短時間內重複數百次，同步寫 DB 會拖垮正在等待的使用者。logger 只入列即返回；單一消費者也順帶避開並發 upsert 競爭。

**0.9.77 的管線修正**（見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) §6.2）：
- ⚠️ Channel 必須是 `Wait` 模式：`DropWrite` 下 `TryWrite` 永遠回 true，丟棄筆數永遠是 0（LOG-02）。
- 同一個例外實例只收一次（`ConditionalWeakTable`）：請求日誌與框架 ExceptionHandlerMiddleware 各記一次的情況不再變成兩列（LOG-03）。
- 寫入器收到停止訊號後清空佇列，上限 5 秒，逾時剩餘筆數輸出到 InternalLogger（LOG-05）。

### 4.1.1 補登檔（0.9.77）

程序即將結束、背景寫入器來不及寫資料庫的例外 —— 啟動失敗（寫入器要到 `app.Run()` 才啟動）與
`AppDomain.UnhandledException` —— 先由 `CrashMarkerStore` 寫成 `{ExceptionPath}/pending/*.json`，
下次成功啟動時在遷移之後補進本表，然後刪檔（LOG-06）。記錄當下的 `LogCritical` 在抑制旗標下執行，
避免「佇列剛好寫進去＋補登」變成兩列。`CrashMarkerStore` 位於記錄機制之外，**不得注入 `ILogger`**。
「清空全部」會連同 `pending/` 一起刪除；補登檔在啟動時即匯入，平常是空的。

### 4.2 收錄條件

**收**：`logLevel >= Error` 且 `exception != null`。

**不收**：
- `OperationCanceledException`／`TaskCanceledException` —— 使用者取消、正常關機。
- 本子系統自己的記錄器（`ExceptionLogProvider`／`ExceptionLogWriter`／`ExceptionLogService`／`ExceptionStackFileStore`）。
- `AsyncLocal` 抑制旗標開啟時（寫入器執行期間全程開啟）。
- 同一個例外實例的第二次以後（0.9.77）。

> `LogWarning(ex, …)` 多半是「已處理、降級可用」，收進來只是噪音，故以 Error 為門檻（程式常數）。

### 4.3 情境資訊來源

| 路徑 | 設定位置 | Source |
| --- | --- | --- |
| Blazor 互動 | `ApplicationCircuitHandler.CreateInboundActivityHandler` | `畫面` |
| HTTP／API | `ApplicationBuilderExtensions.UseHttpRequestLogging`；帳號由 `UseExceptionContextUser` 在 `UseAuthorization` 之後補上（0.9.77 前恆為空） | `/api` 開頭→`WebAPI`，否則`畫面` |
| 系統啟動 | `Program.cs` 的 migrate／seed／RBAC 回填區段；啟動失敗經補登檔補進 | `系統啟動` |
| 程序層級 | `ProcessExceptionHooks`：射後不理的 Task（`UnobservedTaskException`）、其他執行緒的未處理例外（0.9.77） | `系統` |
| 瀏覽器 | `BrowserErrorReporter`：登入後頁面的 JavaScript 錯誤經 circuit 回報（0.9.79），例外型別 `BrowserScriptException`，堆疊是瀏覽器端的呼叫路徑；每個 circuit 每分鐘最多 10 筆 | `瀏覽器` |
| 其他 | 未設定時 | `未知` |

⚠️ `CreateInboundActivityHandler` 是**全站每一次互動都會經過**的路徑，內部全程 `try/catch`，設定情境失敗絕不影響使用者操作。

### 4.4 合併規則

合併鍵＝**例外類型 ＋ 訊息 ＋ 頁面 ＋ 操作**，取 SHA-256 存入 `Signature`（唯一索引）。
相同簽章只累加 `OccurrenceCount` 並更新 `LastOccurredAt`，`FirstOccurredAt` 保持不變。

⚠️ **「操作」必須是日誌訊息的「樣板」**（`{OriginalFormat}`，例如 `Failed to create category. Name={CategoryName}`），
**不是**算好的訊息（`Name=技術文件`）。取錯會讓每個參數值都變成一個新簽章，列數立刻失控 ——
而這個症狀要等正式環境累積一陣子才看得出來。由 `ExceptionSignatureTests` 與 `ExceptionLogProviderTests` 守門。

### 4.5 堆疊檔案

- 路徑：`SystemSettings.ExternalFileSystem.ExceptionPath`（預設 `C:\temp\CustomerHelpAgent\Exception`）
- 檔名：`{yyyyMM}/{簽章前 16 碼}.txt`（年月目錄，比照專案附件慣例）
- **只在首次建立資料列時寫一次**，重複發生不重寫 → 寫檔成本與發生次數無關
- 寫檔失敗不影響資料列建立（`StackTraceFile` 留 `null`）
- ⚠️ **生命週期集中在 `ExceptionStackFileStore` 一處**：刪單列、清空全部、清除舊紀錄三條路徑都經過它；
  資料列建立失敗時也會把剛寫的檔刪掉，避免孤兒檔

### 4.6 列數上限

`ExceptionLogService.MaxRows = 5000`。達上限後不再新增相異列，改以哨兵簽章 `__OVERFLOW__`
累加一列「其他例外（已達列數上限）」，避免帶參數的例外訊息塞爆資料庫。

## 五、權限與安全

- **管理員專屬**：檢視以 `CheckIsAdmin()` 守門，`角色_系統例外紀錄` **刻意不列入**
  `RolePermissionService.GetRoleListPermissionAllName()` —— 不種 `Permission` 資料列、角色矩陣不顯示、
  任何角色都無法被授予。由 `AdminOnlyPermissionTests` 守門，**請勿補上**。
- 例外訊息可能含使用者輸入，**刻意不做遮罩**：本頁與「日誌檢視」同級（皆管理員專屬、皆看得到原始內容）。
- 記錄的身分只有 `Account` 與 `UserId`，**不記姓名／Email**，與全站日誌規範一致。
- **AI 分析送往外部 AI 的內容不含 `Account`／`UserId`**（0.9.68 起），由 `AiExceptionPromptBuilderTests` 以哨兵字串守門；
  稽核（`ExceptionLog.AiAnalyze`／`AiFollowUp`／`AiExportPdf`）只記數量、模型、用量與耗時，不記內容。

## 六、錯誤與邊界

- 佇列滿載：丟棄並累加計數（0.9.77 前計數永遠是 0），停止時由 `InternalLogger` 輸出總丟棄數。**寧可漏記，不可拖垮主流程**
  （與 `nlog.config` 的 `AsyncWrapper overflowAction="Discard"` 同一種取捨）。
- 寫入失敗：`RecordAsync` 全程吞例外，且**不得經由 `ILogger` 回報**（會遞迴），改用 `NLog.Common.InternalLogger`。
- 堆疊檔讀不到：UI 友善降級，不拋例外。
- 本頁只收「程式碼有記錄下來的」例外；完整逐行日誌仍由「日誌檢視」取得。

## 七、驗收與測試

| 測試 | 驗什麼 |
| --- | --- |
| `ExceptionSignatureTests` | 合併鍵組成；**樣板與算好的訊息必須產生不同簽章** |
| `ExceptionLogServiceTests` | 合併累加、堆疊只寫一次、刪列同時刪檔、清空、`PurgeAsync`、5000 列上限、`RecordAsync` 絕不拋出、`GetStackTracesAsync` 批次讀堆疊（缺檔與未知 id 回 null） |
| `ExceptionLogProviderTests` | Warning／無例外／取消例外／自身記錄器／抑制旗標皆不收；佇列滿載丟棄不拋例外且計數增加；同一例外實例只收一次（0.9.77） |
| `ExceptionLogWriterTests` | 停止時清空佇列；清空逾時即放棄（0.9.77） |
| `CrashMarkerStoreTests`／`ProcessExceptionHooksTests` | 補登檔讀寫刪、壞檔略過、路徑未設定不拋；程序層級例外的來源、抑制與情境還原（0.9.77） |
| `ApiIntegrationTests` | API 例外帶得出帳號；非 API 頁面例外只記一列（0.9.77） |
| `DataAccessServiceLifetimeTests` | `ExceptionLogService` 注入 `IDbContextFactory` 而非 scoped context |
| `AdminOnlyPermissionTests` | 權限鍵不在角色矩陣 |
| `MenuPermissionConsistencyTests` | `AdminOnlyViews` 含 `ExceptionLogView.razor.cs` |
| `MenuIconTests` | `bug_report` 在允許清單 |
| `LoggingConventionTests` | 管線內四支類別與 `CrashMarkerStore` 列於 ILogger 豁免清單（**刻意不注入，請勿補上**） |
| `ExceptionLogClipboardTextTests` | 點列／批次複製文字：欄位順序、空值「—」、帳號＋UserId、堆疊標記與缺檔說明、LF 行尾、分隔線（0.9.73） |
| `AiExceptionPromptBuilderTests`／`AiExceptionAnalysisServiceTests`／`AiExceptionReportPdfBuilderTests` | AI 例外分析（0.9.68）：送出內容不含帳號、追問上限不發請求、PDF 產生 |

手動驗收（0.9.11 實跑結果）：

- 以 `support` 登入 → 系統管理 →「系統例外紀錄」，圖示正常、頁面可開、無 console 錯誤。
- 人為造成專案附件寫檔失敗 → 出現一列：來源 `畫面`、頁面 `/projects`、
  操作 `Failed to save project files. ProjectId={ProjectId}`、使用者 `support`、次數 1。
- 重複兩次 → 仍一列、次數 3、`FirstOccurredAt` 不變、**堆疊檔修改時間不變**。
- 點「查看」可見完整堆疊（含原始碼行號）；Esc 可關窗；刪除該列 → 檔案一併消失。
- 清單在 1600px 與 1366px 兩種寬度下皆 `scrollWidth === clientWidth`，無水平捲軸（0.9.12）。
- 非管理員 → 顯示「你沒有權限存取此頁面」，選單亦不顯示本頁。
- 全站 11 頁逐頁回歸（因動到 `ApplicationCircuitHandler`）：全部正常、零 console 錯誤。
- 400px 窄螢幕：工具列換行、表格內部可水平捲動、頁面無水平捲軸。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/ExceptionLog.cs`、`Migrations/*_AddExceptionLog.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/ExceptionSignature.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ExceptionLogService.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/ExceptionStackFileStore.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Models/Systems/ExceptionLogEntry.cs`、`ExceptionLogQuery.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Diagnostics/ExceptionContextAccessor.cs`、`ExceptionLogProvider.cs`、`ExceptionLogWriter.cs`、`CrashMarkerStore.cs`、`ProcessExceptionHooks.cs`（後兩者 0.9.77）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Admins/ExceptionLogPage.razor`、`Components/Views/Admins/ExceptionLogView.razor`、`ExceptionLogClipboardText.cs`（複製文字，0.9.73）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/ApplicationCircuitHandler.cs`（情境設定）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Admins/ExceptionAiAnalysisModal.razor`、`CustomerHelpAgent.Web/Ai/AiExceptionAnalysisService.cs`（AI 例外分析，0.9.68）
- 交叉連結：[AI 例外分析](../features/AI例外分析.md)、[日誌檢視 PRD](日誌檢視-prd.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)
