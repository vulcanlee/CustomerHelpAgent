# 售後 Agent 展示系統 M3 Implementation Plan

- 文件版本：0.2
- 文件狀態：已完成（M3 已於 1.0.5 實作；本文保留當時的計畫與各任務的實作補充，實作現況以 [售後 Agent 示範系統](../features/售後Agent示範系統.md) §6 為準）
- 現行系統版本：1.0.6
- 首次實作版本：1.0.5
- 最後核對日期：2026/10/24

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

M2（單一售後 Agent＋確認卡＋幕後面板，版本 1.0.4）已合併到 main（`e48f204`），並在使用者的 Azure `gpt-6.1-sol`（Responses API、store=false）實機跑通劇本 A／B。M3 的目的是**讓展示不翻車**：展示者能在現場故意製造故障、一鍵重置示範資料、選劇本一鍵就緒、在模型出狀況或沒網路時改用錄製回放、一鍵切換顧客／展示者身分。開工前先處理 M2 審閱留下的 8 項補強（專案記憶 `project-m2-hardening-backlog`），因為回放與故障示範都建立在「還原一致、確認卡不誤導」之上。

使用者已決定：回放時**自動帶入下一句**（不可改字、標示「回放中」）；**一鍵免密碼切換身分**（僅管理員＋示範模式）；**重置只重來業務資料、保留紀錄**（Token 用量、AI 對話紀錄、Agent 執行紀錄、操作埋點、錄製檔保留）。

**Goal:** 展示控制台（故障開關、劇本、重置、即時／回放、身分切換、面板預設）與錄製／回放可用，M2 補強完成，全部通過品質關卡並實機驗證。

**Architecture:**
- 補強在既有 Runner／Tools／SessionStore 上做。還原改以執行軌跡（新增 `TurnResult` 步驟）為單一來源。
- 故障由 Singleton `DemoControlService`（狀態存 SQLite）提供，注入模擬業務服務。
- 錄製在 `AuditingChatClient` 之外包一層 `RecordingChatClient`。回放以 `ReplayChatClient` 取代整條模型鏈，所以不經額度與計費、不需金鑰；工具照常對重置後的資料真實執行，請求摘要不符時中止回放。
- 身分切換用一次性票券＋靜態 SSR 頁 `/Auths/DemoSwitch` 簽發 Cookie。
- 控制台是管理員專屬頁 `/demo-console`。

**Tech Stack:** .NET 10、Blazor Server、AntDesign、EF Core SQLite、MAF 1.25、MEAI 10.10、xUnit＋bUnit。

**Spec:** `docs/prd/售後Agent展示系統-設計.md` §3.1（錄製／回放範圍、種子資料）、§4.5（展示控制台）、§4.3（標紅）；補強清單：專案記憶 `project-m2-hardening-backlog`。

## Global Constraints

- 沿用 M1／M2 全部慣例：分層、`IDbContextFactory`、`OrderBy…ThenBy(Id)`、英文日誌且不含姓名／地址／手機、`AuditActions` 常數、BlazorMaterialIcons、`ViewNotification`、`ConfirmDialog`、Modal 規則、色票只在 `theme.css :root`、文件 UTF-8 含 BOM。權威：`docs/architecture/開發慣例與限制速查.md`。
- 時間一律 `TimeProvider.GetLocalNow().DateTime`。
- **不自動 commit**：實作者不得 `git add`／`git commit`；controller 以 git index 作檢查點。
- 版本：M3 結束 `1.0.4 → 1.0.5`。
- **所有示範功能只在 `DemoSettings.Enabled == true` 時存在**；`ASPNETCORE_ENVIRONMENT=Production` 且 `DemoSettings.Enabled=true` → `StartupSafetyValidator` 拒絕啟動。
- 回放**絕不**呼叫真實模型、不檢查額度、不寫 Token 用量；畫面必須明確標示「回放中」。回放中工具仍真實執行（會真的建立申請）。
- 模型看不到使用者 Id、收件人、地址、手機；錄製檔只存模型請求摘要與回應，**不存**系統指示以外的個資（工具結果本來就不含）。
- 實機模型限制：Responses API、`store=false`、不送 temperature；金鑰只在 user secrets，絕不寫入檔案或報告。

## Review Focus

1. 非管理員或示範模式關閉時，能否透過網址／票券切換成別人 → 一律拒絕。→ Task 7 `Switch_NonAdmin_IsRejected`、`Switch_DemoDisabled_IsRejected`、`Ticket_ReusedOrExpired_IsRejected`。
2. 回放資料與錄製不符（未重置、故障開關不同）→ 中止回放並說明，不偽造回覆。→ Task 6 `Replay_ToolResultDiffers_StopsWithReplayDiverged`。
3. 重置時正有顧客在對話或有照片暫存 → 重置後舊會話不可再確認寫入。→ Task 4 `Reset_ThenOldSessionConfirm_IsStale`。
4. 故障開關殘留在展示後 → 控制台醒目顯示目前啟用的故障，且顧客頁的幕後面板標示「故障模擬中」。→ Task 3、Task 9。
5. 兩分頁同會話各送一句 → 第二個存檔衝突時明確提示，不默默蓋掉。→ Task 2 `SaveState_StaleVersion_Throws`。

---

### Task 1: Agent 補強 A（確認卡去重、穿著資訊、每輪步數總量、標紅、guardrail）

**Files:**
- Modify:
  - Agent：`Web/Agents/AfterSales/AfterSalesAgentRunner.cs`、`AfterSalesAgentTools.cs`、`AfterSalesAgentGuard.cs`、`TracingChatClient.cs`
  - 業務：`Business/Services/DataAccess/AfterSalesRequestService.cs`
  - 其他：`Share/Agents/AgentEnums.cs`、`Web/Ai/AiPromptGuardrails.cs`、`Web/Agents/AfterSales/AfterSalesAgentPromptDefaults.cs`
- Test: `AfterSalesAgentRunnerTests.cs`、`AfterSalesAgentToolsTests.cs`、`PromptTemplateTests.cs`

**Interfaces / behaviour:**
- `AfterSalesRequestService.GetOpenRequestNoAsync(int customerUserId, int orderItemId, AfterSalesType type)` → `Task<string?>`（公開既有私有規則，含擁有權）。`BuildCardAsync` 在組卡前呼叫；有處理中申請 → 不出卡，以 `CreateResponse(false, "這個商品已有處理中的同類申請（{no}）")` 回模型。
- `get_effective_policy(int orderItemId, string issueType, bool? isWorn = null)`：`isWorn` 為顧客明確陳述（描述寫明「只在顧客明說時填」），傳入 `AfterSalesEligibility.Evaluate`；規則卡 `MissingInfo` 不再在顧客已回答後顯示「是否穿過」。
- 每輪模型呼叫總量上限：`TracingChatClient` 以 Runner 傳入的 `TurnBudget`（`MaxSteps + 1`）跨子回合累計，超過 → 拋 `AgentTurnBudgetExceededException` → `StepLimitReached`；被額度擋下的呼叫不計入 `ModelCallCount`（捕捉 `AiQuotaExceededException` 時回減）。
- 標紅：`AgentStepKind` 加 `Warning`；Guard 記錄本輪是否出現 `not_found`／`timeout`／`conflict`／`no_policy`；Runner 收尾時若最終文字是**模型文字**（非程式固定說明）且不含問句 → 發 `Warning` 事件（`Source=Program`，白話「模型在查無資料／逾時／規則衝突後直接下結論，請留意」）。
- `AiPromptGuardrails.DataDescription(AfterSalesAgent)` 改為名詞片語 `"工具回傳內容與顧客訊息"`。

- [ ] **Step 1: 寫失敗測試**：
  - `Card_WhenOpenRequestExists_NotShownAndModelToldWhy`
  - `GetEffectivePolicy_WithIsWorn_NoLongerMissingWornInfo`
  - `TurnBudget_AcrossRejectedSubRuns_StopsAtLimit`（3 次程式拒絕＋模型持續呼叫工具 → 總呼叫數 ≤ MaxSteps+1）
  - `QuotaBlockedCall_NotCountedAsModelCall`
  - `NotFound_ThenModelConcludes_EmitsWarning`
  - `NotFound_ThenModelAsks_NoWarning`
  - `Guardrail_AfterSalesAgent_ReadsAsNounPhrase`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含既有 `AfterSalesAgentRunnerTests`、`AfterSalesAgentToolsTests` 全過）
- [ ] **Step 5: 檢查點**

### Task 2: Agent 補強 B（還原一致性、作廢原子化、會話版本）

**Files:**
- Modify：
  - Agent：`AfterSalesAgentRunner.cs`（`GetHistoryAsync`、`SaveSessionAsync`、收尾發 `TurnResult`）
  - 資料：`Business/Services/DataAccess/AgentSessionStore.cs`、`AccessDatas/Models/Agents/AgentSessionRecord.cs`（加 `int StateVersion`）
  - 列舉：`Share/Agents/AgentEnums.cs`（`AgentStepKind.TurnResult`）
  - 畫面：`Web/Components/Views/Shop/Assistant/ShopAssistantView.razor.cs`（`RestoreAsync`）
- Create：
  - migration `AddAgentSessionVersion`
  - `Web/Agents/AfterSales/AgentHistoryItem.cs`
- Test：`AfterSalesAgentRunnerTests.cs`、`AgentSessionStoreTests.cs`（新）、`Components/ShopAssistantViewTests.cs`

**Interfaces / behaviour:**
- 每輪收尾發一筆 `TurnResult` 步驟，`RawJson` = `{ replyText, isProgramReply, stopReason, stopExplanation, policyCards[], createdRequestNo }`；不含個資。
- `GetHistoryAsync(Guid sessionKey)` 改回傳 `IReadOnlyList<AgentHistoryItem>`：`sealed record AgentHistoryItem(AgentStepKind Kind, AgentDataSource Source, string Text, bool IsProgramReply, IReadOnlyList<AgentPolicyCard> PolicyCards, string? StopExplanation, ConfirmationCard? PendingCard, DateTime OccurredAt)`，來源為該會話所有 run 的 `UserMessage`＋`TurnResult` 步驟（依時間），最後附未決確認卡；另提供 `GetTraceEventsAsync(Guid sessionKey)` 供幕後面板還原。
- 頁面還原：程式說明以系統訊息呈現、規則卡與停止說明還原、幕後面板以軌跡事件還原。被固定說明取代的模型原文**不再出現**。
- `AgentSessionStore.VoidPendingConfirmationAsync(sessionKey, customerUserId)`：條件式 `ExecuteUpdateAsync(Approved == null → false)`，取代 `SendAsync`／`StartNewAsync` 的讀改寫作廢。
- `SaveStateAsync(..., int expectedVersion)`：`WHERE StateVersion == expectedVersion` 更新並 `+1`；0 列 → 拋 `AgentSessionConflictException("這段對話已在其他分頁更新，請重新整理頁面。")`；Runner 於 `BeginTurnAsync` 讀版本，頁面顯示此訊息。

- [ ] **Step 1: 寫失敗測試**：
  - `History_RestoresProgramReplyAsSystemAndPolicyCards`
  - `History_FixedReplyTurn_DoesNotShowReplacedModelText`
  - `TraceEvents_RestoreBackstage`
  - `VoidPending_DoesNotOverwriteConcurrentClaim`
  - `SaveState_StaleVersion_Throws`
  - `Assistant_Restore_ShowsSystemEntryAndPolicyCard`
- [ ] **Step 2–4:** 失敗 → 實作（含 migration）→ 通過
- [ ] **Step 5: 檢查點**

### Task 3: 展示狀態與故障模擬

**Files:**
- Create：
  - `Share/Demo/DemoEnums.cs`
  - `AccessDatas/Models/Demo/DemoControlState.cs`
  - migration `AddDemoControl`
  - `Business/Services/Other/DemoControlService.cs`
- Modify：
  - 業務服務：`ShopOrderService.cs`、`AfterSalesPolicyService.cs`
  - Agent：`AfterSalesAgentTools.cs`（權限過期故障）
  - 啟動與設定：`Web/Configuration/StartupSafetyValidator.cs`、`ServiceCollectionExtensions.cs`
  - 所有直接建構上述服務的測試 fixture
- Test：`DemoControlServiceTests.cs`、`DemoFaultInjectionTests.cs`、`StartupSafetyValidator` 相關測試

**Interfaces / behaviour:**
- `enum DemoFault { OrderServiceTimeout, OrdersNotFound, ShipmentAnomaly, PolicyServiceUnavailable, PolicyConflict, PolicyNotFound, PermissionExpired }`
- `enum DemoMode { Live, Replay }`
- `DemoControlState { Id(=1), FaultsJson, Mode, ActiveRecordingId?, BackstageDefaultOn, UpdatedAt }`（單列）
- `DemoControlService`（Singleton；`IDbContextFactory`、`IOptions<DemoSettings>`、`TimeProvider`、`ILogger`；記憶體快取＋寫入 DB；`Changed` 事件）。成員如下：
  - `bool IsEnabled`
  - `IReadOnlySet<DemoFault> ActiveFaults`
  - `bool IsFaultActive(DemoFault)`
  - `Task SetFaultAsync(DemoFault, bool on)`
  - `Task ClearFaultsAsync()`
  - `DemoMode Mode`
  - `int? ActiveRecordingId`
  - `Task SetModeAsync(DemoMode, int? recordingId)`
  - `bool BackstageDefaultOn`
  - `Task SetBackstageDefaultAsync(bool)`
  - `Task LoadAsync()`（啟動時）
  - `TimeSpan FaultDelay`（預設 30 秒）

  `IsEnabled == false` 時一律視為無故障、Live。
- 注入點（服務多一個建構參數 `DemoControlService`）：
  - `OrderServiceTimeout`：`ShopOrderService` 各讀取方法先 `await Task.Delay(FaultDelay)` 再拋 `TimeoutException("訂單系統沒有回應（故障模擬）")`。Agent 的工具逾時（15 秒）先觸發 → `timeout`。
  - `OrdersNotFound`：`GetMyOrdersAsync` 回空清單；`GetMyOrderItemAsync`／`GetMyOrderDetailAsync` 回 null。
  - `ShipmentAnomaly`：`GetMyOrderDetailAsync` 的物流 `StatusText = "配送異常"`，並附事件「配送異常，物流中心查詢中」。
  - `PolicyServiceUnavailable`：`GetEffectiveAsync` 拋 `InvalidOperationException("規則服務暫時無法使用（故障模擬）")`。
  - `PolicyConflict`：`GetEffectiveAsync` 回 `Conflict`（現行版本＋一筆合成的衝突版本）。
  - `PolicyNotFound`：`GetEffectiveAsync` 回 `NoPolicy`。
  - `PermissionExpired`：`AfterSalesAgentTools.ResolveCustomerAsync` 回 null → `forbidden`（只影響 Agent；傳統流程的登入失效由真實驗證機制處理，不模擬）。
- `StartupSafetyValidator`：Production 且 `DemoSettings.Enabled` → 啟動失敗（訊息說明要關閉示範模式）。

- [ ] **Step 1: 寫失敗測試**：
  - `DemoControl_PersistsAndReloadsFaults`
  - `DemoControl_WhenDemoDisabled_ReportsNoFaults`
  - 每個故障一個測試：`Fault_OrderServiceTimeout_AgentToolReturnsTimeout`、`Fault_OrdersNotFound_…`、`Fault_ShipmentAnomaly_…`、`Fault_PolicyServiceUnavailable_ToolUnavailableAndTraditionalSubmitFails`、`Fault_PolicyConflict_…`、`Fault_PolicyNotFound_…`、`Fault_PermissionExpired_ToolForbidden`
  - `Startup_ProductionWithDemoEnabled_Fails`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含所有既有測試；建構子變更要同步 fixture）
- [ ] **Step 5: 檢查點**

### Task 4: 一鍵重置示範資料

**Files：**
- Create：`Business/Services/Other/DemoResetService.cs`
- Modify：
  - `Business/Startup/DemoCatalogSeeder.cs`（抽出共用的「寫入目錄」方法）
  - `Web/Agents/AfterSales/AgentPhotoInbox.cs`（`ClearAll()`）
  - DI
- Test：`DemoResetServiceTests.cs`

**Interfaces / behaviour：**
- `DemoResetService(IDbContextFactory<BackendDBContext>, AfterSalesFileStore, IOptions<DemoSettings>, TimeProvider, IAuditLogService, CurrentUserService, IEnumerable<IDemoResetParticipant>, ILogger<DemoResetService>)`
  - 實作時調整：Business 不能參考 Web 的 `AgentPhotoInbox`，改加 `IDemoResetParticipant`（Business 介面，`void OnDemoDataReset()`）；
    `AgentPhotoInbox` 實作它（呼叫 `ClearAll()`），Web 以 `AddSingleton<IDemoResetParticipant>(sp => sp.GetRequiredService<AgentPhotoInbox>())` 登記同一實例。Task 6 的回放游標也可沿用。
- `Task<DemoResetResult> ResetAsync()`：`DemoSettings.Enabled == false` → 失敗。
  - 在一個交易中依 FK 順序 `ExecuteDeleteAsync`：
    1. `AfterSalesAttachment`
    2. `AfterSalesRequest`
    3. `AgentConfirmation`
    4. `AgentSessionRecord`
    5. `ShipmentEvent`
    6. `Shipment`
    7. `SalesOrderItem`
    8. `SalesOrder`
    9. `ProductVariant`
    10. `Product`
    11. `AfterSalesPolicy`
  - 接著以 `DemoSeedData.CreateCatalog(now, ids)` 重新寫入。
  - 目錄資料表一律帶**固定 Id**（`CreateCatalog` 內指定；初次寫入與每次重置相同，錄製檔的 `orderItemId` 等才能回放）：
    商品 1–2、規格 1–22（慢跑鞋 黑 38–44＝1–7、白＝8–14；外套 藏青 S–XL＝15–18、灰＝19–22）、訂單與明細 1–4、物流 1、物流事件 1–3、售後規則 1–5。
    售後申請與 Agent 資料表不固定 Id。
  - commit 後刪除附件實體檔（`AfterSalesFileStore.Delete`），並逐一呼叫 `IDemoResetParticipant.OnDemoDataReset()`（`AgentPhotoInbox.ClearAll()`；單一參與者失敗只記警告）。
  - 抽出的共用寫入方法：`DemoCatalogSeeder.WriteCatalogAsync(BackendDBContext, DateTime now, CancellationToken)`（查示範顧客 Id、AddRange、SaveChanges；交易由呼叫端決定）。
  - 稽核 `AuditActions.Demo.Reset`。
  - **不刪**：`ProcessMetric`、`AgentRunTrace`／`AgentRunStep`、Token 用量、AI 對話紀錄、錄製檔、稽核。
- `sealed record DemoResetResult(bool Success, string Message, int DeletedRequests, int DeletedSessions)`
- `AuditActions.Demo`：`Reset = "Demo.Reset"`、`FaultChanged = "Demo.FaultChanged"`、`ModeChanged = "Demo.ModeChanged"`、`SwitchIdentity = "Demo.SwitchIdentity"`、`SwitchBack = "Demo.SwitchBack"`、`RecordingGolden = "Demo.RecordingGolden"`。

- [ ] **Step 1: 寫失敗測試**：
  - `Reset_RestoresFlagshipOrdersRelativeToNow`
  - `Reset_RemovesRequestsAndFiles`
  - `Reset_RestoresPolicyVersionsToV1`
  - `Reset_KeepsTracesMetricsAndUsage`
  - `Reset_ThenOldSessionConfirm_IsStale`
  - `Reset_WhenDemoDisabled_Fails`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 5: 錄製

**Files：**
- Create：
  - `AccessDatas/Models/Demo/AgentRecording.cs`、`AgentRecordedTurn.cs`、`AgentRecordedCall.cs`
  - migration `AddAgentRecording`
  - `Business/Services/DataAccess/AgentRecordingService.cs`
  - `Web/Agents/AfterSales/RecordingChatClient.cs`
  - `Web/Agents/AfterSales/ModelRequestDigest.cs`
- Modify：`AfterSalesAgentRunner.cs`、DI
- Test：`AgentRecordingTests.cs`

**Interfaces / behaviour：**
- 實體：
  - `AgentRecording { Id, SessionKey, CustomerUserId, Title, IsGolden, ScenarioCode?, ModelName, TotalCostTwd?, CreatedAt }`（`SessionKey` unique）
  - `AgentRecordedTurn { Id, AgentRecordingId(FK), Sequence, Kind(Send|Confirm|Reject|PhotosUploaded), CustomerText?, CreatedAt }`
  - `AgentRecordedCall { Id, AgentRecordingId(FK), Sequence, RequestDigest, ResponseJson, UsageJson?, CreatedAt }`
- `ModelRequestDigest.Compute(IEnumerable<ChatMessage> messages)` → SHA-256 hex，算法如下：
  - 只取 `User` 文字與 `FunctionResultContent.Result`，依序串接後雜湊。
  - **不含**系統指示、時間戳、模型自身輸出。
  - 這樣重置後同樣的資料會得到同樣的摘要；ID 由種子固定、日期相對於重置時間。
  - 實作補充：雜湊前正規化會隨重置改變的值——日期時間（ISO-8601、`yyyy/MM/dd [HH:mm]`）、申請編號 `AS\d{12}`、建立結果的 `"request_id":數字`；
    工具結果一律轉成同一種 JSON 寫法（中文不跳脫），當輪物件與還原會話裡重新解析的 JSON 得到同一個字串。庫存、資格等真正的資料差異仍會讓摘要不同。
- `RecordingChatClient : DelegatingChatClient`：
  - 位置：包在 `AgentChatClientFactory.Create` 回傳值之外、`TracingChatClient` 之內。
  - 只記錄成功的回應：串流在完成後 `ToChatResponse()`，序列化用 `AIJsonUtilities.DefaultOptions`。
  - `DemoControlService.IsEnabled && Mode == Live` 時才記。
  - 實作補充：Runner 每輪開始判定一次是否錄製，錄製中才插入這一層（`TracingChatClient` → `RecordingChatClient` → 工廠回傳值）。
    串流照原樣逐段轉交，全部取完後才寫入；呼叫端中途放棄或失敗都不記。模型名稱取回應的 `ModelId`，沒有就用 `AgentChatClientFactory.ConfiguredModel`（`AiSettings:Model`）。
- Runner：
  - 每次 `SendAsync`／`ConfirmAsync`／`NotifyPhotosUploadedAsync` 先寫 `AgentRecordedTurn`，同一會話的第一次呼叫建立 `AgentRecording`（Title 取第一句前 20 字）。
  - 錄製失敗只記 Warning，不影響對話。
  - 實作補充：已作廢（重複按、別的分頁先處理）的確認卡不記；上傳照片的動作記下代送的「（已上傳 N 張照片）」。
- `AgentRecordingService`（記錄寫入均不拋例外）：
  - 實作補充：寫入端 `AppendTurnAsync`（無錄製時建立）、`AppendCallAsync`（無錄製時略過並記 Warning）；動作與呼叫各自由 1 起編號。
  - 實作補充：`TotalCostTwd` 在每記一筆呼叫時，以 AI 對話紀錄 `ConversationId`（＝SessionKey）對到 Token 用量 `CostTwd` 在記憶體加總後存下（AI 對話紀錄過期後仍保留；關閉 AI 對話紀錄時維持 null）。
  - 實作補充：金劇本另有部分唯一索引（`ScenarioCode` where `IsGolden = 1`）守門；設定與刪除不寫稽核，比照 Task 3 由展示控制台以登入者身分寫 `Demo.RecordingGolden`。
  - `GetRecordingsAsync(DataRequest)`
  - `GetAsync(int id)`（含 turns）
  - `SetGoldenAsync(int id, string scenarioCode)`（同劇本只能一個金劇本）
  - `GetGoldenAsync(string scenarioCode)`
  - `DeleteAsync(int id)`
  - `GetCallsAsync(int id)`

- [ ] **Step 1: 寫失敗測試**：
  - `Recording_LiveRun_StoresTurnsAndCallsInOrder`
  - `Recording_ReplayMode_DoesNotRecord`
  - `Recording_DemoDisabled_DoesNotRecord`
  - `Digest_SameDataAfterReset_IsStable`
  - `Digest_IgnoresSystemInstructions`
  - `SetGolden_OnePerScenario`
  - `RecordedJson_NeverContainsAddressOrPhone`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 6: 回放

**Files：**
- Create：`Web/Agents/AfterSales/ReplayChatClient.cs`、`Web/Agents/AfterSales/ReplayCursorStore.cs`（Singleton）
- Modify：
  - `AfterSalesAgentRunner.cs`
  - `Share/Agents/AgentEnums.cs`（`AgentStopReason.ReplayDiverged`、`ReplayFinished`）
  - `AgentStopLabels.cs`
- Test：`AgentReplayTests.cs`

**Interfaces / behaviour：**
- 模式判定：`DemoControlService.Mode == Replay && ActiveRecordingId` 存在 → Runner 不呼叫 `AgentChatClientFactory.Create`，改用 `new ReplayChatClient(calls, cursor)`。因此不需金鑰、不經額度、不記 Token 用量。`TracingChatClient`、function invocation、guard、工具都照常運作。
- `ReplayChatClient`：
  - 依 `ReplayCursorStore`（以 sessionKey 為鍵）取下一筆錄製呼叫。
  - 實作補充：游標另記錄製 Id（換了回放的錄製就從頭開始），顧客動作與模型呼叫各一個索引；呼叫依 `(Sequence, Id)` 順序取用，一個動作可以沒有呼叫。游標只在記憶體，`ReplayCursorStore` 同時登記為 `IDemoResetParticipant`（一鍵重置清空全部游標）。
  - 先比對 `ModelRequestDigest.Compute(messages)` 與錄製的 `RequestDigest`：不同 → 拋 `ReplayDivergedException`；錄製用完 → 拋 `ReplayExhaustedException`。
  - 實作補充：摘要不同的那一筆不消耗。位置與錄製相同（`TracingChatClient` → `ReplayChatClient`），完全不呼叫工廠，所以不驗證端點與金鑰。
  - 一致 → 回傳反序列化的 `ChatResponse`。串流時以 `ToChatResponseUpdates()` 逐段輸出，可加 30 ms 間隔模擬打字。
  - 實作補充：間隔由 Runner 的 internal `ReplayUpdateDelay`（預設 30 ms，測試設 0）傳入。
  - 實作補充（申請編號）：請求裡有 `create_after_sales_request` 的成功結果時，回應文字裡的 `AS\d{12}` 一律換成最近一次建立的 `request_no`（錄製時的編號換成回放當下建立的編號）；沒有建立結果就原樣保留。
- Runner 的對應：
  - `ReplayDivergedException` → `StopReason = ReplayDiverged`，固定說明「回放中止：目前資料或故障開關和錄製時不同，請到展示控制台重置示範資料後再試。」
  - `ReplayExhaustedException` → `ReplayFinished`，說明「回放的錄製內容已播完。」
  - 實作補充：兩者都以固定說明取代回覆（程式回覆）；若本輪已建立申請，回覆是「申請已建立（編號）…」再接上這段說明。
- 提供 `GetReplayScriptAsync(Guid sessionKey)` → 下一個待執行的 `AgentRecordedTurn`（給頁面帶入下一句或提示該按確認／上傳照片），以及 `bool IsReplay`。
  - 實作補充：回傳 `AgentReplayScript(IsReplay, NextTurn)`（`NextTurn` 是 `AgentRecordedTurnView`，播完或不是回放為 null）。
  - 實作補充：每個顧客動作先與錄製的下一個動作核對。以下情況都回程式固定回覆、停止原因 `ReplayActionMismatch`（Task 9 起從 `ReplayDiverged` 分出；白話「請送出錄製時的內容」，停止說明「回放中請按照錄製時的步驟操作。」）：
    - 種類不同。
    - 句子不同（去頭尾空白後逐字比對）：「回放中請送出錄製的句子」。
    - 照片張數不同：「回放中請上傳與錄製時相同張數的照片」。
    - 確認／取消與錄製時相反：「回放中請按下錄製時的選擇：確認送出」或「…：取消」。這個檢查在搶下確認卡之前，所以卡片維持未決。
  - 實作補充：上述情況不呼叫模型、不寫軌跡、不前進游標，改送正確的動作就能繼續。錄製的動作已播完則回 `ReplayFinished`。
- 新會話開始時重設游標（`StartNewAsync`）。
  - 實作補充：同一會話兩個分頁同時送出會各自前進游標，之後就會回放中止（示範限制）。

- [ ] **Step 1: 寫失敗測試**：
  - `Replay_FlagshipA_ReproducesRepliesAndCreatesRequestWithoutModel`（`InnerClientFactory` 若被呼叫就失敗）
  - `Replay_DoesNotTouchQuotaOrUsage`
  - `Replay_ToolResultDiffers_StopsWithReplayDiverged`（錄製後改庫存）
  - `Replay_Exhausted_StopsWithReplayFinished`
  - `Replay_ScriptProvidesNextCustomerLine`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過
- [ ] **Step 5: 檢查點**

### Task 7: 展示身分切換

**Files：**
- Create：
  - `Business/Services/Other/DemoSwitchTicketService.cs`（Singleton）
  - `Web/Components/Auths/DemoSwitch.razor(.cs)`（`@page "/Auths/DemoSwitch"`，靜態 SSR，`[Authorize]`）
  - `Web/Auth/DemoClaims.cs`
- Modify：
  - 版面：`Components/Layout/MainLayout.razor(.cs)`、`ShopLayout.razor(.cs)`
  - 其他：`AuditActions.cs`、`PageAuthorizationTests`、`PageHelpCatalogTests.RoutesWithoutHelp`
- Test：`DemoSwitchTests.cs`（`IntegrationHostCollection`）、`Components/ShopLayoutTests.cs`

**Interfaces / behaviour：**
- `DemoSwitchTicketService.Issue(int presenterUserId, string targetAccount)` → `string ticket`：一次性、60 秒有效、記憶體保存。
- `DemoSwitchTicketService.IssueSwitchBack(int currentUserId)` → `string ticket`。
- `DemoSwitchTicketService.TryRedeem(string ticket, out DemoSwitchTicket t)`。
- `DemoSwitch.razor`（`OnInitializedAsync`，透過 `HttpContext`）。共同前提：示範模式開啟，且票券有效、未使用、建立者等於目前 Cookie 的 `Sid`。
  - 切到顧客：
    1. 目前使用者為管理員。
    2. 目標帳號必須在 `DemoSeedData.Accounts` 內、啟用中、且有 `RoleViewId`。
    3. 以 `ISecurityStampService.EnsureAsync` 取得目標的 stamp。
    4. 用 `CookieClaims.Create(target)` 加上 `DemoClaims.PresenterId`、`DemoClaims.PresenterStamp` 簽發 Cookie（工作階段 Cookie）。
    5. 寫稽核 `Demo.SwitchIdentity`。
    6. 導向：顧客 → `/shop`；客服或店家 → `/App`。
  - 切回展示者：
    1. Cookie 必須帶有展示者 claims，展示者仍為啟用中的管理員，且 stamp 有效。
    2. 以展示者身分簽回 Cookie，寫稽核 `Demo.SwitchBack`，導向 `/demo-console`。
  - 任何一項不符：寫失敗稽核，並導向 `/App`，附帶錯誤提示。
- 版面入口：
  - `MainLayout` 的使用者選單：管理員且示範模式開啟時，顯示「切換展示身分」，連到 `/demo-console#identity`。
  - `ShopLayout` 與 `MainLayout`：Cookie 帶展示者 claim 時，頂端顯示「展示模式：目前以 {名稱} 身分檢視」與「回到展示者」按鈕（取得票券後 `forceLoad` 導向）。
  - 有故障開關啟用時，顯示「故障模擬中」標籤。

- [ ] **Step 1: 寫失敗測試**：
  - `Switch_AdminToCustomer1_SignsInAsCustomer`
  - `Switch_NonAdmin_IsRejected`
  - `Switch_DemoDisabled_IsRejected`
  - `Switch_TargetNotDemoAccount_IsRejected`
  - `Ticket_ReusedOrExpired_IsRejected`
  - `SwitchBack_WithoutPresenterClaim_IsRejected`
  - `SwitchBack_RestoresPresenter`
  - `ShopLayout_PresenterClaim_ShowsBackButton`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `PageAuthorizationTests`、`LogoutConfirmationConventionTests`）
- [ ] **Step 5: 檢查點**

### Task 8: 展示控制台頁 `/demo-console`

**Files：**
- Create：
  - `Components/Pages/Admins/DemoConsolePage.razor`
  - `Components/Views/Admins/DemoConsoleView.razor(.cs/.css)`
  - `Business/Startup/DemoScenarios.cs`（靜態劇本定義）
  - `Datas/Help/demo-console.md`（含 BOM，七段）
- Modify：
  - 選單：`MagicObjectHelper`（`角色_展示控制台 = "展示控制台"`）、`Menu.json`（群組 8 下新增 id `69`「展示控制台」，icon `slideshow`）、`SidebarMenuService.MenuPermissionMap`、`HelpTopics.json`
  - 測試清單：`AdminOnlyPermissionTests.AdminOnlyPermissionKeys`、`MenuPermissionConsistencyTests.AdminOnlyViews`、`MenuIconTests.AllowedIcons`
- Test：`Components/DemoConsoleViewTests.cs`

**內容（`CheckIsAdmin`；示範模式關閉 → 顯示「示範模式未開啟」並全部停用）：**
1. **劇本**：`DemoScenarios.All`（`Code, Title, Account, CustomerLines[], Faults[], TalkingPoints[]`），至少包含：
   - A 換碼（customer1）
   - B 一句兩事（customer1）
   - C 退貨逾期（customer2）
   - D 物流催件（customer2，`ShipmentAnomaly` 可選）
   - F1 訂單逾時（`OrderServiceTimeout`）
   - F2 權限過期（`PermissionExpired`）
   - F3 規則服務停擺（`PolicyServiceUnavailable`）
   - F4 規則衝突（`PolicyConflict`）
   - F5 無適用規則（`PolicyNotFound`）

   每個劇本有「準備此劇本」按鈕，依序執行：
   1. `ConfirmDialog` 確認
   2. 重置
   3. 設定該劇本的故障（其餘關閉）
   4. 若選了回放且有金劇本 → 設定回放
   5. 以票券切換成劇本帳號
   6. 導向 `/shop/assistant`
2. **故障開關**：7 個開關；目前啟用的故障用醒目標籤顯示；有「全部關閉」。
3. **資料**：「一鍵重置示範資料」（`ConfirmDialog.AskDestructiveAsync`）；顯示上次重置時間。
4. **模式**：即時／回放切換；回放時選擇錄製（預設該劇本的金劇本）；未設定 AI 時提示只能用回放。
5. **錄製清單**：時間、標題、劇本、模型、成本；可「設為金劇本」「刪除」（批次刪除依慣例）。
6. **身分切換**（`#identity`）：四個示範帳號各一顆「以此身分檢視」。
7. **幕後面板預設**：開／關。

- 實作補充：稽核由控制台以登入的管理員寫（`DemoControlService` 是 Singleton、不寫稽核）：`Demo.FaultChanged`（每個有變的故障一筆，`fault=…; state=on|off`）、`Demo.ModeChanged`（`mode=Live` 或 `mode=Replay; recordingId=…`）、`Demo.RecordingGolden`（`recordingId=…; scenario=…`）、新增的 `Demo.RecordingDelete`（`recordingId=…`）。重置的稽核仍由 `DemoResetService` 寫；「上次重置」取最新一筆成功的 `Demo.Reset` 稽核（`DemoResetService.GetLastResetAtAsync`）。
- 實作補充：「準備此劇本」在確認窗就講清楚模式——選了回放但劇本沒有金劇本時改用即時（不默默失敗）；切換票券帶 `returnUrl`（顧客劇本 `/shop/assistant`），`/Auths/DemoSwitch` 只接受站內路徑，否則照預設。刪除回放中的錄製後自動改回即時。故障名稱與說明集中在 `Share/Demo/DemoFaultLabels`（顧客頁的「故障模擬中」共用）。
- 實作補充（Task 7 補強）：`/Auths/DemoSwitch` 失敗時，目前是顧客（沒有後台首頁、有顧客商店）就導向 `/shop?demoSwitch=failed`；切回前另以資料庫比對目前示範帳號 Cookie 的版本（`reason=SessionRevoked`，登出到登入頁）。

- [ ] **Step 1: 寫失敗測試**：
  - `Console_NonAdmin_Denied`
  - `Console_DemoDisabled_ShowsNotice`
  - `Console_ToggleFault_PersistsAndShowsBadge`
  - `Console_PrepareScenario_ResetsSetsFaultsAndNavigatesToSwitch`
  - `Console_SetGolden_MarksRecording`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `MenuPermissionConsistencyTests`、`AdminOnlyPermissionTests`、`MenuIconTests`、`PageHelpCatalogTests`、`BatchDeleteConventionTests`、`FormModalConventionTests`）
- [ ] **Step 5: 檢查點**

### Task 9: 顧客頁回放體驗與面板

**Files：** Modify `Components/Views/Shop/Assistant/ShopAssistantView.razor(.cs/.css)`、`AgentBackstagePanel.razor`、`AgentStepTimeline.razor(.css)`；Test `Components/ShopAssistantViewTests.cs`

**要點：**
- **回放模式：**
  - 頂端顯示「回放中（錄製於 {時間}）」標籤。
  - 輸入框自動帶入下一句（唯讀），只能按「送出」。
  - 下一步是確認／取消時，只允許按錄製時的那一個；是上傳照片時，提示「請上傳任一張照片以繼續」。
  - 回放結束或中止時顯示固定說明，並提供「回到展示控制台」連結（展示者身分時）。
- **幕後面板：**
  - 預設開關取自 `DemoControlService.BackstageDefaultOn`。使用者在本機的偏好（localStorage）優先。
  - `Warning` 事件以紅色呈現（色票別名）。
  - 故障模擬中顯示「故障模擬中：{清單}」。
- **還原：** 改用 Task 2 的 `AgentHistoryItem`／`GetTraceEventsAsync`。
- 實作補充：回放中頁面不檢查 AI 設定（不顯示「AI 服務尚未設定」、不擋送出）；`AgentReplayScript` 多帶 `RecordedAt`（錄製建立時間）給標籤用，格式 `yyyy/MM/dd HH:mm`。
- 實作補充：顧客動作與錄製不同改用獨立的停止原因 `ReplayActionMismatch`（可重試）；頁面只補一則系統訊息、拿掉那個顧客泡泡、確認卡維持未決、輸入框重新帶入同一句。`ReplayDiverged`（摘要不同，要重置）與 `ReplayFinished`（含錄製的動作全部播完）才顯示固定說明（`AgentStopLabels` 白話）並鎖住輸入框；按「新對話」從頭開始。
- 實作補充：「回到展示控制台」只在 Cookie 帶展示者 claim 時出現；`/demo-console` 只限管理員，所以按下去取一張切回展示者的票券（`IssueSwitchBack`）整頁導到 `/Auths/DemoSwitch`，切回後導到 `/demo-console`。
- 實作補充：上傳照片的提示為「請上傳任一張照片以繼續」；照片張數要與錄製時相同才能按「送出照片」（代送的「（已上傳 N 張照片）」會進模型請求，張數不同就對不上，而且照片放進暫存區後無法取回）。
- 實作補充：`Warning` 列加 `is-warning`，以 `theme.css` 新增的別名 `--shop-step-warning`（→ `--app-error`）標紅（後台「Agent 執行紀錄」窗共用同一個時間軸元件，也一併標紅）。「故障模擬中：{清單}」在幕後面板標頭下方，名稱與順序取 `DemoFaultLabels`，訂閱 `DemoControlService.Changed`。
- 實作補充：幕後面板預設取 `BackstageDefaultOn`；本機 localStorage 存過 "0"／"1" 時以它為準。

- [ ] **Step 1: 寫失敗測試**：
  - `Replay_ShowsBadgeAndPrefilledReadOnlyInput`
  - `Replay_ConfirmStep_OnlyRecordedChoiceEnabled`
  - `Backstage_DefaultFromDemoControl`
  - `Backstage_WarningRenderedRed`
  - `Backstage_ShowsActiveFaults`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `ThemeConventionTests`）
- [ ] **Step 5: 檢查點**

### Task 10: 文件、品質關卡與實機驗證

**Files：**
- `appsettings.json`（`1.0.4 → 1.0.5`）
- `docs/features/售後Agent示範系統.md`（M3 章節）
- 企劃規劃說明書：
  - §5.0 展示前檢查改為「控制台→準備劇本」
  - §5.4 故障示範改用控制台開關
  - §5.7 出狀況時改為「切回放」的實際步驟
  - §5.2.1 加 M3 驗證步驟
- 企劃書 §4：故障模擬改為〔已提供〕
- 其他文件：設計文件 M3 狀態、`docs/prd/README.md`、`docs/architecture/資料模型與資料庫.md`（Demo 與錄製實體）、`docs/guides/畫面與欄位字典.md`（`/demo-console`、`/Auths/DemoSwitch`）、`docs/prd/稽核紀錄-prd.md`（`Demo.*` 稽核碼與總數）、`docs/operations/正式部署與安全檢查清單.md`（Production 禁止示範模式）、索引
- 專案記憶 `project-m2-hardening-backlog` 由 controller 刪除

- [ ] **Step 1: 更新文件**（UTF-8 含 BOM）
- [ ] **Step 2: `pwsh ./scripts/Invoke-QualityGate.ps1`** → `QUALITY GATE PASSED`
- [ ] **Step 3: 實機驗證**（金鑰在 user secrets，不得寫出）：
  1. 控制台「準備劇本 A」→ 即時跑完並自動錄製 → 設為金劇本。B 同樣做一次。
  2. 重置 → 切回放 → 劇本 A、B 回放成功，且 Token 用量沒有新增。
  3. 錄製後改庫存 → 回放中止，停止原因為 `ReplayDiverged`。
  4. F1 訂單逾時：說明含「不代表您沒有這筆訂單」。
  5. F4 規則衝突：不出確認卡、轉人工。
  6. 以 `ReasoningEffort=low` 跑一次劇本 A 的工具迴圈：成功就在文件中標為已驗證，失敗就保持「不建議」。
- [ ] **Step 4: 檢查點**：回報結果與建議 commit 訊息「M3：展示控制台、故障模擬、重置、錄製回放、身分切換與 M2 補強；版本 1.0.4 → 1.0.5」

> 實作補充（Task 10）：實機驗證以暫時的整合測試程式（`WebApplicationFactory`＋Development 設定＋user secrets，直接驅動 `AfterSalesAgentRunner`、`DemoResetService`、`DemoControlService`、`AgentRecordingService`，等同控制台「準備此劇本」的步驟，但不經畫面、也不寫控制台的稽核）對開發資料庫執行，驗證後即刪除，不提交。Step 3 六項全部通過；另驗證回放中打開「規則衝突」故障同樣中止。`ReasoningEffort=low` 已驗證可用（`none` 會被模型拒絕）。結果寫在 [售後 Agent 示範系統 §6.11](../features/售後Agent示範系統.md) 與[開發歷程紀錄](售後Agent示範系統開發歷程紀錄.md) §5。

---

## 執行方式

沿用：**Subagent-driven**，新分支 `feature/m3-demo-safety`，以 git index 作檢查點，不自動 commit。

## 驗證總結

- 自動：各 Task 測試＋品質關卡（含 Production 禁止示範模式）。
- 實機：Task 10 Step 3 全部項目。
- 計畫核准後另存 `docs/prd/售後Agent展示系統-M3實作計畫.md`（含 BOM）並列入索引。


> 返回 [PRD 主控台](README.md)
