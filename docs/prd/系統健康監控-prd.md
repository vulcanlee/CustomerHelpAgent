# 系統健康監控 PRD

- 文件版本：1.9
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供維運人員一個人工巡檢頁面（`/system-health`），以紅黃綠燈號與健康百分比快速判斷網站、API、資料庫、日誌、身分驗證、檔案系統、主機資源、安全設定、LLM API、快取服務、AI 計費表、寄信服務、日誌管線與排程作業是否正常，並附最近 24 小時 WARN 以上日誌、寄信測試與站內通知測試；另提供部署平台使用的機器可讀探針端點。

- 範圍：`/system-health` 巡檢頁、14 項健康檢查、加權計分與燈號、最近 24 小時 WARN 以上日誌、寄信測試（0.9.59 起）、站內通知測試（0.9.122 起）、`/health/live` 與 `/health/ready` 探針。
- 非範圍：健康狀態的告警通知／歷史趨勢、外部監控整合、自動修復（例外告警信屬「日誌與例外處理」LOG-12，不在本頁）。機制細節不重寫，見 [系統健康監控（機制）](../features/系統健康監控.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-health` | 系統管理 → 監控與診斷 → 系統健康監控（0.9.22 起上選單） | 已登入且為管理員（`IsAdmin`） | 系統管理員／維運 |
| `/health/live` | 非選單（探針） | 無（匿名） | 部署平台存活探針 |
| `/health/ready` | 非選單（探針） | 無（匿名） | 部署平台就緒探針 |

## 三、畫面與欄位

- 頂欄頁名旁的「操作說明」按鈕（0.9.66 起，`PageHelpDialog`）；內容為 `Datas/Help/system-health.md`，於 `Datas/HelpTopics.json` 登記 `/system-health`。
- 摘要區：整體燈號（綠／黃／紅）、健康百分比（`Score%`）、狀態文字（正常／警示／異常）、最後檢查時間。
- 檢查項目卡片（逐項）：名稱、類別、權重、狀態文字、燈號、佐證（Evidence）；異常時另顯示失敗訊息（FailureMessage）。
- 報告只在頁面初始化時產生一次；要重新檢查請重新整理頁面（每次都會重跑 14 項，含會產生費用的 LLM API）。
- 14 項檢查與權重（總計 150）：網站/應用程式(10)、API(10)、資料庫(25)、日誌(15)、身分驗證(15)、檔案系統(10)、主機資源(5)、安全設定(10)、LLM API(10)、快取服務(10)、AI 計費表(5)、寄信服務(10，0.9.59 起)、日誌管線(10，0.9.79 起，見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) LOG-22)、排程作業(5，0.9.96 起)。
  分數是「得分／總權重」，總權重由項目自行加總而非固定 100，因此新增項目會等比稀釋既有項目的佔比。
  權重由 `CustomerHelpAgent.Tests/SystemHealthTests.CheckWeights_ShouldSumTo150` 守住。
- **寄信服務**：`EmailSettings:Provider` 為 `None`（未啟用）或 `Pickup`（開發用）時黃燈；`Smtp` 時實際連線＋加密＋登入（上限 5 秒、不寄信），成功綠燈、失敗紅燈。佐證只顯示主機、埠號、加密方式，帳號與寄件者只顯示「已設定／未設定」，不顯示密碼。
- **寄信測試區**（0.9.59 起）：收件者輸入框（預填目前登入者的 Email）＋「寄出測試信」按鈕；Provider 為 `None` 時輸入框與按鈕停用並提示如何啟用。同步寄送，結果以綠／紅訊息顯示（失敗只顯示例外型別名稱，細節看日誌）。
- **站內通知測試區**（0.9.122 起）：「發送測試通知」按鈕，發一則「測試通知」給全體管理員（不寄信、不設 SourceKey 可重複測），連結回 `/system-health`；結果以綠／紅訊息顯示收件人數。
- ⚠️ **LLM API 這項每次載入頁面都會真的呼叫一次 API**，會產生費用並記入「Token 用量」
  （作業名稱「系統健康檢測」，約 27 token／NT$0.005 一次）與「AI 對話紀錄」（0.9.72 起），頁面載入也會因此多等 1～3 秒。
  逾時固定 30 秒，刻意不沿用 `AiSettings.TimeoutSeconds`（預設 600 秒）。AI 未設定時不發請求、該項黃燈。
- **日誌管線**（0.9.79 起）：佐證列出本次啟動以來的例外佇列長度／丟棄數、寫入失敗、前端錯誤回報被限流丟棄數、告警信失敗、NLog 內部錯誤與日誌磁碟可用空間；燈號規則見 [系統健康監控（機制）§4](../features/系統健康監控.md)。
- 日誌區（0.9.111 起）：標題「最近 24 小時警告以上日誌」，下方顯示查詢區間與筆數；以表格列出 WARN／ERROR／FATAL（時間、等級、記錄器、訊息、TraceId），新到舊、每頁 20 筆，展開列為深色等寬原文；無資料時顯示服務的原因（例如「指定條件下查無日誌紀錄。」），達 10,000 筆上限時黃色提示「僅顯示最新 10000 筆，完整內容請到「日誌檢視」查詢。」。0.9.110 以前為「最後 100 筆日誌紀錄」（今日檔尾端純文字）。

## 四、內部系統運作

1. `SystemHealthPage.OnInitializedAsync`：先 `AuthenticationStateHelper.Check` 驗證登入，再 `CheckIsAdmin`；非管理員設定 `roleMessage`、寫 `Permission.Denied` 稽核（0.9.78 起）並中止，不呼叫服務。
2. `ISystemHealthService.GetReportAsync`（`SystemHealthService`）依序執行 14 項檢查，各回傳 `SystemHealthItem`（狀態 Healthy/Degraded/Unhealthy）：
   - 應用程式：環境、`SystemVersion`（未設定→Degraded）、啟動時間與運作時長。
   - API：Controller action 數量（0→Unhealthy）、Swagger 依環境／設定。
   - 資料庫：`Database.CanConnectAsync`（false→Unhealthy）、待套用 migration 數（>0→Degraded）。
   - 日誌：目錄可寫入與今日檔案讀取筆數（不可寫→Unhealthy；可寫但無內容→Degraded）。
   - 身分驗證：Cookie 與 JWT scheme 是否註冊、JWT 設定完整度、Production 是否仍用開發用 key。
   - 檔案系統：資料庫／下載／上傳／各附件目錄存在且可寫入。
   - 主機資源：Working set 與磁碟可用空間（<1GB→Degraded）。
   - 安全設定：Production 是否開啟 Swagger 或回傳例外細節（風險→Degraded）。
   - LLM API：`IAiHealthProbe` 送一句 `hello`（30 秒上限）；未設定→Degraded，失敗→Unhealthy。
   - 快取服務：寫一筆 sentinel 再讀回比對（5 秒上限）；Provider 無法解析或拋例外→Unhealthy，讀回不符→Degraded。
   - AI 計費表：AI 未啟用→Healthy；匯率 ≤ 0 或目前模型查無價格（沿用 `AiUsageCostCalculator.Resolve`）→Degraded。
   - 寄信服務：依 Provider 判斷；Smtp 走 `IEmailHealthProbe`（連線＋TLS＋登入，5 秒上限，永不拋例外）。
   - 日誌管線：讀 `LoggingPipelineMonitor` 快照、例外佇列長度與 `NLog:BasePath` 所在磁碟，由純函式 `EvaluateLoggingPipeline` 判定（佇列滿或磁碟 < 200 MB→Unhealthy；任何丟棄／寫入或告警失敗／NLog 內部錯誤、佇列達八成、磁碟 < 1 GB→Degraded）。
   - 排程作業（0.9.96 起）：讀 `ScheduledJobOverviewService` 的總覽，由 `EvaluateScheduledJobs` 判定（啟用中的作業最近一次執行失敗→Unhealthy；排程總開關關閉、作業逾期未執行、排程設定有誤→Degraded；讀取失敗→Unhealthy）。
3. 計分（`SystemHealthScoreCalculator`）：以權重加權，Healthy 計滿分、Degraded 計半、Unhealthy 計 0；`Score = round(earned/totalWeight*100)`。
4. 燈號門檻：`Score >= 90` 綠、`>= 70` 黃、其餘紅；狀態文字同門檻映射正常／警示／異常。
5. 日誌區：報告取得後，頁面另呼叫 `ILogQueryService.QueryAsync`（`StartTime = now − 24h`、`MinimumLevel = Warn`、`Take = LogQueryRequest.MaxTake`），結果反轉為新到舊。與報告分開，讀取失敗只影響本區。「日誌」檢查項目仍以 `IHealthLogReader.ReadLatestLines(100)` 判斷目錄可寫與今日檔狀態。
5.1 寄信測試：`EmailTestService.SendAsync` 驗證收件者格式後以 `IEmailSender` 同步寄出（不走背景佇列），並寫稽核 `Email.Test`（detail 只有 provider 與成敗／例外型別，**不含收件者**）。
5.2 站內通知測試：`NotificationTestService.SendAsync` 經 `INotificationSender` 發給 `NotificationTarget.AllAdmins()`（分類 `NotificationCategories.Test`），寫稽核 `Notification.Test`（detail 收件人數）；沒有管理員或發送失敗回失敗訊息。
6. 探針：`/health/live` 對應 tag `live`（`self` 檢查恆 Healthy）；`/health/ready` 對應 tag `ready`（`DatabaseHealthCheck` 檢查資料庫連線）。

## 五、權限與安全

- 巡檢頁為管理員專屬：非管理員顯示「您沒有權限檢視系統健康監控。」並記錄警告與 `Permission.Denied` 稽核，不揭露任何檢查內容。
- 0.9.22 起列入側邊選單（`Menu.json` id 65，權限鍵 `角色_系統健康監控`，與整支系統管理同為管理員專屬、不上架角色矩陣）。
  在此之前刻意不列選單、需自行輸入網址；改列選單只改變管理員的「可發現性」，權限判定仍是頁面內的 `CheckIsAdmin()`，非管理員看不到選單項、直接輸入網址也會被擋。
- 需登入且 `IsAdmin` 才可讀取報告；管理員豁免一如全站 RBAC 慣例。
- 佐證訊息對敏感值採遮蔽：JWT Issuer／Audience 僅顯示「已設定／未設定」，SigningKey 僅顯示長度；SMTP 帳號與寄件者僅顯示「已設定／未設定」，密碼不出現。
- 探針端點匿名可存取，僅回傳存活／就緒狀態，不含詳細佐證。

## 六、錯誤與邊界

- 報告載入前顯示「正在讀取系統健康狀態...」。
- 資料庫、LLM API、快取服務、排程作業檢查擲例外時降級為 Unhealthy 並記錄例外型別，不使頁面崩潰；寄信服務由 probe 內部吞例外。其餘早期項目沒有個別防護（見機制文件 §4.1）。
- 最近 24 小時沒有日誌檔或沒有 WARN 以上紀錄時，日誌區顯示服務回傳的原因；讀取擲例外時記錄錯誤並顯示「讀取日誌失敗：…」，不影響上方檢查結果。
- 任一子項降級／異常僅影響其權重計分與整體燈號，其餘項目仍照常呈現。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/SystemHealthTests.cs`：
  - `CalculateScore_AllHealthy_ShouldReturnGreen100`、`CalculateScore_DegradedRange_ShouldReturnYellow`、`CalculateScore_UnhealthyRange_ShouldReturnRed`（計分與燈號門檻）。
  - `GetLight_ItemStatus_ShouldMapTrafficLight`（狀態→燈號映射）。
  - `HealthLogReader_ReadLatestLines_ShouldReturnLast100Lines`、`HealthLogReader_MissingFile_ShouldReturnDegraded`（「日誌」檢查項目的尾端讀取與缺檔降級）。
  - 日誌區的查詢條件由 `LogQueryServiceTests`（等級篩選、跨日檔案合併）守住；`ApiIntegrationTests.SystemHealthPage_WithoutCookieLogin_ShouldNotExposeDetails` 確認匿名存取看不到「最近 24 小時警告以上日誌」。
  - `CheckWeights_ShouldSumTo150`（14 項名稱與權重、總和 150）。
- `CustomerHelpAgent.Tests/LoggingPipelineHealthTests.cs`：`Evaluate_AllClear_ShouldBeHealthy`、`Evaluate_ClientErrorDropsOnly_ShouldStayHealthy`、`Evaluate_AnyPipelineProblem_ShouldDegrade`、`Evaluate_QueueFullOrDiskAlmostFull_ShouldBeUnhealthy`（日誌管線燈號）。
- `CustomerHelpAgent.Tests/AiHealthProbeTests.cs`：未設定不呼叫、成功記 Token 用量與 AI 對話紀錄、失敗不拋例外且不外洩上游內容。
- `CustomerHelpAgent.Tests/ApiIntegrationTests.cs`：`/health/ready`、`/health/live` 探針回應。
- `CustomerHelpAgent.Tests/EmailHealthProbeTests.cs`：SMTP 連不上時回報失敗、不拋例外、在上限內結束。
- `CustomerHelpAgent.Tests/EmailTestServiceTests.cs`：None 拒絕、收件者無效拒絕、成功稽核不含收件者、失敗回型別名並稽核。
- `CustomerHelpAgent.Tests/NotificationTestServiceTests.cs`（0.9.122 起）：發給全體管理員、不寄信、可重複並寫稽核；沒有管理員、發送失敗時回失敗。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/SystemHealthPage.razor`（`OnInitializedAsync` 的管理員守門、`GetStatusText`／`GetLightClass` 的狀態文字與燈號樣式）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/SystemHealthService.cs`（`GetReportAsync` 與 14 項檢查、`EvaluateLoggingPipeline`、`EvaluateScheduledJobs`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Ai/AiHealthProbe.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Web/Diagnostics/LoggingPipelineMonitor.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/Help/system-health.md`（頁面操作說明）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/SystemHealthScoreCalculator.cs`（計分與燈號門檻）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/SystemHealthModels.cs`（報告與項目模型）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/HealthLogReader.cs`、`DatabaseHealthCheck.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Email/EmailHealthProbe.cs`、`EmailTestService.cs`（寄信服務項與寄信測試）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/NotificationTestService.cs`（站內通知測試）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Extensions/ServiceCollectionExtensions.cs`（`AddConfiguredHealthChecks`，探針 tag `live`/`ready`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Program.cs`（`MapHealthChecks` `/health/live`、`/health/ready`）
- 交叉連結：[系統健康監控（機制）](../features/系統健康監控.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)、[寄信服務 PRD](寄信服務-prd.md)
