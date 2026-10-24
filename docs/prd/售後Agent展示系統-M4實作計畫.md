# 售後 Agent 展示系統 M4 Implementation Plan（客服後台 AI）

- 文件版本：0.2
- 文件狀態：已完成（M4 於 1.0.6 完成）
- 現行系統版本：1.0.7
- 首次實作版本：1.0.6
- 最後核對日期：2026/10/24

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

M3（展示控制台、故障模擬、錄製回放、身分切換，版本 1.0.5）已合併到 main（`d073282`）。M4 要讓「客服後台也有 AI」：
- **AI 管道**（顧客透過線上客服 Agent）送進來的申請，一送出就在背景跑「申請初審 Workflow」。流程：進件 → 三路併行（照片初審 Agent、規則核對程式、歷史紀錄 Agent）→ 摘要 Agent → **人工決策** → 執行 → 通知顧客。
- 客服在審核頁看到流程圖逐格亮起，以及 AI 的初審建議；**決定權永遠在人**。
- 客服另有只讀的「客服 AI 助手」。轉人工時會自動帶上顧客端 Agent 已經查過的內容。

M4 要對照兩件事：一是 Workflow 的「固定流程較穩」，對上顧客端 Agent 的「模型看情況決定」；二是傳統表單管道維持純人工。

使用者已決定：
- **核准即執行**：產生處理單（換貨單／退款紀錄／補寄單／催件紀錄）＋扣庫存＋通知顧客，全部模擬。
- **通知方式**：商店版面加通知鈴（顯示未讀數），「我的申請」顯示最新處理結果。
- **初審時機**：申請一送進來就自動在背景跑，流程圖即時亮燈，重啟後從 checkpoint 續跑。

**時間預估**（依開發歷程紀錄 §7 公式）：規劃 15–30 分＋9 個任務 × 20–30 分＋最終審閱 20–40 分＋實機 20–40 分 ≈ **4.5–6 小時**。M3 實際 4.3 小時（10 個任務），M4 任務比 M3 少，但 Workflows 套件是第一次用、視覺與結構化輸出要實機驗證，所以取偏高的估算。

**Goal:** 申請初審 Workflow（fan-out／fan-in、人工 request/response 節點、SQLite checkpoint 續跑）＋核准執行與顧客通知＋客服 AI 助手，全部通過品質關卡並實機驗證。

**Architecture:**
- **Workflow：**
  - 以 `Microsoft.Agents.AI.Workflows` 1.25.0 組成。節點用非 obsolete 的 `Executor<TIn,TOut>`／`Executor<TIn>` 基底類別，因此**不引用** Generators 套件。
  - 三個 AI 節點透過介面注入，方便測試換成假 Agent。
  - 人工節點是 `RequestPort<DecisionRequest, StaffDecision>`。
  - checkpoint 存在自訂的 `SqliteWorkflowCheckpointStore : JsonCheckpointStore`。
- **背景執行與續跑：**
  - 由 Channel 佇列＋`BackgroundService` 執行。
  - 啟動時掃描未完成的初審，從最新 checkpoint 續跑。
  - 客服決策由 Web 層的協調器處理：有等待中的 Workflow 就送回 response 讓它續跑，否則直接走同一個決策服務。
- **執行與通知（純程式）：** 放在 Business 層的 `AfterSalesDecisionService`。傳統管道核准也走它，差別只在有沒有 AI。

**Tech Stack:** .NET 10、Blazor Server、AntDesign、EF Core SQLite、MAF 1.25（Agents.AI、Agents.AI.Workflows）、MEAI 10.10、xUnit＋bUnit。

**Spec:** `docs/prd/售後Agent展示系統-設計.md`：
- §4.6（客服後台 AI）
- §3.1（以管道切分、錄製／回放涵蓋 Workflow 內 Agent、視覺模型可獨立設定）
- §6 M4 驗證：假 Agent 測併行節點、人工決策前不執行、重啟從 checkpoint 續跑

另參考專案記憶 `project-m2-hardening-backlog`。

## Global Constraints

- **沿用 M1–M3 全部慣例**（權威是 `docs/architecture/開發慣例與限制速查.md`）：
  - 分層與資料存取：分層、`IDbContextFactory` 單一建構子、`OrderBy…ThenBy(Id)`。
  - 日誌與稽核：英文日誌，不含姓名／地址／手機；`AuditActions` 常數。
  - UI：BlazorMaterialIcons、`ViewNotification`、`ConfirmDialog`、Modal 規則；色票只在 `theme.css :root`（別名層）。
  - 文件：UTF-8 含 BOM。
  - 新設定段落要符合 `DocumentationConventionTests`。
- **時間：** 一律用 `TimeProvider.GetLocalNow().DateTime`。
- **套件：** 只寫 `Directory.Packages.props`，釘 `Microsoft.Agents.AI.Workflows` **1.25.0**（不升 1.26）。
- **不自動 commit：** 實作者不得 `git add`／`git commit`；controller 以 git index 作檢查點。
- **版本：** M4 結束時 `1.0.5 → 1.0.6`。
- **傳統表單管道零 AI：** 不跑初審、不顯示 AI 面板與助手。核准後的執行與通知兩個管道相同。
- **AI 永不寫入：** Workflow 的 AI 節點、摘要、助手都只讀；只有「人工決策 → 執行」能寫入，而且執行是純程式、狀態有守門（`ExecuteUpdateAsync` 條件更新）。
- **模型看不到** 姓名、帳號、Email、地址、手機、使用者 Id，顧客一律稱「此顧客」。照片只送給照片初審節點。
- **AI 失敗不擋人工：** 每個 AI 節點自己吞下錯誤，回傳 `Failed` 結論；Workflow 一定會走到人工決策。
- **背景模型呼叫：** worker scope 的 `CurrentUserService.CurrentUser` 為空，記為系統呼叫；`ConversationId = CaseKey`，供 M6 以案件關聯。
- **回放：** 回放絕不呼叫真實模型，回放中的初審也一樣。
- **實機模型限制：** Responses API、`store=false`、不送 temperature；金鑰只在 user secrets，絕不寫入檔案或報告。

## Review Focus

1. **兩位客服同時對同一案按核准** → 只有一個成功、只開一張處理單、只扣一次庫存；另一位看到「狀態已變更」。→ Task 1 `Decide_Concurrent_OnlyOneFulfilment`；Task 6 `Coordinator_ConcurrentDecision_ResumesOnce`。
2. **初審還在跑，客服就直接決定**（不等 AI）→ 決定照常生效，初審標「已由人工處理」，之後即使 AI 跑完也不會再執行或通知。→ Task 6 `Decision_WhileRunning_SupersedesRun_NoLateExecution`。
3. **換貨目標規格在核准當下已無庫存** → 不建單、不扣成負數，Workflow 回到人工決策並顯示原因。→ Task 1 `Approve_ExchangeOutOfStock_FailsWithoutSideEffects`；Task 4 `Execute_Failure_LoopsBackToDecision`。
4. **初審跑到一半應用程式重啟** → 已完成的節點不重跑（不重複呼叫模型），未完成的從 checkpoint 續跑；等待人工的案子重啟後仍可決策。→ Task 4 `Restart_ResumesFromCheckpoint_CompletedNodesNotRerun`、`Restart_AwaitingDecision_StillAcceptsDecision`。
5. **摘要 Agent 建議核准，但規則核對判定不符資格** → 程式把建議降為「需人工判斷」並標紅說明，不讓模型蓋過規則。→ Task 5 `Summary_ApproveButIneligible_DowngradedWithWarning`。

---

### Task 1: 決策執行與處理單（Business）

**Files:**
- Create：
  - `AccessDatas/Models/Shop/AfterSalesFulfillment.cs`
  - migration `AddAfterSalesFulfillment`
  - `Business/Services/DataAccess/AfterSalesDecisionService.cs`
  - `Share/Shop/FulfillmentEnums.cs`
- Modify：
  - 業務：`AfterSalesRequestService.cs`（`DecideAsync`／`RequestMoreInfoAsync` 改為委派給決策服務，保留簽章與狀態守門）、`NotificationCategories.cs`（加 `AfterSalesUpdated`）、`AuditActions.cs`
  - 示範與 DI：`DemoResetService` 參與者（刪處理單、刪示範顧客的 `AfterSalesUpdated` 通知）、DI
  - 測試清單：`DataAccessServiceLifetimeTests`
- Test：`AfterSalesDecisionServiceTests.cs`（新）、既有 `AfterSalesRequestServiceTests.cs`

**Interfaces / behaviour:**
- `enum FulfillmentKind { ExchangeOrder = 1, RefundRecord = 2, ReplacementOrder = 3, ShipmentFollowUp = 4 }`
- `enum StaffDecisionKind { Approve = 1, Reject = 2, NeedMoreInfo = 3 }`
- `AfterSalesFulfillment { Id, AfterSalesRequestId (unique), Kind, ReferenceNo, ProductVariantId?, Quantity, decimal? RefundAmount, Note, CreatedAt }`
  - `ReferenceNo` 格式：`EX`／`RF`／`RP`／`SF` ＋ `yyyyMMdd` ＋ `Id:D4`。
- `Task<DecisionResult> ApplyAsync(int requestId, StaffDecisionKind decision, string? note, int? staffUserId)`。回傳 `sealed record DecisionResult(bool Success, string Message, AfterSalesStatus? NewStatus, string? FulfillmentNo)`。
  - **單一交易內：**
    1. 狀態守門的條件更新（`Submitted →` 目標狀態）；0 列 → `StatusChangedMessage`。
    2. 只在核准時，依類型處理：

       | 類型 | 處理單 | 庫存／物流 |
       |---|---|---|
       | `SizeExchange`／`ColorExchange` | `ExchangeOrder`（目標規格） | `ExecuteUpdate … WHERE Stock >= Quantity` 扣庫存 |
       | `QualityIssue` | `ReplacementOrder`（同規格） | 扣庫存 |
       | `ReturnRefund` | `RefundRecord`（`UnitPrice×Quantity`） | 不動庫存 |
       | `ShipmentUrge` | `ShipmentFollowUp` | 新增 `ShipmentEvent`「客服已催件，物流中心處理中」 |

    3. 扣庫存 0 列 → 回滾，訊息「目標規格已無庫存，無法核准；請改駁回或聯繫顧客。」。
  - 駁回、補件沿用既有規則（補件要有 note），寫入 `StaffNote`、`DecidedAt`、`DecidedByUserId`。
  - **commit 後**：
    - 以 `NotificationSender` 通知顧客：`Category=AfterSalesUpdated`，`Link="/shop/after-sales"`，`SourceKey=$"aftersales:{RequestNo}:{status}"`。
    - 標題依結果：
      - 核准：「售後申請 {no} 已核准」，內文帶處理單號。
      - 駁回：「已駁回」。
      - 補件：「需要補件」。
    - 通知失敗只記 Warning。
    - 寫稽核 `AuditActions.AfterSales.Decided`／`Fulfilled`。
- `GetFulfillmentAsync(int requestId)` → `AfterSalesFulfillmentView?`（給審核頁與顧客頁）。

- [ ] **Step 1: 寫失敗測試**：
  - `Approve_SizeExchange_CreatesExchangeOrderAndDecrementsStock`
  - `Approve_ReturnRefund_CreatesRefundRecordNoStockChange`
  - `Approve_QualityIssue_CreatesReplacementOrder`
  - `Approve_ShipmentUrge_AddsShipmentEvent`
  - `Approve_ExchangeOutOfStock_FailsWithoutSideEffects`
  - `Decide_Concurrent_OnlyOneFulfilment`
  - `Decide_SendsCustomerNotificationWithLink`
  - `Reject_NoFulfilment_NotifiesCustomer`
  - `Reset_RemovesFulfilmentsAndAfterSalesNotifications`
- [ ] **Step 2–4:** 失敗 → 實作（含 migration）→ 通過（既有 `AfterSalesRequestServiceTests`、`AfterSalesReviewViewTests` 全過）
- [ ] **Step 5: 檢查點**

### Task 2: 顧客通知鈴與「我的申請」處理結果

**Files:**
- Modify：
  - 版面：`Components/Layout/ShopLayout.razor(.cs/.css)`（放入既有 `NotificationBell`，必要時加參數 `Variant="shop"` 讓樣式貼合商店頂端列）、`NotificationBell.razor(.cs)`
  - 顧客頁：`Components/Views/Shop/ShopAfterSalesListView.razor(.cs)`
- Test：`Components/ShopLayoutTests.cs`、`Components/ShopAfterSalesListViewTests.cs`、`NotificationComponentTests.cs`

**Behaviour:**
- 商店頂端列顯示鈴鐺與未讀數，點通知導向 `/shop/after-sales`。鈴鐺沿用即時推播＋60 秒輪詢。
- 「我的申請」新增「處理結果」欄，依狀態顯示：
  - 核准：「換貨單 EX… 已建立」或「退款紀錄 RF… 已建立」。
  - 駁回／補件：客服說明。
  - 其他：「客服審核中」。
- 頁面訂閱 `INotificationSignal`，有新通知時自動重新載入。
- 浮層樣式依 DOM 位置放 `OverlayStyles.razor`，色票只用別名。

- [ ] **Step 1: 寫失敗測試**：
  - `ShopLayout_ShowsBellWithUnreadCount`
  - `MyRequests_Approved_ShowsFulfilmentNo`
  - `MyRequests_Signal_ReloadsList`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `ThemeConventionTests`）
- [ ] **Step 5: 檢查點**

### Task 3: Workflow 持久化基礎

**Files:**
- Modify：`Directory.Packages.props`、`CustomerHelpAgent.Web.csproj`（`Microsoft.Agents.AI.Workflows` 1.25.0）
- Create：
  - 實體與 migration：`AccessDatas/Models/Agents/PreReviewRun.cs`、`PreReviewNodeState.cs`、`WorkflowCheckpoint.cs`，migration `AddPreReviewWorkflow`
  - 服務：`Business/Services/DataAccess/PreReviewRunService.cs`
  - Workflow 端：`Web/Agents/PreReview/SqliteWorkflowCheckpointStore.cs`、`Web/Agents/PreReview/PreReviewJsonContext.cs`
  - 列舉：`Share/Agents/PreReviewEnums.cs`
- Modify：`DemoResetService` 參與者（刪初審與 checkpoint）、DI、`DataAccessServiceLifetimeTests`
- Test：`SqliteWorkflowCheckpointStoreTests.cs`、`PreReviewRunServiceTests.cs`

**Interfaces / behaviour:**
- `enum PreReviewStatus { Pending = 1, Running = 2, AwaitingDecision = 3, Executing = 4, Completed = 5, Failed = 6, Superseded = 7 }`
- `enum PreReviewNode { Intake, PhotoReview, RuleCheck, HistoryReview, Summary, StaffDecision, Execute, Notify }`
- `enum NodeStatus { Waiting, Running, Done, Failed, Skipped }`
- `PreReviewRun`：
  - 欄位：`Id`、`AfterSalesRequestId(FK)`、`Attempt`、`CaseKey`、`WorkflowSessionId`（`$"prereview-{requestId}-{attempt}"`，unique）、`Status`
  - 結論 JSON：`PhotoJson?`、`RuleJson?`、`HistoryJson?`、`SummaryJson?`
  - 摘要：`Recommendation?`、`HasWarning`、`ErrorMessage?`
  - 回放：`ReplayRecordingId?`
  - 時間：`CreatedAt`、`UpdatedAt`、`CompletedAt?`
  - 索引：`(AfterSalesRequestId, Attempt)` unique。
- `PreReviewNodeState { Id, PreReviewRunId(FK), Node, Status, StartedAt?, EndedAt?, Note? }`
- `WorkflowCheckpoint { Id, SessionId, CheckpointId, ParentCheckpointId?, PayloadJson, CreatedAt }`，索引 `(SessionId, Id)`、`(SessionId, CheckpointId)` unique。
- `SqliteWorkflowCheckpointStore`：實作 `CreateCheckpointAsync`（新 Guid、存 `GetRawText()`）、`RetrieveCheckpointAsync`、`RetrieveIndexAsync`。`RetrieveIndexAsync` **依 Id 由舊到新**排序，`GetLatestCheckpointAsync` 依賴這個順序。
- `PreReviewJsonContext : JsonSerializerContext` 涵蓋所有節點之間的訊息與狀態型別，交給 `CheckpointManager.CreateJson(store, options)`。
- `PreReviewRunService`：
  - 建立與讀取：`CreateAsync(requestId)`（Attempt 遞增）、`GetLatestForRequestAsync`、`GetLatestByRequestIdsAsync(ids)`（佇列頁用）
  - 節點與狀態：`SetNodeAsync(runId, node, status, note)`、`SaveFindingAsync(runId, node, json)`、`SetStatusAsync`
  - 認領：`TryClaimDecisionAsync(runId)`（`AwaitingDecision → Executing` 條件更新）
  - 收尾：`SupersedeAsync(requestId)`、`GetUnfinishedAsync()`（`Pending`／`Running`）

- [ ] **Step 1: 寫失敗測試**：
  - `CheckpointStore_RoundTripsJsonAndIndexOldestFirst`
  - `CheckpointStore_GetLatest_ReturnsNewest`（經 `CheckpointManager.CreateJson`）
  - `RunService_Attempt_Increments`
  - `RunService_TryClaim_OnlyOnce`
  - `Reset_RemovesRunsAndCheckpoints`
- [ ] **Step 2–4:** 失敗 → 實作（含 migration、套件弱點檢查 `dotnet list package --vulnerable --include-transitive`）→ 通過
- [ ] **Step 5: 檢查點**

### Task 4: 初審 Workflow 圖（假 Agent 測試）

**Files:**
- Create：
  - `Web/Agents/PreReview/PreReviewMessages.cs`（訊息與結論 record）
  - `PreReviewExecutors.cs`（Intake、PhotoReview、RuleCheck、HistoryReview、Summary、Execute、Notify）
  - `PreReviewWorkflowFactory.cs`
  - `IPreReviewAgents.cs`（`IPhotoReviewer`、`IHistoryReviewer`、`IPreReviewSummarizer`）
  - `PreReviewWorkflowRunner.cs`
- Test：`PreReviewWorkflowTests.cs`（用假 reviewer、in-memory SQLite、真 `SqliteWorkflowCheckpointStore`）

**Interfaces / behaviour:**
- **訊息型別：**
  - `PreReviewStart(int RunId, int RequestId)`
  - `PreReviewCase`（申請事實：類型、商品、顏色／尺碼、目標規格、簽收天數、是否穿過、描述、附件 Id 清單、`CaseKey`；**不含個資**）
  - `PreReviewFinding(PreReviewNode Node, FindingStatus Status, string Headline, string Json)`，其中 `FindingStatus { Ok, NotApplicable, Failed }`
  - `DecisionRequest(int RunId, string? PreviousFailure)`
  - `StaffDecision(StaffDecisionKind Kind, string? Note, int StaffUserId)`
  - `ExecutionResult(DecisionResult Result)`
- **圖：** `Intake →` fan-out `[PhotoReview, RuleCheck, HistoryReview]` → `AddFanInBarrierEdge` → `Summary → RequestPort("staff-decision") → Execute → Notify`。另有條件邊 `Execute → port`：執行失敗時送新的 `DecisionRequest(PreviousFailure=訊息)`。
- **每個節點：**
  - 進出都呼叫 `PreReviewRunService.SetNodeAsync`，並發布 `PreReviewSignal.Publish(requestId)`（Singleton，`Subscribe(requestId, Func<Task>)`）。
  - 自己捕捉例外，回傳 `Failed` finding，不讓 Workflow 中斷。
- **各節點規則：**
  - **PhotoReview：** 沒有附件時，回 `NotApplicable`、不呼叫模型。
  - **RuleCheck（純程式）：** `AfterSalesPolicyService.GetEffectiveAsync`＋`AfterSalesEligibility.Evaluate`，換貨類再查目標庫存。
  - **Summary：**
    - 收到 3 個 finding 才呼叫 summarizer。finding 暫存在 executor 欄位，以 `OnCheckpointingAsync`／`OnCheckpointRestoredAsync` 存回 context state。
    - 若 RuleCheck 不符資格而建議核准 → 改為 `Manual` 並設 `HasWarning`。
    - summarizer 失敗時，改用程式產生的固定摘要。
- **`PreReviewWorkflowRunner`：**
  - `StartAsync(runId)`：`RunStreamingAsync`（帶 `CheckpointManager`、`sessionId = WorkflowSessionId`）→ 讀事件直到 `RequestInfoEvent` → 狀態設為 `AwaitingDecision`，結束本次執行。
  - `ResumeAsync(runId)`：重啟續跑，從 `GetLatestCheckpointAsync` 用 `ResumeStreamingAsync` 接續。
  - `SubmitDecisionAsync(runId, StaffDecision)`：從最新 checkpoint 續跑 → 收到重播的 `RequestInfoEvent` → `SendResponseAsync(evt.Request.CreateResponse(decision))` → 跑到結束或再次等待。
  - 每次都重建同拓撲、同 executor id 的 Workflow。
- **Execute：** 呼叫 `AfterSalesDecisionService.ApplyAsync`。
- **Notify：** 只標記節點完成（通知已在 `ApplyAsync` commit 後送出），並把 run 設為 `Completed`。

- [ ] **Step 1: 寫失敗測試**：
  - `FanOut_ThreeReviewersRunInSameSuperstep`（假 reviewer 以 `TaskCompletionSource` 互相等待，證明是併行）
  - `FanIn_SummaryRunsOnceWithAllThreeFindings`
  - `BeforeDecision_NoStatusChangeOrFulfilment`
  - `Decision_Approve_ExecutesAndCompletes`
  - `Reviewer_Throws_FindingFailed_StillReachesDecision`
  - `Execute_Failure_LoopsBackToDecision`
  - `Restart_ResumesFromCheckpoint_CompletedNodesNotRerun`（第一次在 RuleCheck 後丟掉 runner，用新 runner 續跑，計數器證明 PhotoReview 只跑一次）
  - `Restart_AwaitingDecision_StillAcceptsDecision`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 5: 模型節點（照片、歷史、摘要）＋錄製回放

**Files:**
- Create：
  - `Web/Agents/PreReview/ModelPhotoReviewer.cs`、`ModelHistoryReviewer.cs`、`ModelPreReviewSummarizer.cs`、`PreReviewPromptDefaults.cs`
  - `Web/Agents/PreReview/PreReviewChatClientProvider.cs`（依即時／回放組模型鏈）
- Modify：
  - 提示詞：`AccessDatas/Models/PromptTemplate.cs`（`PromptTemplateKeys`：`AfterSalesPreReviewPhoto`、`AfterSalesPreReviewHistory`、`AfterSalesPreReviewSummary`、`AfterSalesStaffAssistant`，加入 `All`）、`Web/Ai/AiPromptGuardrails.cs`（兩個 switch；結構化輸出的 key 不要求 Markdown）
  - 計量：`Models/Systems/TokenUsageModels.cs`（`TokenUsageOperations.AfterSalesPreReview = "AI 售後初審 Workflow"`、`AfterSalesStaffAssistant = "客服 AI 助手"`）
  - 設定與模型工廠：`Web/Configuration/AfterSalesAgentSettings.cs`（`VisionModel`，空字串沿用 `AiSettings.Model`）＋validator、`AgentChatClientFactory.Create(..., string? model = null)`、`appsettings.json`
  - 錄製回放：`AgentRecordedCall`（加 `Stream` 欄，預設 `"customer"`）＋migration `AddRecordingStream`、`AgentRecordingService.AppendCallAsync/GetCallsAsync(…, stream)`、`ReplayCursorStore`（以 `(sessionKey, stream)` 為鍵）、`RecordingChatClient`
- Test：`PreReviewModelNodeTests.cs`、`PromptTemplateTests.cs`、`AgentRecordingTests.cs`、`AgentReplayTests.cs`

**Interfaces / behaviour:**
- 三個節點都用 `ChatClientAgent` ＋ `RunAsync<T>`（結構化輸出），無工具。
- **結構化輸出型別：**
  - `PhotoReviewResult { Verdict: consistent|inconsistent|unclear, Observations[], Confidence: low|medium|high }`
  - `HistoryReviewResult { PriorRequestCount, Notes[], CustomerStatements[] }`
  - `PreReviewSummary { Recommendation: approve|reject|need_more_info|manual, Headline, Reasons[], StaffChecklist[] }`
- **輸入：**
  - **照片：** `DataContent(bytes, contentType)`，經 `AfterSalesFileStore.TryResolveFullPath` 讀出，最多 3 張；文字只有類型與顧客描述。
  - **歷史：** 該顧客過去的申請（類型、結果、時間），以及 `AgentTraceService.GetSessionStepsAsync(caseKey, customerUserId)` 中 `UserMessage`／`ToolResult` 的白話文字。
  - **摘要：** 三個 finding 的 JSON。
- **模型鏈：**
  - 即時：`TracingChatClient`＋`RecordingChatClient(stream: "prereview:photo"|"prereview:history"|"prereview:summary")` → `factory.Create(TokenUsageOperations.AfterSalesPreReview, caseKey, Responses, reasoningEffort, model)`。
  - 回放：若該 `CaseKey` 的顧客執行紀錄 `ReplayRecordingId` 有值，改用 `ReplayChatClient`（同一份錄製、對應 stream），不呼叫工廠。
  - `PreReviewRun.ReplayRecordingId` 記錄回放來源。
- **錄製：** 顧客端維持 `"customer"` stream，既有錄製資料視為 customer。

- [ ] **Step 1: 寫失敗測試**：
  - `Photo_SendsImagesAsDataContent_NoPersonalData`
  - `Photo_NoAttachments_NoModelCall`
  - `History_PromptExcludesNameAddressPhone`
  - `Summary_ApproveButIneligible_DowngradedWithWarning`
  - `Summary_StructuredOutputInvalid_FallsBackToProgramSummary`
  - `VisionModel_Empty_FallsBackToMainModel`
  - `Recording_PreReviewCalls_StoredPerStream`
  - `Replay_PreReview_UsesRecordedStreamsWithoutModel`
  - `Guardrails_StructuredKeys_DoNotDemandMarkdown`
- [ ] **Step 2–4:** 失敗 → 實作（含 migration）→ 通過（既有錄製回放測試全過）
- [ ] **Step 5: 檢查點**

### Task 6: 背景執行、自動觸發、重啟續跑與決策協調

**Files:**
- Create：
  - Business：`Business/Services/Other/IAfterSalesSubmittedHook.cs`（`Task OnSubmittedAsync(int requestId, AfterSalesChannel channel)`；預設 no-op 實作）
  - Web 背景執行：`Web/Agents/PreReview/PreReviewQueue.cs`（有界 Channel）、`PreReviewWorker.cs : BackgroundService`、`PreReviewSubmittedHook.cs`
  - 協調器：`AfterSalesReviewCoordinator.cs`（scoped）
- Modify：`AfterSalesRequestService.SubmitAsync`／`ResubmitAsync`（commit 後呼叫 hook，例外只記 Warning）、DI、`ServiceCollectionExtensions`
- Test：`PreReviewWorkerTests.cs`、`AfterSalesReviewCoordinatorTests.cs`

**Behaviour:**
- **Hook：** 只處理 `Channel == Agent`：`PreReviewRunService.CreateAsync` → `PreReviewQueue.TryEnqueue(runId)`。
- **Worker：**
  - 每筆工作建立新的 scope，`CurrentUser` 為空；設定 `ExceptionContext(Background, "PreReview")`。
  - `Pending` → `StartAsync`；`Running` → `ResumeAsync`（有 checkpoint）或 `StartAsync`（沒有）。
  - 啟動時：
    - `GetUnfinishedAsync()` 全部重新入列。
    - 補建遺漏：Agent 管道、`Submitted`、沒有任何 run 的申請 → 建立 run 後入列。
- **`AfterSalesReviewCoordinator.DecideAsync(int requestId, StaffDecisionKind kind, string? note)`**（審核頁唯一入口）：
  - Agent 管道且最新 run 為 `AwaitingDecision`：`TryClaimDecisionAsync` → `runner.SubmitDecisionAsync`（在客服 circuit 內 await，純程式不呼叫模型；`StaffUserId` 取自 `CurrentUser`）。
  - 認領失敗 → 回「狀態已變更」。
  - 其他情況（傳統、run 在 `Running`／`Failed`／沒有 run）：
    1. `PreReviewRunService.SupersedeAsync(requestId)`
    2. `AfterSalesDecisionService.ApplyAsync`
  - 正在跑的初審在每個節點進入前檢查 `Superseded` → 停止，不執行、不通知。
- **`RetryAsync(requestId)`**：建立新 Attempt 並入列（失敗或完成後都可重跑）。

- [ ] **Step 1: 寫失敗測試**：
  - `AgentSubmit_EnqueuesPreReview`
  - `TraditionalSubmit_NoPreReview`
  - `Resubmit_CreatesNewAttempt`
  - `Startup_RequeuesUnfinishedAndMissing`
  - `Worker_RecordsModelCallsAsSystemWithCaseConversationId`
  - `Coordinator_AwaitingDecision_ResumesWorkflow`
  - `Coordinator_ConcurrentDecision_ResumesOnce`
  - `Decision_WhileRunning_SupersedesRun_NoLateExecution`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 7: 審核頁「AI 初審」面板與流程圖

**Files:**
- Create：`Components/Views/AfterSales/PreReviewPanel.razor(.cs/.css)`、`PreReviewFlowDiagram.razor(.css)`
- Modify：
  - 審核頁：`AfterSalesReviewView.razor(.cs)`
  - 樣式：`theme.css`（`--prereview-*` 別名）
  - 說明：`Datas/Help/after-sales-review.md`（第三段加 AI 初審、助手）
- Test：`Components/PreReviewPanelTests.cs`、`Components/AfterSalesReviewViewTests.cs`

**Behaviour:**
- **佇列表：** 加「AI 初審」欄，只有 Agent 管道顯示。Tag 有：初審中／建議核准／建議駁回／建議補件／需人工判斷／初審失敗／已由人工處理。
- **Modal：** `current.Channel == Agent` 時，在「申請內容」之後插入面板。傳統管道完全不變（M1 刻意的逐項開啟流程保留）。
- **面板內容：**
  - **流程圖：** 8 個節點（進件 → 照片初審｜規則核對｜歷史紀錄 → 摘要 → 人工決策 → 執行 → 通知）。
    - 每格標示「AI」「程式」或「人」，並顯示狀態色。
    - 訂閱 `PreReviewSignal` 即時更新，Dispose 時取消訂閱。
  - **AI 建議卡：** 建議、理由、客服檢查清單。`HasWarning` 時以紅色顯示「AI 建議已被程式改為需人工判斷：規則核對不符資格」。
  - **其他：** 三個 finding 可展開；附「重新初審」按鈕。
- **決策按鈕：** 核准／駁回／要求補件一律改走 `AfterSalesReviewCoordinator.DecideAsync`，傳統管道也一樣，行為不變。
  - 初審未完成時，按鈕旁提示「AI 初審尚未完成，直接決定會略過 AI 建議」，以 `ConfirmDialog` 確認。
  - 核准成功的訊息帶處理單號。

- [ ] **Step 1: 寫失敗測試**：
  - `Panel_TraditionalChannel_NotRendered`
  - `Panel_ShowsNodeStatesAndRecommendation`
  - `Panel_Signal_RefreshesDiagram`
  - `Panel_Warning_RenderedRed`
  - `Decide_WhileRunning_AsksConfirm`
  - `Queue_AgentRow_ShowsPreReviewTag`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `ThemeConventionTests`、`FormModalConventionTests`、`PageHelpCatalogTests`）
- [ ] **Step 5: 檢查點**

### Task 8: 客服 AI 助手

**Files:**
- Create：`Web/Agents/StaffAssistant/StaffAssistantRunner.cs`、`StaffAssistantTools.cs`、`HandoverSummaryBuilder.cs`、`StaffAssistantPromptDefaults.cs`；`Components/Views/AfterSales/StaffAssistantPanel.razor(.cs/.css)`
- Modify：`AfterSalesReviewView.razor`（Agent 管道的 modal 加「AI 助手」分頁）、DI
- Test：`StaffAssistantToolsTests.cs`、`HandoverSummaryBuilderTests.cs`、`Components/StaffAssistantPanelTests.cs`

**Behaviour:**
- **交接摘要（純程式，不呼叫模型）：** `HandoverSummaryBuilder.BuildAsync(caseKey, customerUserId)`，從顧客端 Agent 的軌跡整理出：
  - 查過的訂單筆數
  - 套用的規則與版本
  - 查過的庫存
  - 顧客說過的關鍵句
  - 確認卡內容
  - 是否上傳照片

  分頁一打開就顯示，標題「轉人工交接：顧客端 Agent 已查過」。
- **工具（全部只讀；案件由程式綁定為目前開啟的那一案，模型不能指定其他案件或顧客）：**
  - `get_case`
  - `get_effective_policy`
  - `check_stock`
  - `get_customer_history`
  - `get_customer_agent_trail`
  - `get_pre_review_result`

  每次呼叫先確認目前客服有 `角色_售後審核` 權限，沒有 → `forbidden`。工具結果沿用 `ToolOutcome` 信封與 `withheld_fields`。
- **Runner：**
  - `ChatClientAgent`＋`UseFunctionInvocation(MaximumIterationsPerRequest=6)`，串流回覆。
  - 會話只存在記憶體（modal 關閉即清除）。
  - 回覆以 `AiMarkdownRenderer.ToSafeHtml` 呈現；工具呼叫以小標籤顯示在回覆上方（「查了：規則 v1」）。
- **限制：**
  - 回放模式下助手停用，顯示「回放模式不提供客服 AI 助手（需即時模型）」。
  - 未設定 AI 時同樣停用並提示。

- [ ] **Step 1: 寫失敗測試**：
  - `Handover_ListsWhatCustomerAgentLookedUp`
  - `Handover_NoPersonalData`
  - `Tools_CaseBoundToOpenRequest_CannotQueryOthers`
  - `Tools_NoPermission_Forbidden`
  - `Tools_AreAllReadOnly`
  - `Panel_ReplayMode_Disabled`
  - `Panel_TraditionalChannel_NoAssistantTab`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 9: 文件、品質關卡、實機驗證與開發歷程

**Files:**
- 版本：`appsettings.json`（`1.0.5 → 1.0.6`）
- 功能文件：`docs/features/售後Agent示範系統.md`（新增 M4 章節：流程、狀態、已知限制）
- 設計文件：`docs/prd/售後Agent展示系統-設計.md`（M4 進度與實作差異註記）
- 企劃規劃說明書：
  - §5 加 M4 展示段落：劇本 B 白鞋開膠送出 → 切到客服後台看流程圖亮燈 → AI 建議 → 人工核准 → 顧客鈴鐺收到通知
  - §5.2.1 加 M4 驗證步驟
- 企劃書：客服 AI 改為〔已提供〕
- 其他文件：`docs/architecture/資料模型與資料庫.md`（新實體）、`docs/prd/稽核紀錄-prd.md`（新稽核碼與總數）、`docs/guides/畫面與欄位字典.md`、`docs/operations/日誌與設定檔說明.md`（`VisionModel`）、`docs/prd/README.md`、`docs/README.md`
- 計畫存檔：`docs/prd/售後Agent展示系統-M4實作計畫.md`（含 BOM），並列入索引
- 開發歷程：`docs/prd/售後Agent示範系統開發歷程紀錄.md`
  - §1 時間軸補 M3 commit `d073282` 與 M4 一列
  - 新增 M4 章節（指示原話、選擇、做了什麼、結果、最花時間、新工件、學到的事）
  - §7 估算對照實際
  - §8 新增 M4 審閱發現（如果沒抓到會怎樣、怎麼修、幾輪），更新 §8.5 數字
  - §9 回顧
- 專案記憶 `project-m2-hardening-backlog` 由 controller 更新

- [ ] **Step 1: 更新文件**（UTF-8 含 BOM）
- [ ] **Step 2: `pwsh ./scripts/Invoke-QualityGate.ps1`** → `QUALITY GATE PASSED`
- [ ] **Step 3: 實機驗證**（金鑰在 user secrets，不得寫出）：
  1. 劇本 A（換碼）即時跑完 → 客服後台看到流程圖跑到「人工決策」，照片節點為「不適用」→ 核准 → 換貨單建立、黑 41 庫存 −1 → 顧客鈴鐺有通知、「我的申請」顯示處理單號。
  2. 劇本 B 白鞋開膠上傳照片 → 照片初審以視覺模型回結構化結論（驗證 `gpt-6.1-sol` 收圖片＋JSON schema 在 Responses `store=false` 可用；不可用時記錄於文件並設 `VisionModel`）。
  3. 初審進行中停止應用程式再啟動 → 從 checkpoint 續跑，Token 用量顯示已完成的節點沒有再次呼叫。
  4. 開 `PolicyServiceUnavailable` → 規則核對節點失敗，摘要為需人工，仍可人工決策。
  5. 回放劇本 B 金劇本（需先重錄含初審的金劇本）→ 初審節點以回放完成、Token 用量不增加。
  6. 客服 AI 助手：問「顧客之前說過穿過嗎？」→ 引用交接內容回答；問其他顧客的訂單 → 拒絕。
- [ ] **Step 4: 檢查點**：回報結果，建議 commit 訊息「M4：申請初審 Workflow、核准執行與顧客通知、客服 AI 助手；版本 1.0.5 → 1.0.6」

---

## Claude 代為決定的事項（請檢查）

1. **傳統管道核准也開單、扣庫存、通知**：兩個管道的差別只在有沒有 AI，這樣對照才公平，傳統顧客也收得到通知。
2. **不新增「已完成」狀態**：核准當下就在同一交易內開單，顧客看到的是「已核准＋處理單號」。
3. **客服 AI 助手只在即時模式可用**：回放時停用，因為客服提問每次不同。初審 Workflow 本身支援回放。
4. **客服在初審跑完前就決定**：允許，但要先確認；之後 AI 的結果只供參考，不會再執行。
5. **要求補件後，顧客重新送件**：會跑新一輪初審（Attempt 2）。

## 執行方式

沿用：**Subagent-driven**，新分支 `feature/m4-staff-ai`，以 git index 作檢查點，不自動 commit。

## 驗證總結

- **自動：** 各 Task 測試（特別是 Task 4 的假 Agent Workflow 測試：併行、人工前不執行、重啟續跑）＋品質關卡。
- **實機：** Task 9 Step 3 全部項目。
