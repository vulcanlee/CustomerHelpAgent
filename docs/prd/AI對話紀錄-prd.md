# AI 對話紀錄 PRD

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.72
- 最後核對日期：2026/10/23

## 一、目標與範圍

讓管理員能回頭檢視**每一次呼叫 AI API 時實際送出的 Prompt 與取得的 Response**，並知道那次呼叫屬於哪個**作業**、由哪位**使用者**發起，
用來排查 AI 結果不如預期、失敗或花費異常的原因，而不必重現一次。本功能複刻自 ReviewSkills.AI 1.0.34 的同名頁，UI 改用本專案的設計規範。

- 範圍：
  - 全部 AI 呼叫點（3 種作業，見 §三）的完整請求與回應記錄。內文寫在檔案系統，SQLite 只存可篩選的索引。
  - 管理員專屬頁面 `/ai-call-logs`：篩選、清單、五種檢視的明細視窗、JSON 下載、PDF 匯出、CSV（僅中繼資料）。
  - 單筆刪除、清除此日之前、清空全部，以及依保留天數自動過期。
  - 與 [Token 用量](Token用量-prd.md) 以呼叫識別碼（CallId）雙向連結。
- 非範圍：
  - 不提供 Web API（與同群組的 Token 用量、日誌檢視等管理員頁面一致，只有 Blazor 畫面）。
  - 不做內文全文搜尋。
  - 不能重送或修改對話。
  - 不提供多筆合併 PDF。
  - 不做「一般使用者只看自己的」分權。
  - 不支援串流（SSE）原文：本專案目前沒有串流呼叫點，ReviewSkills 的 SSE 相關欄位與檢視刻意不移植。

> ⚠️ **這是對既有紅線的正式修訂。**0.9.71 之前系統承諾「絕不保存提示詞與模型回應內文」。
> 0.9.72 起修訂為：`TokenUsageLog`／`TokenUsagePath` 仍然絕不存內文；完整內文**只准**存進本功能的專屬儲存區，
> 僅管理員可看，並依保留天數自動過期。其他資料表、NLog 日誌與稽核紀錄仍不得寫入內文。
> 規則的權威來源見 [開發慣例與限制速查 §6.7](../architecture/開發慣例與限制速查.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 |
| --- | --- | --- |
| `/ai-call-logs` | 系統管理 → AI 管理 → id=66「AI 對話紀錄」（AI 提示詞與 Token 用量之間，圖示 `forum`；0.9.113 前在「統計與分析」）| **管理員專屬**（`CheckIsAdmin()`；權限鍵 `角色_AI對話紀錄` 不上架角色矩陣）|
| `/ai-call-logs?callId={Guid}` | Token 用量明細窗「AI 對話紀錄」列的查看對話按鈕 | 同上；載入後自動開啟該筆明細，找不到時提示原因 |

隱私揭露：在操作說明（日誌檢視、系統例外紀錄、系統健康監控、Token 用量、本頁）中註明
「AI 呼叫內容會被記錄，管理員可查閱」，畫面不另加提示；本頁上方的說明框另標示內容敏感與保留天數。

## 三、記錄什麼

### 3.1 記錄點

| 作業（`TokenUsageOperations`）| 呼叫點 | 身分來源 | 關聯說明（RelatedInfo）|
|---|---|---|---|
| AI 日誌分析 | `AiChatCompletionClient`（經 `AiLogAnalysisService`）| `CurrentUserService` | 「日誌 起～迄，送出 n／N 筆」（實際送出的最新 n 筆）|
| AI 例外分析 | `AiChatCompletionClient`（經 `AiExceptionAnalysisService`）| 同上 | 第一次：「例外紀錄 #id（例外類型）」；追問：「例外紀錄 #id，追問第 n 輪」|
| 系統健康檢測 | `AiHealthProbe` | 同上 | 「系統健康監控 AI 連線探測」|

AI 例外分析與它的每一輪追問共用同一個 `ConversationId`（由 `ExceptionAiAnalysisModal` 開窗時產生，經 `AiExceptionCallContext`
傳給無狀態的服務），明細窗會列出同一段對話的所有呼叫。本專案的追問與第一次分析共用作業名稱「AI 例外分析」，以關聯說明區分輪數。

### 3.2 何時記

**只要請求確實送出就記**：成功、上游錯誤（保存錯誤 body）、回應為空／內容過濾／長度上限、逾時、連線中斷、使用者取消、非預期錯誤。
設定不完整（根本沒送出）不記。

⚠️ Token 用量**不記**使用者取消與非預期錯誤的呼叫（沿用既有規則），所以兩頁筆數可能不同；明細窗的用量區塊會說明原因。

### 3.3 保存的內容

| 項目 | 說明 |
|---|---|
| 原始請求 | `AiChatRequestFactory` 產生的 JSON，**逐字**保存（中文為 `\uXXXX`，畫面另行排版）。**不含 HTTP 標頭與金鑰** |
| 原始回應 | 回應 body（含錯誤 body）；沒拿到回應（逾時、連線失敗、取消）時為 null |
| 解析後的回應文字 | `choices[0].message.content` |
| 中繼資料 | 送出時間（本地時間）、作業、帳號／UserId、供應商、要求模型與實際模型、端點（只含 scheme／主機／連接埠／路徑）、HTTP 狀態、結束原因、成敗與原因、例外型別、耗時、關聯說明、ConversationId、CallId |

訊息的角色（system／user／assistant）不另外存，讀取時由原始請求解析（`AiCallLogMessages.Parse`）。
畫面看到的永遠是實際送出的內容。

### 3.4 儲存與生命週期

- 資料表 `AiCallLog`（索引：`OccurredAt`、`CallId` 唯一、`ConversationId`）；`TokenUsageLog` 新增可空欄位 `CallId`（索引），
  migration `AddAiCallLog`。
- 內容檔：`ExternalFileSystem:AiCallLogPath/{yyyyMM}/{CallId:N}.json`，一律經 `AiCallLogFileStore`。
- 寫入順序：先寫檔再建資料列；資料列建不起來就刪檔。寫檔失敗時仍建資料列（`ContentFile` 為 null，明細顯示檔案不存在）。
- 設定 `AiCallLogSettings { Enabled = true, RetentionDays = 90 }`（`RetentionDays` 範圍 1～3650，`ValidateOnStart`）：
  - `Enabled=false` 不記。
  - 排程作業「AI 對話紀錄清理」（0.9.96 起預設每天 03:00；之前是 `AiCallLogRetentionWorker` 啟動時與每 24 小時）刪除過期資料列與檔案，並清掉門檻月份之前的整個月份目錄（收拾孤兒檔）；刪到資料時寫 `AiCallLog.AutoPurge`。
  - 停用時仍執行過期清除。
  - 兩個值都可在「系統參數」頁「資料保留」分類覆寫（「AI 對話紀錄保留天數」「記錄 AI 對話內容」）。
- 系統備份預設**不含**內容檔（`BackupSettings:IncludeAiCallLogs` 預設 `false`，系統參數「備份 AI 對話內容」）：內容敏感、備份沒有加密；換主機還原後資料列還在，明細只看得到中繼資料（「內容檔不存在…」）。見 [系統備份與還原 PRD](系統備份與還原-prd.md)。
- 上限：關聯說明截斷至 500 字元。
- 記錄**絕不影響** AI 呼叫本身：recorder 全程吞例外，呼叫點另以 `RecordSafelyAsync` 在 `finally` 中執行。

## 四、畫面

UI 依本專案的設計規範：說明框沿用 Token 用量頁的淡色框、按鈕一律 `ToolbarIconButton`／`CrudActionButton`、
狀態一律 `StatusPill`、確認窗一律 `ConfirmDialog`、顏色只用 `theme.css` 的 token。

### 4.1 清單

- 篩選：起始日、結束日、作業、結果（成功／失敗或取消）、模型、帳號（部分比對）、關聯說明關鍵字（部分比對）。
- 欄位：
  - 送出時間（可排序，預設新到舊）
  - 結果（`StatusPill`：成功／失敗（原因）／已取消）
  - 作業、發起者、模型
  - 耗時（可排序）、送出字元（可排序）
  - 關聯說明
  - 操作：查看對話、刪除
- 工具列：查詢、重新整理、匯出 CSV（上限 10,000 筆，**只含中繼資料**）、清空全部；另有「清除此日之前＋批次清除」。
- 破壞性動作一律 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 刪除、清除、清空與匯出都寫自我稽核（`AiCallLog.Delete`／`Purge`／`ClearAll`／`Export`），只記動作、Id 與筆數。

### 4.2 明細視窗（`.ai-call-log-modal`，96vw／96vh，0.9.75 起）

- **一般／中／大**：窗內字級三段縮放（`--ai-font-scale`），與日誌檢視、例外 AI 分析兩個視窗一致；關窗回到「一般」。
- **上方資料**：
  - 基本：送出時間、作業、發起者、結果與例外型別、供應商／模型（要求→實際）、端點、HTTP／結束原因、耗時、關聯說明、呼叫識別碼。
  - **用量與費用**：以 CallId 帶出 Token 用量的輸入／輸出／合計與 NT$／US$。
- **同一段對話**：ConversationId 相同的其他呼叫，可點選切換。
- **五種檢視**（只渲染目前這一種）：
  1. 完整對話：依序列出送出的每則訊息，最後是 AI 回應。
  2. 送出 Prompt：只列送出的訊息。
  3. 取得 Response：只列 AI 回應；未取得時附說明。
  4. 原始 Request：排版後的 JSON。
  5. 原始 Response：排版後的 JSON body；沒有 body 時說明原因。
- **每段內容**：
  - 淺色卡片，左側色條標示角色：系統提示（System，灰）／使用者（User，重點色）／AI 助理（Assistant，綠）。
  - 以 `StatusPill` 標示方向：送出／取得。追問時的歷史回答標示「送出（對話歷史）」。
  - 附字元數與複製鈕（複製完整內容）。
  - 原始 Request／Response 用深色原文區塊（`--app-code-*`，與日誌檢視、健康監控、例外堆疊同一組）。
- **純文字／Markdown 切換**：預設純文字。Markdown 一律經 `AiMarkdownRenderer.ToSafeHtml`，渲染結果超過 512 KB 時退回純文字。
- **顯示上限**：每段畫面最多顯示 20 萬字元，超過時提示改用複製或下載 JSON。
- **下載完整紀錄（JSON）**：內容檔原文（不加 BOM）。
- **匯出 PDF**：`AiCallLogPdfBuilder`（沿用 `AiMarkdownPdfRenderer`），中繼資料加完整對話，**不含原始 JSON**。每段上限 20 萬字元，超過時註明截斷。

### 4.3 Token 用量頁的連結

Token 用量明細窗新增「AI 對話紀錄」列：
- 有對應紀錄時顯示查看對話按鈕，導向 `/ai-call-logs?callId=`。
- 否則顯示「（0.9.72 之前的紀錄沒有對話可看）」或「（對話紀錄已清除，或當時未啟用記錄）」。

深連結帶查詢字串，所以 `MainLayout`／`NavMenu` 比對選單前會先去掉 `?` 與 `#`（頁名與選單高亮才會正確）。

## 五、驗收

| 項目 | 守門 |
|---|---|
| 記錄、停用、寫檔失敗、截斷、DB 失敗不丟例外、篩選、排序分頁、明細（訊息／用量／對話）、刪除、清除、清空、過期與孤兒目錄 | `AiCallLogServiceTests` |
| 結果預設值、送出旗標、記錄失敗不外拋、端點去除查詢字串、訊息解析 | `AiCallCaptureTests` |
| 成功／錯誤 body／逾時／傳輸失敗／取消／非預期錯誤／未設定不記、CallId 與 Token 用量一致、內容不含金鑰、recorder 丟例外不影響結果 | `AiChatCompletionClientTests`、`AiLogAnalysisServiceTests`、`AiHealthProbeTests` |
| 例外分析與追問共用 ConversationId、RelatedInfo | `AiExceptionAnalysisServiceTests` |
| PDF 產出、中繼資料、角色標示 | `AiCallLogPdfBuilderTests` |
| 設定預設值與範圍 | `AiCallLogSettingsTests` |
| 管理員專屬、選單一致、圖示、操作說明、資料服務生命週期、字級縮放、設定文件 | `AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`DataAccessServiceLifetimeTests`、`AiModalStyleConventionTests`、`DocumentationConventionTests` |

## 六、已知限制

- 不支援內文全文搜尋；只能用中繼資料與關聯說明縮小範圍。
- AI 日誌分析的關聯說明是「實際送出的日誌」時間區間，不含日誌檢視頁當時的查詢條件。
- 日誌分析的單一內容檔可能數 MB；畫面與 PDF 以上限保護，完整內容請下載 JSON。
- 每次開啟系統健康監控都會新增一筆「系統健康檢測」紀錄（內容很小，可用作業篩掉，也會自動過期）。
- 內容檔寫在 AI 呼叫回到畫面之前；日誌分析送出大量日誌時，會多出一次寫檔的時間。

## 七、規劃中需求

- 內文全文搜尋（需另建索引或在篩選結果內逐檔掃描）。
- 多筆合併匯出 PDF。
