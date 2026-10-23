# 日誌與例外處理 PRD

- 文件版本：1.10
- 文件狀態：已實作
- 現行系統版本：1.0.5
- 首次實作版本：0.9.11（例外自動記錄管線上線）
- 最後核對日期：2026/10/24

> 本文件是**全系統共用**的需求規範，不是單一頁面。往後**任何功能的開發與驗收**，凡涉及日誌、例外處理、稽核、
> 告警，一律以本文件為準。§三、§四 是「每個功能都必須遵守」的開發規範；§六 列出已實作的基線與尚待實作的缺口
> （P0 於 0.9.77、P1 於 0.9.78、P2 於 0.9.79 完成；尚待處理的事項見 §十）。

## 一、目標與範圍

**目標**：系統裡發生的任何一件事——使用者的操作、捕捉到的例外、**沒有被捕捉到的例外**——事後都能回答
「**誰、何時、在哪一頁、做了什麼、結果如何**」；嚴重問題不必等管理員自己去翻，系統會主動通知。

- **範圍**
  - NLog 檔案日誌：等級規則、必須記錄的事件、情境欄位、敏感資料。
  - 系統例外紀錄（`ExceptionLog`）：收錄條件、未捕捉例外的涵蓋範圍。
  - 稽核紀錄（`AuditLog`）：必須稽核的事件。
  - 四類紀錄（日誌檔、例外、稽核、AI 對話）的保存期限。
  - 例外告警通知、瀏覽器端錯誤回報、慢操作記錄、日誌管線的自我監控。
- **非範圍**
  - 集中式日誌平台（Seq／ELK／Loki／Application Insights）與結構化 JSON 輸出 —— 本系統以**系統內頁面**
    （`/logs`、`/system-exceptions`、`/audit-logs`、AI 分析）為主要分析方式，日誌檔維持現有純文字格式，
    因為 `/logs` 檢視器與 AI 日誌分析都依賴這個格式解析。
  - 稽核記錄「修改前後值」（diff）。
  - APM、分散式追蹤、多台主機的日誌彙整。
- **與其他文件的分工**

  | 文件 | 負責 |
  |---|---|
  | **本 PRD** | 規定「必須做到什麼」，是驗收依據；等級規則以本文件 §三 為準 |
  | [日誌與設定檔說明](../operations/日誌與設定檔說明.md) | 「怎麼設定、怎麼寫」：NLog 設定、寫法範例、appsettings 鍵、守門測試說明 |
  | [系統例外紀錄 PRD](系統例外紀錄-prd.md)、[稽核紀錄 PRD](稽核紀錄-prd.md)、[日誌檢視 PRD](日誌檢視-prd.md)、[日誌等級設定 PRD](日誌等級設定-prd.md)、[AI 對話紀錄 PRD](AI對話紀錄-prd.md) | 各頁面的畫面、欄位與操作細節 |
  | [開發慣例與限制速查 §6.6](../architecture/開發慣例與限制速查.md) | 例外管線的防遞迴紅線 |

## 二、四種紀錄與事件的去向

系統有四種持久紀錄，各有分工，**不可互相取代**：

| 紀錄 | 存放 | 回答的問題 | 查看入口 |
|---|---|---|---|
| NLog 日誌檔 | `{NLog:BasePath}\CustomerHelpAgent.Web\*.log`（純文字） | 系統當時逐步發生了什麼 | `/logs` |
| 系統例外紀錄 | SQLite `ExceptionLog` ＋ 堆疊檔 | 有哪些程式缺陷、發生幾次、誰遇到 | `/system-exceptions` |
| 稽核紀錄 | SQLite `AuditLog` | 誰在何時對什麼資料做了什麼 | `/audit-logs` |
| AI 對話紀錄 | SQLite `AiCallLog` ＋ 內文檔 | 每次 AI 呼叫送了什麼、回了什麼 | `/ai-call-logs` |

**每一類事件寫到哪裡**（✔＝必須寫）：

| 事件類別 | 日誌檔 | 例外紀錄 | 稽核紀錄 | 告警 |
|---|:-:|:-:|:-:|:-:|
| 使用者操作（瀏覽、查詢、按鈕意圖） | ✔ | | | |
| 寫入動作（新增／修改／刪除／匯出／下載／清除） | ✔ | | ✔ | |
| 安全事件（登入、登出、權限拒絕、改密碼） | ✔ | | ✔ | |
| 外部呼叫（AI、Email、Google SSO） | ✔ | | | |
| 使用者錯誤（驗證失敗、重複名稱、查無資料） | ✔ | | | |
| 可預期的外部失敗（SMTP、AI 逾時） | ✔ | | | |
| 系統缺陷（捕捉到的未預期例外） | ✔ | ✔ | | 依 §6 LOG-12 條件 |
| 未捕捉例外 | ✔ | ✔ | | 依 §6 LOG-12 條件 |
| 瀏覽器端錯誤 | ✔ | ✔ | | 依 §6 LOG-12 條件 |

使用者錯誤與可預期的外部失敗**不帶例外物件、等級低於 Error**，因此不會進例外紀錄表；等級依 §三 判定。
例外紀錄表的收錄門檻是「**Error 以上且帶例外物件**」（`OperationCanceledException` 除外），由
`ExceptionLogProvider` 自動攔截，開發者只要正確寫 `LogError(ex, …)` 就會收錄。

## 三、日誌等級規則（驗收依據）

本章以 [日誌與設定檔說明](../operations/日誌與設定檔說明.md) §2.1–2.3 的既有判定為基礎完整寫出，並補上原本沒有規定的情境。
**兩份文件若不一致，以本章為準**，並回頭修正操作文件。

### 3.1 選等級的三個問題

依序問，第一個答「是」的就是答案：

1. 這個結果是**使用者輸入的正常後果**，行為端正的使用者日常操作就會遇到嗎？
   → **Information**（若屬純查詢／換頁／排序則為 **Debug**）
2. 這是**非預期但已處理**的狀況 —— 可能的誤用、設定錯誤、安全相關的拒絕、資料完整性異常？
   → **Warning**
3. 使用者要求的操作**失敗了**，需要程式或維運介入（例外、外部資源失敗）？
   → **Error**

兩條可直接套用的細則：

- **讀取查無資料 = Information；寫入查無資料 = Warning。** GET 一筆已刪除的資料是常態；UPDATE／DELETE 一筆不存在的資料代表畫面過期或有人在探測。
- **欄位／重複名稱驗證失敗一律 Information；權限與身分驗證失敗一律 Warning。**

> **為什麼驗證失敗不是 Warning**：0.4.30 曾因把業務驗證未通過列為 Warning，導致 Warning 與 Information 數量相當，
> 真正的異常被淹沒，當時重新分類了 50 處。**Warning 是給「維運人員應該看一眼」的事；使用者打錯字不是。**

### 3.2 六個等級的定義

| 等級 | 使用時機 | 預設會寫入檔案？ |
|---|---|:-:|
| `Trace` | 逐筆、迴圈內每一次迭代的追蹤；外部呼叫的原始中繼資料（狀態碼、標頭名稱、重試次數等，**不含內文**）。**不強制使用**，有排錯需求時才寫，不為了填滿等級而寫；既有程式不要求補寫。 | 否 |
| `Debug` | 流程細節、查詢條件、分頁排序、快取命中、權限判定過程、純查詢按鈕。**排查時才看。** | 否 |
| `Information` | **使用者做了什麼**（畫面切換、新增／修改／刪除／匯出／登入登出）、商業流程完成、外部呼叫完成、啟動與關閉。 | 是 |
| `Warning` | 授權被拒、身分驗證失敗、寫入時查無資料、併發衝突、設定或資料完整性異常、可預期的外部失敗、慢操作、連線中斷。 | 是 |
| `Error` | 例外、外部資源重試後仍失敗、重要流程中斷。**帶例外物件時自動進例外紀錄表。** | 是 |
| `Critical` | 應用程式無法繼續運作而即將終止。目前全專案只有 `Program.cs` 頂層 catch 一處；新增前須說明理由。 | 是 |

### 3.3 預設可見性 ⚠️

- `nlog.config` 的全域規則 `*` 最低等級是 **Info**，所以 **Debug 與 Trace 預設不會寫入檔案**；要到
  `/log-level-setting` 把等級調低才會出現（重啟後恢復）。
- `Microsoft.*`、`System.*`、`Microsoft.EntityFrameworkCore.*` 只記 **Warning 以上**；`Microsoft.Hosting.Lifetime*` 記 Info 以上。
- **推論：必須事後查得到的事，不可只記 Debug。** 凡是會出現在 §3.4「Information 以上」欄的事件，若只寫成 Debug，視同沒有記錄。

### 3.4 事件目錄

**現行已規定**（沿用）：

| 事件 | 等級 | 記在哪 |
|---|---|---|
| 畫面切換 | Information | `ApplicationCircuitHandler` 集中記錄，**頁面不需自己寫** |
| 改變狀態的按鈕（新增／修改／刪除／匯出／套用／登入登出） | Information | 各事件處理方法；**意圖與結果各記一筆** |
| 純查詢的按鈕（搜尋／重新整理／分頁／排序） | Debug | 各事件處理方法 |
| circuit 開啟／關閉 | Information | `ApplicationCircuitHandler` |
| circuit 斷線 | Warning | `ApplicationCircuitHandler` |
| 權限拒絕、登入失敗、帳號鎖定 | Warning | 各自的處理方法 |
| HTTP 請求完成 | Information（靜態資源、`_blazor`、`/health`、`/swagger` 為 Debug；狀態碼 ≥ 400 一律 Information 以上） | `UseHttpRequestLogging` |

**本文件新增**（原本沒有規定）：

| 事件 | 等級 | 必帶欄位 |
|---|---|---|
| 外部呼叫（AI、Email、SSO）完成 | Information | `Operation`、`ElapsedMilliseconds`、結果 |
| 外部呼叫的請求細節（模型、參數、狀態碼） | Debug | — |
| 外部呼叫可預期的失敗（逾時、4xx、SMTP 拒收），會重試或降級 | Warning | `Attempt`、失敗原因 |
| 外部呼叫重試後仍失敗 | Error（帶例外物件） | — |
| 背景作業啟動、停止 | Information | 作業名稱 |
| 背景作業每一輪 | **有處理到資料**時 Information（摘要：筆數、耗時）；**空跑**時 Debug | `ProcessedCount`、`ElapsedMilliseconds` |
| 慢操作（超過 §6 LOG-21 的門檻） | Warning | 操作名稱、耗時、門檻 |
| 設定值缺漏或不合法、改用預設值 | **啟動時記一次** Warning，不可每次使用都記 | 設定鍵名稱（不含值） |
| 迴圈或批次處理 | 迴圈結束後記一筆摘要（Debug 或 Information）；逐筆細節才用 Trace | 總筆數、成功／失敗筆數 |

### 3.5 效能規則

- 引數需要額外計算（序列化、字串組合、查詢）時，先以 `logger.IsEnabled(level)` 判斷再計算。
- 熱路徑（每個請求、每次渲染、每筆資料都會經過的程式）**不得**以 Information 以上逐筆記錄。
- 不使用 `{@Obj}` 解構整包物件（同時是效能與外洩問題）。

## 四、開發規範（每個功能都必須遵守）

### 4.1 例外處理三原則

1. **只在能處理的地方 catch。** 處理不了就讓它往上拋，交給全域攔截（§五）。
2. **catch 了就必須記錄。** 系統缺陷用 `logger.LogError(ex, "…")`（例外放第一個參數）；
   使用者錯誤依 §3.1 判定等級，且**不帶例外物件**（帶了就會被誤收進例外紀錄表）。
3. **禁止空 catch 與靜默吞掉。** 真的可以忽略的例外（例如 JSON 解析失敗改用預設值），至少記一筆
   Debug 或 Warning，並在程式註解寫明為什麼可以忽略。唯一例外是例外管線本身（§4.5）。

### 4.2 例外分類表

| 分類 | 舉例 | 等級 | 帶例外物件 | 進例外紀錄 | 使用者看到 |
|---|---|---|:-:|:-:|---|
| 使用者錯誤 | 欄位驗證失敗、重複名稱／重複鍵、讀取查無資料 | Information | 否 | 否 | 具體的友善訊息 |
| 使用者錯誤（可疑） | 寫入時查無資料、併發衝突 | Warning | 否 | 否 | 「資料已被變更，請重新整理」類訊息 |
| 可預期的外部失敗 | SMTP 失敗、AI 逾時、SSO 回應錯誤 | Warning；重試仍失敗改 Error | Error 時帶 | 只有 Error 才進 | 「服務暫時無法使用」類訊息 |
| 系統缺陷 | 程式錯誤、未預期的例外 | Error | 是 | 是 | 通用錯誤訊息＋**錯誤追蹤碼**（LOG-10） |
| 系統無法繼續 | 啟動失敗、應用程式即將終止 | Critical | 是 | 是，並觸發告警 | — |

使用者看到的訊息**不得**含例外型別、堆疊、SQL、檔案路徑；API 回應是否帶例外細節由 `ExceptionDetailPolicy` 決定（正式環境不帶）。

例外（0.9.130 起）：右下角錯誤通知的「複製」按鈕，在**管理員**或 `ExceptionDetailPolicy` 允許時（Development、`Security:ReturnExceptionDetails=true`）會在剪貼簿內容最後附上完整例外（`ex.ToString()`：類型、訊息、內層例外、堆疊）；AI 上游失敗則附 HTTP 狀態與上游 code／type／param（不含上游 message 與回應本文）。畫面上仍不顯示這些細節，也不寫進通知那筆日誌（例外已由呼叫端 `LogError(ex, …)` 記錄）。其他使用者複製到的只有畫面內容與追蹤碼。

### 4.3 每筆紀錄的必備情境

| 欄位 | 日誌檔 | 例外紀錄 | 說明 |
|---|:-:|:-:|---|
| 時間、等級、Logger 名稱 | ✔ | ✔ | 已具備 |
| 追蹤碼（TraceId） | ✔ | ✔ | 8 碼錯誤追蹤碼：HTTP 請求與 Blazor 每次互動各一個；例外紀錄存 `LastTraceId`（0.9.78，LOG-10） |
| 來源（Source） | | ✔ | `畫面`／`WebAPI`／`系統啟動`／`背景作業`／`系統`（0.9.77，LOG-04）／`瀏覽器`（0.9.79，LOG-20）／`未知` |
| 頁面 | 依訊息 | ✔ | **一律記路由樣板**（`/api/v1/projects/{id}`），不記帶 Id 的原始路徑（0.9.78，LOG-16） |
| Account、UserId | 依訊息 | ✔ | **可記錄的身分只有這兩項** |
| CircuitId | Blazor 相關訊息 | | 用於串起同一位使用者的連續操作 |

### 4.4 敏感資料

沿用 [日誌與設定檔說明](../operations/日誌與設定檔說明.md) §2.4 的禁記清單（密碼、Salt、token、金鑰、驗證碼、TOTP、連線字串、Email、姓名、電話），並明訂：

- **查詢關鍵字**：不記原文，只記 `HasSearch`（有無）與 `SearchLength`（長度）；Web API 的參數名稱是 Keyword，對應 `HasKeyword`／`KeywordLength`。0.9.77 起由慣例測試守門（LOG-08）。
- 不記 QueryString、HTTP header、cookie。
- 例外紀錄、稽核 `Detail`、告警信、日誌檔**都不得**寫入 AI Prompt／Response 內文；內文只存在 AI 對話紀錄。
- Cookie 驗證下 `ClaimTypes.Name` 是**使用者姓名**，不可記錄（claim 對應見操作文件 §2.4）。

### 4.5 記錄機制不可破壞業務流程

- 任何記錄動作（日誌、例外紀錄、稽核、告警）**失敗時都不得拋出例外**或中斷呼叫端流程；稽核寫入失敗只記 Warning。
- 例外管線的四個類別（`ExceptionContextAccessor`、`ExceptionLogProvider`、`ExceptionLogWriter`、`ExceptionStackFileStore`）
  **不可注入 `ILogger`**，需要留話一律走 `NLog.Common.InternalLogger`，避免遞迴。本文件新增的告警、清理、
  前端回報等元件若位於管線內，同樣適用。
- 佇列滿時寧可丟棄、不可阻塞，但**丟棄必須被計數並看得到**（LOG-02、LOG-22）。

### 4.6 守門方式

| 規範 | 守門 |
|---|---|
| 訊息英文、PascalCase 佔位、禁 `{@}`、禁敏感佔位與敏感屬性、行為類別必有 `ILogger` | `LoggingConventionTests`（已有） |
| 例外簽章用訊息樣板 | `ExceptionSignatureTests`、`ExceptionLogProviderTests`（已有） |
| 禁查詢關鍵字佔位（`search`／`keyword`，`HasSearch` 等旗標除外） | `LoggingConventionTests.LogPlaceholders_ShouldNotRecordSearchText`（0.9.77） |
| 禁空 catch（本體只有註解也算） | `LoggingConventionTests.CatchBlocks_ShouldNotBeEmpty`（0.9.77）；取消／斷線類例外與管線類別列白名單，須註明理由 |
| 稽核動作代碼只能用 `AuditActions` 常數 | `AuditConventionTests`（0.9.78） |
| 等級選得對不對、有沒有記到該記的事 | **人工 Code Review**，依本文件 §三 判斷 |

## 五、未捕捉例外的涵蓋矩陣

「未捕捉」指沒有被業務程式 catch 的例外。每個進入點都必須有一道最後防線，把例外記進日誌檔與例外紀錄表。

| 進入點 | 現行防線 | 現況 | 需求 |
|---|---|:-:|---|
| Web API（`/api/*`） | `ApiExceptionFilterAttribute`：記 Error，回 `ApiResult` 500 附 `TraceId`；控制器自行 catch 的 `ApiServerError` 也帶（0.9.78） | ✅ | — |
| 一般 HTTP 請求（非 API） | `UseHttpRequestLogging` 記 Error 後重拋；非開發環境 `UseExceptionHandler("/Error", createScopeForErrors: true)`；以例外實例去重、帳號於驗證後補上（0.9.77）；Host 空白或組不成網址由 `UseRejectInvalidHost` 回 400（0.9.115） | ✅ | —（0.9.78 起 `/Error` 頁顯示的追蹤碼與日誌一致；0.9.115 起 Blazor 頁面 SSR 例外也看得到錯誤頁，之前錯誤頁本身拋 "already initialized" 而回空白 500） |
| Blazor 頁面元件 | `LoggingErrorBoundary` 包住 `AuthorizeRouteView`，記 Error 並顯示錯誤訊息與追蹤碼（0.9.78） | ✅ | — |
| Blazor 浮層（對話窗、通知、確認窗） | `AntContainer` 包在第二個 `LoggingErrorBoundary` 內，換頁自動復原（0.9.77），提示附追蹤碼（0.9.78） | ✅ | — |
| 射後不理的 Task（`_ = XxxAsync()`） | `ProcessExceptionHooks`：`UnobservedTaskException` 記 Error，來源 `系統`（0.9.77） | ✅ | — |
| 程序層級（其他執行緒） | `ProcessExceptionHooks`：`AppDomain.UnhandledException` 寫補登檔並記 Critical（0.9.77） | ✅ | — |
| 背景服務 | 四個背景服務（`ExceptionLogWriter`、`EmailDispatchWorker`、`JobSchedulerWorker`、`SystemParameterRefreshWorker`）都自行 catch；未設定 `BackgroundServiceExceptionBehavior` | ✅ | 維持；新 Worker 必須自行 catch 並記 Error |
| 啟動：`builder.Build()` 之前 | 頂層 catch 直接經 NLog 寫檔，並寫補登檔（0.9.77） | ✅ | — |
| 啟動：遷移、種子資料 | 頂層 catch 記 Critical 並寫補登檔，下次啟動補進例外紀錄（0.9.77） | ✅ | — |
| 關機 | 寫入背景服務停止時清空佇列，上限 5 秒（0.9.77） | ✅ | — |
| 瀏覽器 JavaScript | `client-error-reporter.js` ＋ `BrowserErrorReporter`，經 circuit 回報，來源 `瀏覽器`（0.9.79） | ✅ | 僅限登入後頁面 |

## 六、需求清單

依 [PRD 維護規則](README.md) 第 5 條，「已實作」與「規劃中」分開列出。規劃中需求**不代表系統已提供**。

### 6.1 已實作（現況基線）

| 能力 | 現況 | 主要程式 |
|---|---|---|
| NLog 檔案日誌 | 每日一檔、保留 30 天、單檔超過 100MB 切檔、非同步寫入、佇列滿丟棄 | `nlog.config`、`Program.cs` |
| 執行期調整等級 | `/log-level-setting` 調整全域 `*` 規則，動作寫稽核 | `LogLevelRuntimeState` |
| 全域請求日誌 | 方法、路徑、狀態碼、耗時 | `ApplicationBuilderExtensions.UseHttpRequestLogging` |
| 例外自動收錄 | 任何 `LogError/LogCritical(ex, …)` 自動進例外紀錄表；相同簽章合併計次；堆疊另存檔案；上限 5000 列 | `ExceptionLogProvider`、`ExceptionLogWriter`、`ExceptionLogService` |
| 例外情境 | 來源、頁面、帳號由 `ExceptionContextAccessor` 提供（HTTP、circuit 互動、啟動、背景作業） | `ApplicationCircuitHandler`、`UseHttpRequestLogging` |
| 稽核 | 動作代碼 0.9.78 起收斂到 `AuditActions`（當時約 31 種，現行共 147 個常數）：登入、使用者／角色異動、密碼重設、檔案下載、日誌與 AI 相關操作、清除動作、清單點狀態標籤的啟用／停用（0.9.120）、測試通知（0.9.122）；0.9.117 起每筆自動記來源 IP（`AuditLogService` 經 `IClientIpProvider` 取得，呼叫端不傳；排程作業為空）；0.9.131 起加上開頁（`Page.View`）、查詢（`*.Search`）、查看單筆（`*.View`）與修改前後差異（寫在詳細內容檔）。每筆同步記一行 Debug `Audit recorded.`（只有短摘要與檔名，不含內容檔） | `AuditLogService` |
| API 錯誤回應 | `ApiResult` 500 附 `TraceId`，正式環境不帶例外細節 | `ApiExceptionFilterAttribute`、`ExceptionDetailPolicy` |
| 啟動保護 | 整段 try/catch，失敗記 Critical 並 `LogManager.Shutdown()` | `Program.cs` |
| Blazor 錯誤邊界 | `LoggingErrorBoundary` | `Routes.razor` |
| 慣例守門 | 訊息格式、敏感資料、必備 `ILogger` | `LoggingConventionTests` |

### 6.2 已實作 —— P0 缺口修正（0.9.77）

以下八項原列為規劃中的 P0，已於 0.9.77 完成。每項都有自動化測試守門；
「拿掉修正後測試必須轉紅」已逐一實測（LOG-01、LOG-02、LOG-03、LOG-08）。

| 編號 | 原本的問題 | 0.9.77 的做法 | 守門測試 |
|---|---|---|---|
| LOG-01 | HTTP／API 例外紀錄的帳號欄恆為空：`UseHttpRequestLogging` 排在驗證之前 | 新增 `UseExceptionContextUser()`，排在 `UseAuthorization` 之後補上帳號（JWT 端點要到授權中介軟體才驗證出 User）；`UseHttpRequestLogging` 的 catch 記錄前再取一次（內層設定的值不會流回外層）。帳號解析改由 `RequestActorResolver` 依 claim 判斷、不依路徑 —— 原本 `/api/project-files`（Cookie）會把**姓名**當帳號 | `ApiIntegrationTests.UnhandledApiException_ShouldRecordAccountInExceptionLog`、`RequestActorResolverTests` |
| LOG-02 | 丟棄筆數永遠是 0：`DropWrite` 模式下 `TryWrite` 永遠回 true | Channel 改為 `Wait` ＋ `TryWrite`（與 `ChannelEmailQueue` 同理），滿載時才回得出 false | `ExceptionLogProviderTests.Log_WhenChannelIsFull_ShouldDropWithoutThrowing` |
| LOG-03 | 非 API 請求的未處理例外記成兩列（請求日誌＋框架 ExceptionHandlerMiddleware） | `ExceptionLogProvider` 以**例外實例**去重（`ConditionalWeakTable`），同一個例外只收第一次；一併涵蓋 DeveloperExceptionPage 與「記錄後重拋」。日誌檔不受影響 | `ApiIntegrationTests.UnhandledPageException_ShouldBeRecordedOnlyOnce`、`ExceptionLogProviderTests.Log_SameExceptionInstanceTwice_ShouldCaptureOnlyOnce` |
| LOG-04 | 射後不理的 Task、背景執行緒的未處理例外完全不留紀錄 | `ProcessExceptionHooks` 訂閱 `TaskScheduler.UnobservedTaskException`（記 Error、標記已觀察、還原情境）與 `AppDomain.UnhandledException`（寫補登檔、抑制收錄下記 Critical）；新增來源 `系統`（`ExceptionSources.Process`）；`ApplicationStopped` 時取消訂閱 | `ProcessExceptionHooksTests` |
| LOG-05 | 關機時佇列中的例外遺失 | `ExceptionLogWriter` 收到停止訊號後清空佇列，上限 5 秒；逾時剩餘筆數輸出到 InternalLogger | `ExceptionLogWriterTests` |
| LOG-06 | 啟動失敗只進日誌檔；host 建立前的失敗連日誌檔都沒有 | `StartupSafetyValidator` 移到 NLog 設定之後；host 建立前的失敗直接經 NLog 寫檔；所有啟動失敗寫入**補登檔**（`{ExceptionPath}/pending/*.json`，`CrashMarkerStore`），下次成功啟動於遷移後補進例外紀錄（來源 `系統啟動`）。`HostAbortedException`（`dotnet ef` 的正常中止）排除在外 | `CrashMarkerStoreTests`；實機驗證：以 Production 設定啟動失敗一次 → 日誌檔有 FATAL、產生補登檔 → 正常啟動後例外紀錄出現該列、補登檔已刪 |
| LOG-07 | 浮層（`AntContainer`）在錯誤邊界外，出錯整個 circuit 中斷 | `Routes.razor` 以第二個 `LoggingErrorBoundary` 包住 `AntContainer`，觸發時在畫面底部顯示「畫面元件發生錯誤，已記錄」並提供重新整理；換頁自動 `Recover()`。追蹤碼待 LOG-10 | `FormModalConventionTests.AntContainer_ShouldBeWrappedInErrorBoundary`（結構守門；瀏覽器內的實際觸發尚未人工驗收） |
| LOG-08 | 查詢關鍵字記錄原文；多處 JSON 解析失敗與情境設定失敗的 catch 不留紀錄 | 17 處改記 `HasSearch`／`SearchLength`（API 為 `HasKeyword`／`KeywordLength`、專案擁有者篩選為 `HasOwner`）；團隊／權限 JSON 解析失敗、健康檢查失敗、例外情境設定失敗補記 Warning；Try 型 API（回傳 false／null 即結果）維持不動 | `LoggingConventionTests.LogPlaceholders_ShouldNotRecordSearchText`、`LoggingConventionTests.CatchBlocks_ShouldNotBeEmpty`（取消／斷線類例外與管線類別列白名單並附理由） |

### 6.3 已實作 —— P1：可追蹤性與主動通知（0.9.78）

以下七項原列為規劃中的 P1，已於 0.9.78 完成；與原規劃不同之處標示「調整」。

| 編號 | 0.9.78 的做法 | 守門測試 |
|---|---|---|
| LOG-10 錯誤追蹤碼 | `TraceCode` 產生 8 碼短碼（Crockford Base32，不含時間、主機、帳號）：HTTP 請求由 `UseHttpRequestLogging` 產生並**取代 `HttpContext.TraceIdentifier`**（`/Error` 重新執行時沿用同一個碼），Blazor 每次互動由 `ApplicationCircuitHandler` 產生；兩者放進 NLog ScopeContext。例外紀錄新增 `LastTraceId`（migration `AddExceptionLogLastTraceId`），明細窗、點列複製、CSV 都顯示，關鍵字可搜尋。錯誤邊界兩處、`/Error` 頁（改為中文並修正原本顯示 `Activity.Id` 對不上的問題）、表單儲存失敗與 18 處「…失敗：{型別}」通知（`ViewNotification.UnexpectedError`）顯示「錯誤追蹤碼：xxxx」；`ApiServerError` 回應帶 `TraceId`。`/logs` 新增「錯誤追蹤碼」篩選（比對 TraceId 欄位完全相同）。**調整**：不新增日誌欄位，沿用原本的 TraceId 欄位（`${scopeproperty:item=TraceCode:whenEmpty=${aspnet-TraceIdentifier}}`），欄位數維持 7 個、舊日誌檔照樣解析。只有「未預期例外」的訊息附追蹤碼，「名稱重複」等一般驗證訊息不附 | `TraceCodeTests`、`LogQueryServiceTests.Query_TraceCodeFilter_ShouldKeepOnlyExactTraceIdMatches`、`ApiIntegrationTests.UnhandledApiException_TraceIdShouldMatchExceptionLog`、`ApiIntegrationTests.CaughtApiException_ShouldReturnTraceId`、`ExceptionLogServiceTests.RecordAsync_ShouldKeepLastTraceId_AndReturnOutcome` |
| LOG-11 使用者錯誤不進例外紀錄表 | 分類、團隊的新增／修改撞唯一索引時先判斷，命中記 Information、不帶例外物件；`ProjectController` 刪除時的外鍵衝突改為先判斷再記錄，並補上 SQLite 的訊息（原本只認 SQL Server） | `TeamServiceTests.AddAsync_WhenRejected_ShouldNotWriteAudit` 等既有服務測試 |
| LOG-12 例外 Email 告警 | `ExceptionAlertService`（`ExceptionAlertSettings`）在寫入器每筆寫入後評估，補登檔匯入時也評估；觸發：新簽章、Critical、暴增；節流：同簽章冷卻、全系統每小時上限（超過的筆數併入下一封）。`RecordAsync` 改為回傳 `ExceptionRecordOutcome`，`ExceptionLogEntry` 新增 `IsCritical`。信件 `EmailKinds.ExceptionAlert` 只含摘要。寄信背景作業自身的錯誤不告警。出貨預設停用 | `ExceptionAlertServiceTests`（假時鐘） |
| LOG-13 統一保存期限 | `LogRetentionSettings`＋排程作業（0.9.96 起預設每天 03:00；0.9.78～0.9.95 為 `LogRetentionWorker` 啟動時與每日；`TimeProvider` 可替換）；例外以本地時間、稽核以 UTC 計算門檻；刪到資料時寫 `ExceptionLog.AutoPurge`／`Audit.AutoPurge`。兩頁的手動清除改讀同一組天數。**調整**：「不自動清理」為 `0`（不接受負數） | `RetentionJobTests`（假時鐘，0.9.96 起取代 `LogRetentionWorkerTests`） |
| LOG-14 補齊稽核事件 | 動作代碼收斂到 `CustomerHelpAgent.Business.Helpers.AuditActions`。新增：分類／團隊／專案增刪改（Blazor 走服務、API 走 Controller，`ControllerAuditExtensions`）、專案附件上傳／刪除筆數、登出、Google SSO 成功／失敗／停用／帳號連結／自動建立、自行變更密碼（兩條路徑）、JWT 刷新失敗、Blazor 頁面權限拒絕（`AuthenticationStateHelper.RecordPageAccessDeniedAsync`，14 頁）、例外紀錄與 Token 用量的刪除／清除／清空、稽核／例外／AI 對話／日誌／Token 用量的匯出、自動清理 | `AuditConventionTests`（禁字串字面值、代碼唯一）、`TeamServiceTests.AddUpdateDelete_ShouldWriteAuditWithCurrentUser` |
| LOG-15 補上缺少日誌的類別 | `JwtTokenService`、`ApiValidationFilterAttribute`（只記欄位名稱）、`DatabaseHealthCheck`、`RecordAccessScopeProvider`、登出（含帳號）補上日誌；`LoggingConventionTests` 的「必有 ILogger」範圍加入 `Web/Auth`、`Web/Filters`、`Web/Health` | `LoggingConventionTests.BehaviourClasses_ShouldHoldALogger` |
| LOG-16 頁面記路由樣板 | `UseExceptionContextUser` 與請求日誌的 catch 以 `RouteEndpoint.RoutePattern.RawText` 取代原始路徑（例如 `/api/ContractProbe/throw/{id}`）；Blazor 頁面目前沒有帶參數的 `@page`，維持原路徑 | `ApiIntegrationTests.UnhandledApiException_ShouldRecordRouteTemplateAsPage` |

### 6.4 已實作 —— P2：可觀測性強化（0.9.79）

以下三項原列為規劃中的 P2，已於 0.9.79 完成；與原規劃不同之處標示「調整」。

| 編號 | 0.9.79 的做法 | 守門測試 |
|---|---|---|
| LOG-20 瀏覽器端錯誤回報 | `wwwroot/js/client-error-reporter.js` 掛上 `error` 與 `unhandledrejection`，經 circuit 呼叫 `BrowserErrorReporter.Report`（`[JSInvokable]`，每個 circuit 一份），以 Error 記錄並帶 `BrowserScriptException`，進系統例外紀錄（來源 `瀏覽器`）。只有登入後的 MainLayout 會註冊回報（靜態登入頁沒有 circuit，錯誤只暫存不送出）。防濫用：每個 circuit 每分鐘最多 10 筆（`ClientErrorReporting:MaxPerCircuitPerMinute`），超過的丟棄並計數；訊息 1000 字、堆疊 4000 字、頁面只取路徑（去掉查詢字串與片段）；過濾瀏覽器外掛與 `ResizeObserver loop`、`Script error.` 等已知雜訊 | `BrowserErrorReporterTests` |
| LOG-21 慢操作記錄 | `SlowOperationSettings`：HTTP 請求 3 秒（`UseHttpRequestLogging`，排除 `/_blazor` 長連線）、資料庫指令 1 秒（`SlowDbCommandInterceptor`，只記指令類型）、寄信 10 秒（`EmailDispatchWorker`）、AI 呼叫 60 秒（`AiChatCompletionClient`、`AiHealthProbe`）；超過記 Warning。**調整**：不量「Blazor 單次互動」—— AI 分析在按鈕事件裡等待、確認窗等使用者按鈕都會算進互動時間，每次誤報；Google SSO 的往返本身就是 HTTP 請求，由 HTTP 門檻涵蓋，不另設 | `LoggingPipelineHealthTests`（門檻判斷、指令類型只取第一個關鍵字、`/_blazor` 排除）；實機以 1 毫秒門檻驗證三類 Warning 都寫入日誌檔 |
| LOG-22 日誌管線自我監控 | `LoggingPipelineMonitor`（單例、不得注入 ILogger）累計：例外寫入失敗（`RecordAsync` 回傳 null）、前端回報限流丟棄、告警信入列失敗與寄送失敗、NLog 內部錯誤（訂閱 `InternalLogger.InternalEventOccurred`，Error 以上）。系統健康監控新增「日誌管線」（權重 10，總權重 145；之後加入「排程作業」權重 5，現行 14 項總權重 150），另讀例外佇列長度、`DroppedCount` 與日誌所在磁碟可用空間 | `LoggingPipelineHealthTests`、`ExceptionLogWriterTests.RunAsync_WhenRecordFails_ShouldCountWriteFailure`、`SystemHealthTests.CheckWeights_ShouldSumTo150` |

## 七、設定鍵

全部已實作：`ExceptionAlertSettings`、`LogRetentionSettings`（0.9.78）、`SlowOperationSettings`、`ClientErrorReporting`（0.9.79），
逐鍵說明見 [日誌與設定檔說明](../operations/日誌與設定檔說明.md) §4.11～§4.14。
新增 `ExternalFileSystem` 路徑時，須同步加進 `ApiIntegrationTests.CreateSettings()`（見速查表 §6.6）。

| 區段 | 鍵 | 預設 | 意義 |
|---|---|---|---|
| `ExceptionAlertSettings` | `Recipients` | `[]` | 告警收件人；空清單＝停用 |
| | `BurstThreshold` ／ `BurstWindowMinutes` | `20` ／ `10` | 暴增判定 |
| | `PerSignatureCooldownMinutes` | `60` | 同簽章冷卻 |
| | `MaxEmailsPerHour` | `20` | 全域上限 |
| `LogRetentionSettings` | `ExceptionLogDays` | `90` | `0` 不自動清理（範圍 0～36500） |
| | `AuditLogDays` | `365` | `0` 不自動清理（範圍 0～36500） |
| | `TokenUsageLogDays` | `365` | Token 用量紀錄（0.9.96 起）；`0` 不自動清理（範圍 0～36500） |
| `SlowOperationSettings` | `HttpRequestMs` ／ `DbCommandMs` ／ `ExternalCallMs` ／ `AiCallMs` | `3000` ／ `1000` ／ `10000` ／ `60000` | `0` 停用該項（無 `UiInteractionMs`，見 LOG-21 調整） |
| `ClientErrorReporting` | `Enabled` | `true` | 前端錯誤回報開關 |
| | `MaxPerCircuitPerMinute` | `10` | 防濫用 |

## 八、權限與安全

- `/logs`、`/system-exceptions`、`/audit-logs`、`/ai-call-logs` 維持**管理員專屬**；本文件新增的畫面功能比照辦理。
- 告警信可能被轉寄或留在信箱，因此**只含摘要**（LOG-12），完整內容須登入系統查看。
- 前端回報入口只對已登入的 circuit 開放，並有頻率與長度上限（LOG-20）。
- 追蹤碼可以給使用者看，不得含帳號、主機名稱、時間戳以外的內部資訊。
- 日誌檔與堆疊檔可從 `/logs` 原始匯出，因此 §4.4 的禁記規則同樣適用於例外訊息以外的所有日誌行。

## 九、驗收與測試

| 需求 | 主要驗收方式 |
|---|---|
| §三、§四 規範 | `LoggingConventionTests`（自動）＋ Code Review 依本文件判斷等級 |
| LOG-01、LOG-03（0.9.77 已實作）、LOG-10、LOG-16（0.9.78 已實作） | `ApiIntegrationTests` 整合測試 |
| LOG-02、LOG-05（0.9.77 已實作） | `ExceptionLogProviderTests`、`ExceptionLogWriterTests` |
| LOG-04、LOG-06、LOG-07（0.9.77 已實作） | `ProcessExceptionHooksTests`、`CrashMarkerStoreTests`、`FormModalConventionTests`＋人工驗收（啟動失敗已實機驗證；浮層例外待瀏覽器手動重現） |
| LOG-08（0.9.77 已實作）、LOG-14、LOG-15（0.9.78 已實作） | 慣例測試＋各事件單元測試 |
| LOG-11（0.9.78 已實作） | `ExceptionLogServiceTests`、服務測試 |
| LOG-12、LOG-13（0.9.78 已實作） | 以假時鐘的單元測試（`ManualTimeProvider`） |
| LOG-20、LOG-21、LOG-22（0.9.79 已實作） | `BrowserErrorReporterTests`、`LoggingPipelineHealthTests`；瀏覽器內實際觸發 JS 錯誤、健康監控頁顯示仍需人工驗收 |

每一項實作完成後，須把該項從「規劃中」移到「已實作」（如 §6.2），並更新文件版本與現行系統版本。

## 十、已知限制與待決事項

- **Google SSO 使用者的 Account 就是 Email**：記 Account 等於記 Email，為既有且已接受的取捨（見操作文件 §2.4）。
- **稽核不記修改前後值**：只記變更了哪些欄位名稱；需要還原「從 A 改成 B」時須另立需求。
- **單機設計**：例外合併、告警節流、前端回報計數都在單一程序的記憶體中；未來若改為多台主機需重新設計。
- **時區**：例外紀錄與日誌檔使用伺服器本地時間，稽核使用 UTC（見稽核紀錄 PRD §四），跨表比對時須注意。
- **告警只有 Email**：Teams、Slack、LINE 等通道不在本次範圍。
- ~~**Web API（JWT）的紀錄存取範圍只看得到公開紀錄**~~（0.9.78 補日誌時發現；**0.9.105 已修正**）：原本 `RecordAccessScopeProvider` 以 `Sid` claim
  找使用者，但 JWT 的 UserId 放在 `NameIdentifier`，因此 API 使用者一律退回「非管理員、無團隊」。0.9.105 起改以 `RequestActorResolver`
  取出使用者 Id（Cookie 在 Sid、JWT 在 NameIdentifier）；已登入卻解析不到是誰時回 `RecordAccessScope.None`（什麼都看不到）並記 Warning。
- **Blazor 互動的追蹤碼、前端錯誤回報、浮層錯誤提示尚未在瀏覽器內人工驗證**：HTTP 請求的追蹤碼與慢操作日誌已實機確認；
  circuit 內的行為由程式碼與單元測試保證。
- **管線監控的數字是本次啟動以來的累計**，重啟歸零；也不會主動告警（例外告警本身失敗時無法再用告警通知）。

## 十一、相關程式與文件

- 程式：
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/nlog.config`
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/Program.cs`（NLog 初始化、啟動保護、中介軟體順序）
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/Diagnostics/`（`ExceptionLogProvider`、`ExceptionLogWriter`、`ExceptionContextAccessor`、`LogLevelRuntimeState`）
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/Extensions/ApplicationBuilderExtensions.cs`（`UseHttpRequestLogging`、`UseRejectInvalidHost`）
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/LoggingErrorBoundary.cs`、`Components/Routes.razor`、`Components/ApplicationCircuitHandler.cs`
  - `src/CustomerHelpAgent/CustomerHelpAgent.Web/Filters/ApiExceptionFilterAttribute.cs`、`Controllers/ControllerApiResponseExtensions.cs`
  - `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ExceptionLogService.cs`、`Services/Other/AuditLogService.cs`
  - `src/CustomerHelpAgent/CustomerHelpAgent.Models/Systems/ExceptionLogEntry.cs`（`ExceptionSources`）
  - `src/CustomerHelpAgent/CustomerHelpAgent.Tests/LoggingConventionTests.cs`
- 文件：
  - [日誌與設定檔說明](../operations/日誌與設定檔說明.md)
  - [開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（§6、§6.6）
  - [系統例外紀錄 PRD](系統例外紀錄-prd.md)、[稽核紀錄 PRD](稽核紀錄-prd.md)、[日誌檢視 PRD](日誌檢視-prd.md)、[日誌等級設定 PRD](日誌等級設定-prd.md)、[AI 對話紀錄 PRD](AI對話紀錄-prd.md)、[系統健康監控 PRD](系統健康監控-prd.md)、[寄信服務 PRD](寄信服務-prd.md)

> 返回 [PRD 主控台](README.md)
