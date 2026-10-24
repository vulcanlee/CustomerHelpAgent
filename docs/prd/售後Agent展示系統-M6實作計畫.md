# 售後 Agent 展示系統 M6 Implementation Plan（效益對照頁）

- 文件版本：0.2
- 文件狀態：已完成（M6 於 1.0.8 完成）
- 現行系統版本：1.0.8
- 首次實作版本：1.0.8
- 最後核對日期：2026/10/24

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

M5（多 Agent 交接＋長期記憶，1.0.7）已合併到 main（`e727fb0`）。M6 是最後一個里程碑：做一頁「效益對照」，把**傳統表單、單一 Agent、多 Agent** 三種做法在同一個情境下的數字並排呈現，讓展示者跑完劇本後切過來，讓客戶一眼看到差異與節省的成本（設計 §4.8、§6 M6）。

**使用者已決定：**

| 項目 | 決定 |
|---|---|
| 給誰看 | 兩者都要：上半部是展示用的大數字與對照圖，下半部可展開明細表 |
| 數字怎麼取 | 依劇本分組，可選時間範圍（預設今天）；回放執行一律排除 |
| 假設值 | 在效益頁直接調整並儲存；以不同底色標示「假設」與「實測」 |
| 呈現方式 | 並排指標卡（大數字＋差異百分比）、長條圖對照、月度推估、Excel／PDF 匯出 |

**探索結論**（影響設計的事實）：

1. **劇本執行沒有被標記**。「準備此劇本」只寫了日誌，所以要新增「劇本執行」紀錄，並在埋點時蓋上執行編號。
2. **示範重置會刪掉申請、初審、處理單**，但埋點、執行紀錄、Token 用量、AI 對話紀錄都會保留。所以所有數字一律從「保留的資料」計算：
   - `ProcessMetric`
   - `AgentRunTrace`
   - `AiCallLog`＋`TokenUsageLog`
3. **`TokenUsageLog` 沒有 `ConversationId`**。每案成本要透過 `AiCallLog.CallId` 去關聯，`AgentRecordingService.SumCostAsync` 已有可沿用的寫法。客服 AI 助手的呼叫無法關聯到案件，所以不計入每案成本，只在頁面註明。
4. **沒有圖表套件**。長條圖沿用 Token 用量頁的手寫 SVG 作法（`TokenUsageTrendChart`）。
5. **沒有傳統流程的劇本**。要新增傳統版劇本，才能做到「同一情境三種做法」並排。

**時間預估**（開發歷程 §十三公式；本里程碑沒有第一次使用的框架）：

| 項目 | 預估時間 |
|---|---|
| 規劃 | 約 30 分 |
| 5 個任務 | 約 2.5～3 小時 |
| 最終審閱 | 約 40 分 |
| 文件 | 約 30 分 |
| 實機驗證 | 約 30 分 |
| **合計** | **約 4.5～5.5 小時** |

**Goal:** `/benefit-comparison` 效益對照頁具備以下內容，並全部通過品質關卡與實機驗證：
- 依情境並排傳統／單一 Agent／多 Agent 的實測數字
- 可調整並儲存的假設值與月度推估
- Excel／PDF 匯出

**Architecture:**
- **資料蒐集**：新增 `DemoScenarioRun`。展示控制台「準備此劇本」時建立一筆執行，`ProcessMetric` 寫入時蓋上目前的執行編號。
- **計算**：Business 層新增 `BenefitMetricsService`，從保留的資料算出每次執行的指標，再依情境（家族）× 做法（流程）彙總。
- **假設值**：單列實體 `DemoAssumption`。
- **月度推估**：純函式計算，並有對應的測試。
- **頁面**：沿用後台頁慣例；圖表用 SVG；匯出沿用 `TabularExport`（Excel）與既有的 PDF 產生器。

**Tech Stack:** .NET 10、Blazor Server、AntDesign、EF Core SQLite、xUnit＋bUnit（不新增套件）。

**Spec:** `docs/prd/售後Agent展示系統-設計.md` §4.8、§6 M6。驗證標準是：以種子劇本跑兩種流程後數字合理，且 AI 成本與 Token 用量頁一致。企劃規劃說明書 §5.5 要求「明說哪些是實測、哪些是假設」。

## Global Constraints

- 沿用 M1～M5 全部慣例（權威：`docs/architecture/開發慣例與限制速查.md`，含 §6.30、§6.31）：
  - **程式**：分層、`IDbContextFactory` 單一建構子、英文日誌不含個資、`AuditActions` 常數。
  - **畫面**：色票只在 `theme.css :root` 別名；子元件用自己的 scoped CSS。
  - **說明與文件**：操作說明七段式；文件一律 UTF-8 含 BOM。
- **時間**：一律用 `TimeProvider.GetLocalNow().DateTime`。不新增套件。
- **Git**：不自動 commit；版本在 M6 結束時從 `1.0.7` 改為 `1.0.8`。
- **回放一律排除**：符合以下任一條件的執行或案件，一律不算入：
  - 該次執行建立時是回放模式；
  - 該案件的任何 `AgentRunTrace.ReplayRecordingId` 有值。
- **實測與假設分開**：
  - 實測值只來自系統紀錄。
  - 假設值與由假設算出的推估一律標示「假設／推估」並用不同底色。
  - 匯出檔也要同樣標示。
- **成本口徑一致**：每案 AI 成本 = 該案 CaseKey 下所有呼叫的 `TokenUsageLog.CostTwd` 加總，包含顧客 Agent、多 Agent 與 M4 初審，經由 `AiCallLog.ConversationId` 關聯。同一時間範圍、同一操作類型的總和必須與 Token 用量頁一致。
- **頁面不顯示個資**：不顯示顧客姓名、帳號、地址、手機，只顯示示範帳號代號與申請編號。

## Review Focus

1. **回放執行被算進平均** → 數字失真；回放的 AI 成本是 0，會讓 AI 看起來不花錢。
   → Task 3 測試 `Replay_RunsAndCases_Excluded`。
2. **傳統表單的「填寫細節」每改一個欄位就記一次** → 步數被灌水，對照不公平。
   → Task 3 測試 `CustomerSteps_CollapseConsecutiveFillDetail`。
3. **示範重置刪掉申請之後** → 舊的執行數字不能消失或變成 0。
   → Task 3 測試 `Metrics_SurviveDemoReset`。
4. **AI 成本與 Token 用量頁不一致**，例如漏掉初審呼叫，或重複計算。
   → Task 3 測試 `AiCost_MatchesTokenUsageSummary`。
5. **假設值被改成負數、0 或極端值** → 推估出現無意義或除以零的結果；改值也要有權限控管。
   → Task 2 測試 `Assumption_InvalidValues_Rejected`；Task 4 測試 `Assumptions_EditRequiresPermission`。

---

### Task 1: 劇本執行與埋點標記

**Files**
- Create：
  - `AccessDatas/Models/Demo/DemoScenarioRun.cs`
  - migration `AddDemoScenarioRun`
  - `Business/Services/DataAccess/DemoScenarioRunService.cs`
- Modify：
  - 劇本與展示控制：`Share` 或 `Business/Startup/DemoScenarios.cs`（`Family`、`Flow`、新劇本）、`DemoControlService`（`ActiveRunId`）、`DemoConsoleView`（準備劇本時建立執行；傳統劇本導向售後申請表單）
  - 埋點：`ProcessMetric`（加 `DemoRunId?` 欄位）、`ProcessMetricService.RecordAsync`（示範模式且有目前執行時蓋上編號）
  - 修正：`ShopAfterSalesListView`（「重新送件」改用申請本身的管道，原本寫死傳統）
- Test：`DemoScenarioRunTests.cs`、`DemoConsoleViewTests`、`ProcessMetricServiceTests`

**Interfaces**
- `enum ScenarioFlow { Traditional = 1, SingleAgent = 2, MultiAgent = 3 }`
- `DemoScenario` 加兩個欄位：`Family`（情境家族，例如「換尺碼」「一句兩事」「退貨」「物流催件」）與 `Flow`。
- 新增劇本：
  - **A-T**：傳統表單換碼，customer1 自己填表。
  - **B-T**：傳統表單一句兩事，要填兩張表。
  - **A-M**：多 Agent 換碼。
- 既有劇本的家族與流程：
  - A：換尺碼／單一
  - B：一句兩事／單一
  - H：一句兩事／多 Agent
  - C、D、E、F 系列：各自的家族，不參與並排比較的部分，頁面以「其他」列出
- `DemoScenarioRun { Id, ScenarioCode, Family, Flow, Account, IsReplay, StartedAt }`
- `DemoControlService.ActiveRunId`：準備劇本時設定。重置時清空，但下一次準備劇本會先重置再設定新的執行，所以不衝突。
- `ProcessMetricService.RecordAsync` 簽章不變，內部自動蓋上 `DemoRunId`。
- 重置保留 `DemoScenarioRun`。

**Steps**
- [ ] Step 1：寫失敗測試：
  - `PrepareScenario_CreatesRun_AndStampsMetrics`
  - `PrepareTraditionalScenario_NavigatesToForm`
  - `ReplayMode_RunMarkedIsReplay`
  - `Resubmit_UsesRequestChannel`
  - `Reset_KeepsRuns_ClearsActiveRun`
- [ ] Step 2～4：確認測試失敗 → 實作（含 migration）→ 測試通過
- [ ] Step 5：檢查點

### Task 2: 假設值

**Files**
- Create：
  - `AccessDatas/Models/Demo/DemoAssumption.cs`（單列，Id = 1）
  - migration `AddDemoAssumption`
  - `Business/Services/DataAccess/DemoAssumptionService.cs`
- Modify：`AuditActions`（`Demo.AssumptionChanged`）、DI、`DataAccessServiceLifetimeTests`
- Test：`DemoAssumptionServiceTests.cs`

**Interfaces**
- 欄位與預設值如下，預設值放在常數，文件也要寫明：

  | 欄位 | 意思 | 預設 |
  |---|---|---|
  | `QueueWaitMinutesTraditional` | 傳統管道排隊等審核的時間 | 240 |
  | `QueueWaitMinutesAi` | AI 管道排隊等審核的時間 | 30 |
  | `ContactsPerCaseTraditional` | 傳統管道每案平均來回聯繫次數 | 2.0 |
  | `ContactsPerCaseAi` | AI 管道每案平均來回聯繫次數 | 0.5 |
  | `MinutesPerContact` | 每次聯繫花的客服分鐘數 | 5 |
  | `StaffCostPerMinuteTwd` | 客服每分鐘成本（新台幣） | 8 |
  | `MonthlyCaseVolume` | 每月案件量 | 1000 |

  另有 `UpdatedAt`、`UpdatedByUserId`。
- `GetAsync()` 沒有資料時回傳預設值。
- `SaveAsync(DemoAssumptionInput)` 回傳結果物件。驗證規則：
  - 分鐘與次數必須 ≥ 0，且有合理上限：分鐘 ≤ 10080，次數 ≤ 50。
  - 成本必須 ≥ 0。
  - 月案件量介於 1 ～ 1,000,000。
- 儲存時寫入稽核。重置不刪除假設值。

**Steps**
- [ ] Step 1：寫失敗測試：
  - `Get_ReturnsDefaultsWhenEmpty`
  - `Save_Persists_AndAudits`
  - `Assumption_InvalidValues_Rejected`
- [ ] Step 2～4：確認測試失敗 → 實作（含 migration）→ 測試通過
- [ ] Step 5：檢查點

### Task 3: 指標計算服務

**Files**
- Create：
  - `Business/Services/DataAccess/BenefitMetricsService.cs`
  - `CustomerHelpAgent.Models/Benefit/*`（檢視模型）
  - `Business/Helpers/BenefitEstimator.cs`（純函式）
- Test：`BenefitMetricsServiceTests.cs`、`BenefitEstimatorTests.cs`

**Interfaces**
- `GetComparisonAsync(DateTime from, DateTime to)` 回傳 `BenefitComparison`：
  - **依家族的並排結果**：每個家族、每種流程一筆 `FlowMetrics`，內容是該流程所有有效執行的平均，並附執行次數。
  - **「其他／未分類」**：示範模式外的案件，依管道彙總。
- 每次執行的 `RunMetrics`：
  - **顧客操作步數**：該執行的 Customer 埋點筆數；連續的 `FillDetail` 合併成 1 步。
  - **客服操作步數**：Staff 埋點筆數。
  - **端到端分鐘數**：從第一筆顧客操作到第一筆客服決策（核准／駁回／要求補件）的時間。沒有決策時標「未完成」，不列入平均。
  - **客服處理分鐘數**：從客服 `OpenRequest` 到決策的時間。
  - **AI 成本（新台幣）**：該執行所有 CaseKey 的成本，經 `AiCallLog` 關聯計算，沿用 `SumCostAsync` 的作法。
  - **模型呼叫次數**：由 `TokenUsageLog` 筆數計算，不用 `ModelCallCount`，因為回放也會累加後者。
  - **Agent 停下詢問次數**：`AgentRunTrace.StopReason == AwaitingUserReply`。
  - **等待上傳次數**：`AwaitingPhotoUpload`，另計。
  - **申請筆數**：由埋點 `Submit`／`AgentConfirm` 計算，因為申請表在重置後可能已被刪除。
- 回放排除：該執行 `IsReplay`，或其任一 CaseKey 的 trace 有 `ReplayRecordingId`，就整筆排除，並在明細中標示「回放（不計）」。
- `BenefitEstimator.Estimate(FlowMetrics traditional, FlowMetrics ai, DemoAssumption a)` 回傳 `MonthlyEstimate`：
  - 每案客服分鐘 = 實測客服處理分鐘 ＋ 來回聯繫次數 × 每次分鐘
  - 每月節省客服時數 = （傳統每案 − AI 每案）× 月案件量 ÷ 60
  - 每月節省成本 = 節省分鐘 × 每分鐘成本 − AI 每案成本 × 月案件量
  - 顧客等待（端到端）= 實測端到端 ＋ 排隊等待假設
  - 任一邊沒有資料時，回傳「資料不足」，不得除以零。

**Steps**
- [ ] Step 1：寫失敗測試：
  - `CustomerSteps_CollapseConsecutiveFillDetail`
  - `Replay_RunsAndCases_Excluded`
  - `Metrics_SurviveDemoReset`
  - `AiCost_MatchesTokenUsageSummary`（同範圍內，與 `TokenUsageLogService.GetSummaryAsync` 依操作類型的加總一致）
  - `EndToEnd_UndecidedRunExcludedFromAverage`
  - `StopToAsk_CountsAwaitingUserReply`
  - `Estimator_InsufficientData_NoDivideByZero`
  - `Estimator_FormulaMatchesSpec`
- [ ] Step 2～4：確認測試失敗 → 實作 → 測試通過
- [ ] Step 5：檢查點

### Task 4: 效益對照頁

**Files**
- Create：
  - `Components/Pages/AfterSales/BenefitComparisonPage.razor`（`/benefit-comparison`）
  - `Components/Views/AfterSales/BenefitComparisonView.razor(.cs/.css)`
  - `Web/Benefit/BenefitBarChart.cs`（SVG 長條圖純函式，比照 `TokenUsageTrendChart`）
  - `Datas/Help/benefit-comparison.md`（含 BOM，七段式）
- Modify：
  - 選單與權限：`Menu.json`（售後服務群組，id 94，icon `insights`）、`SidebarMenuService.MenuPermissionMap`、`MagicObjectHelper`（`角色_效益對照`）、`RolePermissionService`（售後服務群組）
  - 角色與說明：`DemoSeedData.Roles`（店家管理者可看）、`HelpTopics.json`
  - 測試清單：`MenuIconTests.AllowedIcons`、`MenuPermissionConsistencyTests`
- Test：`Components/BenefitComparisonViewTests.cs`、`BenefitBarChartTests.cs`

**Behaviour**
- **上半部（展示用）**：
  - 篩選列：日期範圍，預設今天；另有「自上次重置以來」的快速選項。
  - 依情境家族分區，每區有：
    - 三欄指標卡：傳統、單一 Agent、多 Agent，每張卡顯示大數字，並標示相對傳統的差異百分比，例如「顧客步數 9 → 2（−78%）」。
    - 長條圖：每個指標一組，三色依序代表三種流程。
  - 缺少某個流程的資料時，顯示「尚無資料（請在展示控制台準備劇本 X）」。
- **假設值與月度推估**：
  - 假設值區以專屬底色標示「假設」。
  - 有 `RequirePermission Edit` 權限才能修改；改值後立即重算，儲存時跳出確認。
  - 月度推估卡標示「推估（依假設值）」。
- **下半部（明細）**：可展開，列出每次執行的編號、劇本、流程、時間、各指標、是否回放（回放標「不計」）。
- **不顯示**：顧客個資。
- **說明**：操作說明第三段寫按鈕與欄位，並交代「實測與假設」的意思。

**Steps**
- [ ] Step 1：寫失敗測試：
  - `Page_RequiresPermission`
  - `Cards_ShowDeltaPercent`
  - `MissingFlow_ShowsPrepareHint`
  - `Assumptions_EditRequiresPermission`
  - `Assumptions_Change_RecomputesEstimate`
  - `Detail_ReplayRunMarkedNotCounted`
  - `BarChart_ScalesAndLabels`
- [ ] Step 2～4：確認測試失敗 → 實作 → 測試通過（含 `PageAuthorizationTests`、`PageHelpCatalogTests`、`MenuPermissionConsistencyTests`、`ThemeConventionTests`）
- [ ] Step 5：檢查點

### Task 5: 匯出（Excel／PDF）

**Files**
- Modify：`BenefitComparisonView`（加匯出按鈕）
- Create：`Web/Export/BenefitExport.cs`
- Test：`BenefitExportTests.cs`

**Behaviour**
- **Excel**：沿用 `TabularExport` 與 `BusinessExports` 的模式，產生三張工作表：
  - 對照摘要
  - 明細
  - 假設值與推估
- **PDF**：沿用專案既有的 PDF 產生器（先確認 `AiMarkdownPdfRenderer` 是否適用；用法見通用匯出 PRD 與 AI 分析匯出）。內容是對照表、推估，並註明「實測／假設」。
  - 若既有產生器無法呈現表格，改以 Markdown 表格轉 PDF，並在報告中說明。
- **兩種格式共通**：檔名含日期範圍；檔頭寫明資料範圍、回放已排除、AI 成本口徑；不含個資；匯出寫入稽核（沿用既有匯出的稽核碼）。

**Steps**
- [ ] Step 1：寫失敗測試：
  - `Excel_HasThreeSheets_AndAssumptionLabels`
  - `Pdf_ContainsComparisonAndAssumptionNote`
  - `Export_NoPersonalData`
- [ ] Step 2～4：確認測試失敗 → 實作 → 測試通過
- [ ] Step 5：檢查點

### Task 6: 文件、品質關卡、實機驗證、開發歷程

**Files**
- `appsettings.json`：版本 `1.0.7 → 1.0.8`
- 功能說明 `docs/features/售後Agent示範系統.md`：新增 §9 M6，涵蓋：
  - 指標定義與口徑
  - 回放排除
  - 假設值與公式
  - 匯出
  - 已知限制：客服 AI 助手成本無法歸到案件；AI 對話紀錄保留 90 天，超過之後無法再計算該期間的每案成本
- 設計文件：補 M6 完成與差異註記。
- 企劃規劃說明書：補 §5.5 的效益頁展示段落與驗證步驟；企劃書：M6 改為〔已提供〕。
- `docs/features/售後Agent示範系統-MAF功能運用說明.md`：註明 M6 沒有新增 MAF 功能，並說明原因（純資料彙總）。
- 其他：資料模型、畫面欄位、稽核 PRD、速查表、索引、M6 計畫文件頭。
- **開發歷程**（知識手冊）：
  - 新增第九章之後的 M6 階段章節，章節編號依現有結構。
  - 審閱發現小節：寫明全部發現、怎麼找到、影響、怎麼修。
  - 時間對照。
  - 經驗法則：「效益數字要可信，就要先把實測與假設分開」。
  - 全系統完成的總結。

**Steps**
- [ ] Step 1：更新文件（UTF-8 含 BOM）
- [ ] Step 2：`pwsh ./scripts/Invoke-QualityGate.ps1` → 結果須為 `QUALITY GATE PASSED`
- [ ] Step 3：實機驗證（暫時性 Live 測試驅動真實服務，跑完刪除；金鑰不得寫出）：
  1. 準備劇本 A-T，以傳統表單送出；客服核准。
  2. 準備劇本 A（單一 Agent）即時跑完；客服核准。
  3. 準備劇本 A-M（多 Agent）即時跑完；客服核准。
  4. 回放一次 B 金劇本，確認不計入。
  5. 檢查效益服務的數字：三種流程都有數據；回放被排除；AI 成本與 Token 用量頁（同範圍、同操作類型）一致；月度推估有值且標示為推估。
  6. 匯出 Excel 與 PDF 各一次並檢查內容。
- [ ] Step 4：檢查點：回報結果，並提供建議 commit 訊息「M6：效益對照頁、劇本執行標記、假設值與匯出；版本 1.0.7 → 1.0.8」

---

## Claude 代為決定的事項（請檢查）

1. **新增「劇本執行」紀錄並在埋點蓋上編號**：這樣才能「依劇本分組」。重置不會刪除這些紀錄，舊的展示數字仍然看得到。
2. **新增傳統版劇本**：A-T 換碼、B-T 一句兩事。另新增 A-M（多 Agent 換碼），讓兩個情境都有三種做法可以並排。
3. **所有指標都從重置後仍保留的資料計算**，因為重置會刪掉申請表。
4. **傳統表單連續的「填寫細節」合併成 1 步**，避免每改一個欄位就多算一步。
5. **模型呼叫次數改用 Token 用量筆數**，不用執行紀錄裡的次數，因為回放也會累加後者。
6. **假設值比設計多 3 個欄位**：排隊與聯繫次數各分傳統／AI 兩值，另加「每次聯繫分鐘數」。這樣月度推估的公式才完整。預設值如 Task 2 的表格，可在頁面上修改。
7. **客服 AI 助手的成本不計入每案成本**：它的呼叫無法對應到案件，頁面會註明。
8. **頁面放在「售後服務」群組**：管理員與店家管理者可以看，有編輯權限的人才能改假設值。
9. **長條圖用手寫 SVG**：與 Token 用量頁相同作法，不新增套件。

## 執行方式

沿用 **Subagent-driven**：新分支 `feature/m6-benefit`，以 git index 作檢查點，不自動 commit。

## 驗證總結

- **自動**：各任務的測試（Review Focus 五項都有對應測試），以及品質關卡。
- **實機**：Task 6 Step 3。
- **計畫存檔**：核准後另存 `docs/prd/售後Agent展示系統-M6實作計畫.md`（含 BOM）並列入索引。
