# 售後 Agent 展示系統 M5 Implementation Plan（多 Agent 交接＋長期記憶）

- 文件版本：0.2
- 文件狀態：已完成（M5 於 1.0.7 完成）
- 現行系統版本：1.0.8
- 首次實作版本：1.0.7
- 最後核對日期：2026/10/24

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

M4（客服後台 AI，1.0.6）已合併到 main（`ea599dc`）。M5 要補上設計 §4.7 的兩項能力。

**一、多 Agent 交接（Handoff）**
- 顧客端可在「單一 Agent」與「多 Agent」之間切換。
- 多 Agent 模式下，由「分流 Agent」把對話交給換退貨專員、品質專員或物流專員。
- 每位專員只拿得到自己需要的工具。
- 幕後面板顯示交接事件。

**二、長期記憶**
- 每次呼叫模型前，自動注入顧客的偏好，並標註「來自記憶，需與顧客確認」。
- 寫入記憶要經過確認卡。
- 顧客可以在「我的 AI 記憶」頁檢視、刪除自己的記憶。
- 記憶永遠不當作業務事實。

**使用者已決定：**
- **切換位置**：在顧客頁開新對話時切換（自動開新對話，對話中途不能換）；展示控制台的劇本可以預設模式。
- **記憶內容**：尺碼、顏色偏好，加上溝通偏好（例如「偏好簡短回覆」）。
- **記憶範圍**：兩種模式讀寫同一份記憶。
- **新增劇本 E**：記住偏好 → 開新對話時 Agent 主動帶出並確認 → 在記憶頁刪除 → 之後不再使用。
- **另外新增一份獨立文件「MAF 功能運用說明」**：M5 完成後寫，先白話、再技術細節，最後附總結。

**時間預估**（依開發歷程紀錄 §8 調整後的公式：第一次用的框架、併發相關任務以 45～60 分計）：

| 項目 | 預估 |
|---|---|
| 規劃 | 約 30 分 |
| 試跑 | 約 40 分 |
| 8 個任務（其中 3 個大任務） | 約 4.5 小時 |
| 最終審閱 | 約 45 分 |
| 文件與 MAF 說明 | 約 40 分 |
| 實機驗證 | 約 30 分 |
| **合計** | **約 6.5～8 小時** |

**Goal:** 多 Agent 交接模式＋長期記憶（含確認卡、記憶頁、劇本 E），全部通過品質關卡並以實機驗證；另新增 MAF 功能運用說明文件。

**Architecture:**
- **多 Agent**：用 MAF `AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)` 建立交接 Workflow，以 `.AsAIAgent()` 包成 `AIAgent`。
  - 沿用現有 Runner 的 `RunStreamingAsync`＋`AgentSession` 流程。
  - Workflow 的 checkpoint 改用 M4 的 `SqliteWorkflowCheckpointStore`。
  - 每位專員都是 `ChatClientAgent`：共用同一條模型鏈（Tracing／Recording／Replay），掛同一個 Guard 中介層，各自只拿自己的工具子集。
  - 交接事件由程式判斷產生，判斷依據是發言 Agent 改變，以及出現 `handoff_to_N` 工具呼叫。
- **長期記憶**：
  - 讀取：自訂 `CustomerMemoryContextProvider : AIContextProvider`，以程式固定的 `AIContext.Instructions` 注入記憶，不用使用者訊息，因此不影響回放摘要。
  - 寫入：`remember_preference` 以 `ApprovalRequiredAIFunction` 包裝，確認卡改為依種類區分。
  - 資料：存在 `CustomerMemory` 表。
- **先試跑**：開工前先做一個試跑任務，把框架行為鎖成測試，再寫正式程式。這是 M4 學到的教訓。

**Tech Stack:** .NET 10、Blazor Server、AntDesign、EF Core SQLite、MAF 1.25（Agents.AI、Agents.AI.Workflows）、MEAI 10.10、xUnit＋bUnit。

**Spec:** `docs/prd/售後Agent展示系統-設計.md` §4.7、§3.1（記憶寫入也要確認、錄製涵蓋 Handoff）、§6 M5 驗證（交接事件出現在面板；刪除記憶後不再注入）。

**已驗證的 MAF 1.25 事實**（給實作者，規劃時已讀原始碼核對）：

1. **Handoff Workflow 的建立方式**
   - `HandoffWorkflowBuilder`：入口是 `CreateHandoffBuilderWith(AIAgent)`；不要用 obsolete 的 `HandoffsWorkflowBuilder`（多一個 s）。
   - 可用方法：`WithHandoff(from, to, reason)`、`WithHandoffs(from, IEnumerable to)`、`EnableReturnToPrevious()`、`WithHandoffInstructions`、`EmitAgentResponseUpdateEvents`、`Build()`。
2. **交接工具名稱是 `handoff_to_1`、`handoff_to_2`…（依序號，不是 Agent id），而且沒有交接事件型別。**
   - 要從 `Update.AuthorName` 或 `ExecutorId` 的改變，以及 `handoff_to_N` 呼叫來判斷交接。
   - 有交接目標的 Agent 會被強制 `AllowMultipleToolCalls=false`。
3. **工具核准會以 `RequestInfoEvent` 出現。**
   - 包成 Agent 後，會變成 update 裡的 `ToolApprovalRequestContent`，其 RequestId 是外部 id。
   - 回覆方式：送回含 `ToolApprovalResponseContent`（同一個 RequestId）的訊息。
   - 官方測試涵蓋：`HandoffOrchestrationTests.Handoffs_TwoTransfers_SecondAgentUserApproval…`。
4. **`workflow.AsAIAgent(...)` 的使用要點**
   - 支援 `RunStreamingAsync(messages, session)` 以及 `Serialize`／`DeserializeSessionAsync`。
   - 一定要給 `executionEnvironment: InProcessExecution.OffThread.WithCheckpointing(checkpointManager)`，否則 session 會把整個 checkpoint 庫塞進去，越來越大。
   - 包裝後的 Agent 不能再掛函式中介層，中介層要掛在各個參與者上。
   - 每個參與者的 `ChatClientAgentOptions.Id`／`Name` 必須固定，重建時 executor id 才會一致。
5. **`AIContextProvider` 的實作方式**
   - 覆寫 `ProvideAIContextAsync(InvokingContext, ct)`，回傳 `AIContext{ Instructions, Messages, Tools }`。
   - 用實例掛在 `ChatClientAgentOptions.AIContextProviders`（1.25 沒有 factory）。
   - 在 Handoff 的參與者上也有效；但 session 是 executor 內部建立的，所以顧客 Id 要在建構 provider 時傳入（每輪建立一次）。
6. **1.25 不存在的名稱**：`AIContextProviderFactory`、`HandoffEvent`、`AgentThread`、`FunctionApprovalRequestContent`、`Mem0Provider`、`InMemoryChatMessageStore`。

## Global Constraints

- **沿用 M1–M4 全部慣例**（權威是 `docs/architecture/開發慣例與限制速查.md`，含 §6.30 M4 踩雷點）：
  - 資料存取：分層、`IDbContextFactory` 單一建構子。
  - 日誌與稽核：英文日誌，不含個資；`AuditActions` 常數。
  - UI：色票只在 `theme.css :root` 別名；子元件樣式不能依賴父元件的 scoped CSS。
  - 文件：UTF-8 含 BOM。
- **時間**：一律用 `TimeProvider.GetLocalNow().DateTime`。
- **套件**：只寫 `Directory.Packages.props`，維持 MAF 1.25.0。
- **不自動 commit**：實作者不得 `git add`／`git commit`；controller 以 git index 作檢查點。
- **版本**：M5 結束時 `1.0.6 → 1.0.7`。
- **記憶寫入規則**：一律經確認卡，未確認絕不寫入；程式重核內容，含電話、Email、地址、姓名的一律拒絕；記憶只代表偏好，不是事實。
  - 注入文字固定是「以下來自顧客的長期記憶（來自記憶，需與顧客確認，不可當作訂單或規則等業務事實）」。
  - 注入文字由程式產生，不放在可編輯的提示詞裡。
- **記憶隔離**：顧客 A 的記憶絕不注入顧客 B 的對話；刪除後下一次呼叫立即不再注入（不快取）。
- **多 Agent 安全規則**：每位專員只拿得到自己的工具子集；Guard 的停止規則（forbidden、timeout、unavailable 終止；not_found、no_policy、conflict 封鎖寫入）在兩種模式完全相同。
- **回放**：絕不呼叫真實模型；回放必須以錄製當時的模式重建 Agent 結構。
- **個資**：模型看不到姓名、帳號、地址、手機、使用者 Id；所有新的送模型路徑都要經過既有的遮蔽或投影。
- **實機模型限制**：Responses API、`store=false`、不送 temperature；金鑰只在 user secrets，絕不寫出。
- **回報語言**：給使用者看的說明一律用繁體中文。

## Review Focus

1. **模型呼叫 `remember_preference` 但顧客按取消或關掉頁面**：不得寫入任何記憶。→ Task 3 `Remember_WithoutConfirmation_NoRow`、`Remember_Rejected_NoRow`。
2. **記憶內容夾帶個資**（電話、Email、地址、姓名）**或超長**：程式拒絕，不出確認卡。→ Task 2 `Upsert_RejectsPersonalData`；Task 3 `Remember_PersonalData_NoCard`。
3. **顧客在記憶頁刪除後，下一輪就不能再被注入**；另一位顧客永遠看不到這份記憶。→ Task 3 `Provider_AfterDelete_NotInjected`、`Provider_OtherCustomer_NeverInjected`。
4. **多 Agent 模式下，專員呼叫不屬於自己的工具**（模型亂編名稱）：不會被執行；物流專員查不了庫存。→ Task 5 `Specialist_ToolSubset_Enforced`。
5. **多 Agent 模式在確認卡等待時應用程式重啟**：重新整理後按確認，仍能正確建立申請，且只建立一筆。→ Task 5 `Handoff_ConfirmAfterRestart_CreatesOnce`。

---

### Task 1: 試跑——鎖定 MAF Handoff 與 AIContextProvider 的行為

**Files：** Create `CustomerHelpAgent.Tests/MafHandoffBehaviorTests.cs`（長期保留，作為框架行為測試；升級 MAF 時要重跑）

**要用測試證明的事**（用假 `IChatClient` 依序腳本化回應）：
- `Handoff_Wrapped_StreamsAuthorNamePerAgent`：`AsAIAgent` 之後，update 的 `AuthorName` 能分辨分流 Agent 與專員；能取得 `handoff_to_N` 呼叫並對應到目標 Agent。
- `Handoff_ParticipantMiddleware_RunsAndTerminates`：參與者上的 `AsBuilder().Use(guard)` 會執行，`context.Terminate=true` 能結束該輪。
- `Handoff_ApprovalSurfacesAndResumesAfterSessionRoundTrip`：專員的 `ApprovalRequiredAIFunction` 會出現 `ToolApprovalRequestContent`。接著做 session 序列化 → 丟掉 Agent → 用相同 id 重建 → 反序列化 → 送 `ToolApprovalResponseContent`，該工具會執行一次。checkpoint 要用 `SqliteWorkflowCheckpointStore`（M4）。
- `Handoff_SessionJson_StaysSmallWithCheckpointManager`：有 `WithCheckpointing` 時，session JSON 不含 checkpoint 內容。
- `Handoff_ReturnToPrevious_NextTurnGoesToSpecialist`：開啟 `EnableReturnToPrevious` 後，下一輪直接由上一位專員接手。
- `ContextProvider_InjectsInstructions_SingleAndHandoff`：在單一 `ChatClientAgent` 與 Handoff 參與者上，`ProvideAIContextAsync` 的 Instructions 都會出現在送給模型的 `ChatOptions.Instructions`，且不在 user 訊息內。
- `ReplayDigest_StableAcrossHandoff`：同一段劇本跑兩次，`ModelRequestDigest` 序列相同。

- [ ] **Step 1–3：** 寫測試 → 跑 → 結果寫進報告（每項通過或失敗、實際觀察到的行為）
- [ ] **Step 4：** 若某項不成立，在報告中提出替代做法，例如改成程式自己做路由：分流 Agent 呼叫 `route_to_specialist` 工具，由程式換 Agent。controller 依結果裁定後續任務。
- [ ] **Step 5：** 檢查點

### Task 2: 記憶資料層

**Files：**
- Create：
  - `AccessDatas/Models/Agents/CustomerMemory.cs`＋migration `AddCustomerMemory`
  - `Share/Agents/MemoryEnums.cs`
  - `Business/Services/DataAccess/CustomerMemoryService.cs`
  - `Business/Helpers/MemoryContentPolicy.cs`
- Modify：
  - 重置與清除：`DemoResetService`（刪除示範顧客的記憶）、清除已刪除帳號的排程作業（一併刪除記憶）
  - 稽核與 DI：`AuditActions`（`Memory.Saved`／`Memory.Deleted`）、DI、`DataAccessServiceLifetimeTests`
- Test：`CustomerMemoryServiceTests.cs`

**Interfaces：**
- `enum MemoryKind { ShoeSize = 1, ClothingSize = 2, PreferredColor = 3, Communication = 4 }`
- `CustomerMemory { Id, CustomerUserId, Kind, Value(≤60), SourceSessionKey?, CreatedAt, UpdatedAt }`，`(CustomerUserId, Kind)` unique，一種偏好只存一筆，新值覆蓋舊值。
- `MemoryContentPolicy.Validate(MemoryKind, string)` 回傳 `(bool Ok, string? Normalized, string? Reason)`：
  - 鞋碼：35–46，可有半號。
  - 衣服尺碼：XS–XXL。
  - 顏色：中文顏色詞，≤10 字。
  - 溝通偏好：≤40 字。
  - 一律拒絕電話、Email、地址樣式，以及顧客姓名、帳號。
- `CustomerMemoryService`：
  - `GetAsync(int customerUserId)`、`UpsertAsync(customerUserId, kind, value, sessionKey)` 回傳 `MemoryResult`
  - `DeleteAsync(customerUserId, memoryId)`（只能刪自己的）、`DeleteAllAsync(customerUserId)`
  - 寫入稽核。

- [ ] **Step 1：** 寫失敗測試：
  - `Upsert_SameKind_Overwrites`
  - `Upsert_RejectsPersonalData`
  - `Upsert_InvalidSize_Rejected`
  - `Delete_OtherCustomersMemory_Denied`
  - `Reset_DeletesDemoCustomersMemory`
  - `Purge_DeletesMemoryOfPurgedAccount`
- [ ] **Step 2–4：** 失敗 → 實作（含 migration）→ 通過
- [ ] **Step 5：** 檢查點

### Task 3: 記憶接上 Agent（單一模式）

**Files：**
- Create：`Web/Agents/AfterSales/CustomerMemoryContextProvider.cs`、`MemoryConfirmationCard`（或為 `ConfirmationCard` 加 `Kind`）
- Modify：
  - Agent：`AfterSalesAgentTools.cs`（`remember_preference`）、`AfterSalesAgentGuard.cs`（描述、`Source=Memory`、記憶寫入不受「封鎖寫入」影響）、`AfterSalesAgentRunner.cs`（`PrepareConfirmationAsync` 依工具名稱分流；確認後寫入）、`AfterSalesAgentPromptDefaults`
  - 客服端：`HandoverSummaryBuilder`（跳過記憶卡）、`ModelHistoryReviewer`（排除記憶工具結果）
- Test：`CustomerMemoryAgentTests.cs`、既有 Runner／Tools／Handover／PreReview 測試

**Behaviour：**
- **Provider**：每輪以 `customerUserId` 建立；在 `ProvideAIContextAsync` 讀取最新記憶，有記憶才回傳固定格式的 Instructions。每輪只發一次 `Source=Memory` 的軌跡事件，例如「注入記憶：鞋碼 41（需與顧客確認）」。
- **工具** `remember_preference(kind: shoe_size|clothing_size|color|communication, value)`：用 `ApprovalRequiredAIFunction` 包裝。
  - 組卡前先以 `MemoryContentPolicy` 重核；不通過就不出卡，並以 `CreateResponse(false, 原因)` 回給模型。
  - 確認卡內容例如「要我記住：鞋子平常穿 41 碼嗎？之後的對話會先向您確認。」
  - 確認後呼叫 `CustomerMemoryService.UpsertAsync`。
  - 寫入成功、取消、失敗都要記軌跡。
- **提示詞**：記憶只能用來「詢問確認」，不能直接當作申請的規格；顧客明說的內容優先。

- [ ] **Step 1：** 寫失敗測試：
  - `Remember_WithoutConfirmation_NoRow`
  - `Remember_Confirmed_Saved`
  - `Remember_Rejected_NoRow`
  - `Remember_PersonalData_NoCard`
  - `Provider_InjectsAsInstructionsNotUserMessage`
  - `Provider_AfterDelete_NotInjected`
  - `Provider_OtherCustomer_NeverInjected`
  - `Handover_SkipsMemoryCard`
  - `HistoryReviewer_ExcludesMemoryResults`
- [ ] **Step 2–4：** 失敗 → 實作 → 通過（含既有 Agent 測試）
- [ ] **Step 5：** 檢查點

### Task 4: 「我的 AI 記憶」頁

**Files：**
- Create：`Components/Pages/Shop/ShopMemoryPage.razor`（`/shop/memory`）、`Components/Views/Shop/ShopMemoryView.razor(.cs/.css)`
- Modify：`ShopLayout.razor`（導覽加「我的 AI 記憶」）、`PageHelpCatalogTests.RoutesWithoutHelp`
- Test：`Components/ShopMemoryViewTests.cs`

**Behaviour：**
- 頁面套用 `CheckAccessPage(角色_顧客商店)`。
- 列出記憶：種類、內容、記住時間；說明「這些是 AI 記住的偏好，對話時會先向您確認」。
- 每筆可刪除，另有「全部刪除」，都以 `ConfirmDialog` 確認。
- 沒有記憶時顯示空狀態。

- [ ] **Step 1：** 寫失敗測試：
  - `Memory_ListsOnlyOwnMemories`
  - `Memory_Delete_RemovesAndRefreshes`
  - `Memory_DeleteAll_AsksConfirm`
  - `Memory_Empty_ShowsHint`
- [ ] **Step 2–4：** 失敗 → 實作 → 通過（含 `PageAuthorizationTests`、`ThemeConventionTests`）
- [ ] **Step 5：** 檢查點

### Task 5: 多 Agent 交接結構與 Runner

**Files：**
- Create：`Web/Agents/AfterSales/HandoffTopologyFactory.cs`、`AfterSalesHandoffPromptDefaults.cs`
- Modify：
  - 會話與 Runner：`AgentSessionRecord`（加 `AgentMode Mode`）＋migration `AddAgentSessionMode`、`AgentSessionStore.CreateAsync(..., mode)`、`AfterSalesAgentRunner.cs`（依模式建 Agent；`StartNewAsync(mode)`；確認流程與還原適配 Workflow session）
  - 工具與 Guard：`AfterSalesAgentTools.CreateFunctions(params string[] names)`、`AfterSalesAgentGuard`
  - 列舉：`Share/Agents/AgentEnums.cs`（`AgentMode { Single = 1, Handoff = 2 }`、`AgentStepKind.Handoff`）
  - 提示詞：`PromptTemplateKeys`（加入 `AfterSalesTriage`、`AfterSalesExchangeSpecialist`、`AfterSalesQualitySpecialist`、`AfterSalesLogisticsSpecialist`，共 11 個）、`AiPromptGuardrails`、`PromptTemplateView.TitleOf`、`PromptTemplateTests`
  - 計量與設定：`TokenUsageOperations.AfterSalesAgentHandoff = "AI 售後多 Agent"`、`AfterSalesAgentSettings.HandoffMaxSteps`（預設 14）
- Test：`AfterSalesHandoffRunnerTests.cs`

**Behaviour（依 Task 1 結果調整）：**
- **參與者**（固定 `Id`／`Name`；工具子集；每個都掛同一個 Guard 與記憶 provider）：

  | 參與者 | 工具 |
  |---|---|
  | 分流 Agent | `query_my_orders`、`remember_preference` |
  | 換退貨專員 | 訂單、規則、庫存、建立申請、記憶 |
  | 品質專員 | 訂單、規則、要求上傳照片、建立申請、記憶 |
  | 物流專員 | 訂單、物流、規則、建立申請、記憶 |

- **交接路線**：分流 → 三位專員；三位專員 → 分流（處理範圍外的事要交回分流）。開啟 `EnableReturnToPrevious`。
- **模型鏈**：共用同一條 Tracing／Recording／Replay 鏈，錄製仍走 `"customer"` 這個 stream，依序進行。`TracingChatClient` 的每輪步數上限改用 `HandoffMaxSteps`。
- **交接事件**：發言 Agent 改變時，發出 `AgentStepKind.Handoff` 事件（`Source=Program`），白話寫「分流 Agent → 品質專員：{理由}」，`RawJson` 帶 from、to、reason。
- **確認卡**：從 update 中的 `ToolApprovalRequestContent` 取得外部 RequestId，存進 `AgentConfirmation`。確認時送 `ToolApprovalResponseContent`；工具名稱照常分流（建立申請或記憶）。
- **會話**：Workflow session 以 `SerializeSessionAsync` 存進 `StateJson`，checkpoint 存 SQLite。版本衝突保護沿用 `StateVersion`。

- [ ] **Step 1：** 寫失敗測試：
  - `Handoff_TriageToQuality_EmitsHandoffEvent`
  - `Handoff_ScenarioB_TwoSpecialists`
  - `Specialist_ToolSubset_Enforced`
  - `Handoff_ConfirmCreatesRequest`
  - `Handoff_ConfirmAfterRestart_CreatesOnce`
  - `Handoff_GuardForbidden_Terminates`
  - `Handoff_StepBudget_Stops`
  - `Handoff_MemoryInjectedForSpecialists`
  - `SingleMode_Unchanged`（既有測試全過）
- [ ] **Step 2–4：** 失敗 → 實作（含 migration）→ 通過
- [ ] **Step 5：** 檢查點

### Task 6: 管道標示、錄製回放與劇本

**Files：**
- Modify：
  - 管道：`Share/Shop/AfterSalesLabels.cs`（`AgentHandoff` 改名為「AI 多 Agent」）；新增 `AfterSalesChannelExtensions.IsAi()`，並取代程式中所有 `== AfterSalesChannel.Agent` 的判斷（約 10 處：初審 hook、協調器、補建、審核頁、管道徽章）
  - Agent：`AfterSalesAgentTools`（多 Agent 模式建立的申請用 `AgentHandoff`）、`ShopAssistantView` 埋點（依模式填管道）
  - 錄製：`AgentRecording`（加 `Mode`）＋migration、`AgentRecordingService`、回放以錄製的模式重建
  - 劇本：`DemoScenarios`（加 `Mode` 欄；新增劇本 H「多 Agent：一句兩事」與劇本 E「長期記憶」）、`DemoConsoleView`（準備劇本時帶入模式；導向 `/shop/assistant?mode=handoff`）
- Test：`AgentReplayTests`、`AgentRecordingTests`、`DemoConsoleViewTests`、`AfterSalesChannelTests`

**Behaviour：**
- **管道**：多 Agent 建立的申請一樣會跑 M4 初審、顯示 AI 面板與助手；M6 可依管道分組對照。
- **回放**：回放多 Agent 錄製時，以 Handoff 結構重建；不呼叫模型，Token 用量不增加。
- **劇本 E（customer1）**：
  1. 第一句「我平常鞋子穿 41 碼，以後記得喔」→ 確認卡 → 記住。
  2. 開新對話：「想換一雙鞋的尺寸」→ Agent 帶出「記得您平常穿 41 碼，這次也是嗎？」。
  3. 到記憶頁刪除。
  4. 再開新對話，Agent 不再提起。

- [ ] **Step 1：** 寫失敗測試：
  - `IsAi_TrueForAgentAndHandoff`
  - `HandoffRequest_GetsPreReview`
  - `Replay_HandoffRecording_RebuildsTopologyWithoutModel`
  - `Console_PrepareScenarioH_StartsHandoffMode`
  - `ScenarioE_Defined`
- [ ] **Step 2–4：** 失敗 → 實作（含 migration）→ 通過
- [ ] **Step 5：** 檢查點

### Task 7: 顧客頁：模式切換、交接呈現、記憶卡

**Files：**
- Modify：`Components/Views/Shop/Assistant/ShopAssistantView.razor(.cs/.css)`、`AgentBackstagePanel`、`AgentStepTimeline`、`AgentConfirmCardView`
- Test：`Components/ShopAssistantViewTests.cs`

**Behaviour：**
- **模式切換**：頂端加「單一 Agent／多 Agent」切換。切換時先以 `ConfirmDialog` 提示「切換會開始新對話」，再呼叫 `StartNewAsync(mode)`。
  - 支援 `?mode=handoff`（展示控制台用）。
  - 回放時鎖定為錄製的模式。
- **交接呈現**：多 Agent 模式下，每則 Agent 回覆標示發言者，例如「品質專員」；交接時在對話中插入系統訊息「已轉給品質專員」。
  - 幕後面板的 Handoff 列以專屬樣式與 Agent 名稱徽章顯示。
- **記憶確認卡**：呈現「記住偏好」卡；記憶注入事件在面板以「記憶」來源色顯示。

- [ ] **Step 1：** 寫失敗測試：
  - `ModeSwitch_StartsNewConversation`
  - `QueryModeHandoff_StartsHandoff`
  - `Handoff_ShowsSpeakerAndSystemEntry`
  - `MemoryCard_RenderedAndConfirmable`
  - `Replay_LocksMode`
- [ ] **Step 2–4：** 失敗 → 實作 → 通過（含 `ThemeConventionTests`）
- [ ] **Step 5：** 檢查點

### Task 8: 文件、MAF 功能運用說明、品質關卡、實機驗證、開發歷程

**Files：**
- 版本：`appsettings.json`（`1.0.6 → 1.0.7`）
- **新文件** `docs/features/售後Agent示範系統-MAF功能運用說明.md`（含 BOM），列入 `docs/README.md`、`docs/features/README.md`。內容：
  1. 本系統用到的 MAF 功能，每項包含：
     - 白話用途
     - 技術名稱（類別、API）
     - 用在系統哪裡（頁面、程式位置）
     - 為什麼需要
  2. 涵蓋的功能：`ChatClientAgent`、工具（`AIFunctionFactory`）、核准（`ApprovalRequiredAIFunction`）、函式中介層、`UseFunctionInvocation` 迴圈上限、`AgentSession` 序列化、串流、結構化輸出 `RunAsync<T>`、多模態 `DataContent`、Workflows（Executor、fan-out／fan-in、`RequestPort`、checkpoint、自訂 `JsonCheckpointStore`）、Handoff 編排、`AsAIAgent`、`AIContextProvider`，以及 MEAI 的 `IChatClient` 管線（Auditing、Recording、Replay、Tracing）。
  3. 本系統沒有用到的 MAF 功能清單（依規劃時核對的 1.25 套件清單），各附一句說明與「為什麼這次不需要」。
  4. 總結：MAF 在本系統扮演的角色；沒有這個套件時，會面臨哪些問題（要自己寫工具迴圈、核准中斷與續跑、多 Agent 交接、checkpoint、記憶注入等）。
- **其他文件**：
  - 功能說明新增 M5 章節（§8）；設計文件加入 M5 差異註記。
  - 企劃規劃說明書：§5 加入多 Agent 與劇本 E 的展示段落、§5.2.1 驗證步驟；企劃書：多 Agent、長期記憶改為〔已提供〕。
  - 資料模型、畫面欄位、稽核 PRD、提示詞管理 PRD、速查表（M5 踩雷點）、索引。
- **開發歷程**（`docs/prd/售後Agent示範系統開發歷程紀錄.md`）：
  - §1 時間軸補上 M4 commit `ea599dc`，並新增 M5 一列。
  - 新增 M5 章節。
  - §8 加入預估與實際對照。
  - §9 加入 M5 審閱發現。
  - §10 回顧。
- **專案記憶**：controller 更新。

- [ ] **Step 1：** 更新文件（UTF-8 含 BOM）
- [ ] **Step 2：** `pwsh ./scripts/Invoke-QualityGate.ps1` → `QUALITY GATE PASSED`
- [ ] **Step 3：** 實機驗證（暫時的 Live 測試驅動真實服務，跑完刪除；金鑰不得寫出）：
  1. **多 Agent 劇本 H（一句兩事）**：分流 → 換退貨專員 → 交回分流 → 品質專員；面板出現交接事件；確認後建立 `AgentHandoff` 管道的申請，M4 初審自動執行。
  2. **劇本 E 記憶**：確認卡後記住 41 碼 → 開新對話時 Agent 帶出並確認 → 刪除 → 再開新對話時 Agent 不再提起。
  3. **記憶不當事實**：記憶是 41，顧客這次說要 42 → 申請規格為 42。
  4. **單一模式也會讀取同一份記憶。**
  5. **多 Agent 回放**：錄製劇本 H 的金劇本，重置後回放 → 交接事件重現，Token 用量不增加。
- [ ] **Step 4：** 檢查點：回報結果，附上建議 commit 訊息「M5：多 Agent 交接、長期記憶與我的 AI 記憶頁、MAF 功能運用說明；版本 1.0.6 → 1.0.7」

---

## Claude 代為決定的事項（請檢查）

1. **交接直接使用 MAF 內建的 Handoff 編排**，不自己寫路由；若試跑（Task 1）發現做不到，就改用「分流 Agent 呼叫路由工具、由程式換 Agent」的備案，並說明理由。
2. **多 Agent 建立的申請用「AI 多 Agent」管道**：沿用目前沒有被使用的 `AgentHandoff` 列舉值，改名。這樣 M6 能把單一與多 Agent 分開對照，而且一樣會跑 M4 初審。
3. **記憶一種偏好只存一筆**：鞋碼、衣服尺碼、顏色、溝通偏好各一筆，新值覆蓋舊值，避免記憶越積越多、互相矛盾。
4. **記憶以「系統指示」注入**，不當成顧客的話，所以不影響回放比對；注入文字固定、不能從後台改掉。
5. **示範資料重置時，會清掉示範顧客的記憶**；帳號被清除時，記憶一併刪除。
6. **多 Agent 模式每輪步數上限 14**（單一模式維持 8），因為交接本身也會用掉模型呼叫。
7. **新增劇本 H**（劇本 B 的多 Agent 版本）與**劇本 E**（記憶），兩者都可在展示控制台一鍵準備。

## 附錄：MAF 1.25.0 功能清單（規劃時核對 1.25.0 套件與 tag `dotnet-1.25.0`，供 Task 8 撰寫「MAF 功能運用說明」）

1.25.0 共有 34 個 .NET 套件。下面依「用到／沒用到」分組，每項附核對到的實際類型或方法名稱。

**本系統已用到或 M5 將用到的功能：**

| 套件 | 功能 |
|---|---|
| `Microsoft.Agents.AI(.Abstractions)` | `ChatClientAgent`、`AgentSession` 序列化、`RunStreamingAsync`、`RunAsync<T>`、函式中介層 `AsBuilder().Use`、`ApprovalRequiredAIFunction`、`AIContextProvider`（M5） |
| `Microsoft.Agents.AI.OpenAI` | Responses API |
| `Microsoft.Agents.AI.Workflows` | Executor、fan-out／fan-in、`RequestPort`、checkpoint、`JsonCheckpointStore`、Handoff 編排與 `AsAIAgent`（M5） |
| MEAI（Microsoft.Extensions.AI） | `IChatClient` 管線、`DataContent` |

**沒有用到的功能**（寫入文件時，每項附一句用途說明與「這次不需要的原因」）：

- **工作流程與編排**
  - 編排：Sequential、Concurrent、GroupChat、Magentic。
  - 宣告式：YAML 宣告式 Agent（`Declarative`，rc1）、宣告式 Workflow（`Workflows.Declarative`，含 PowerFx、MCP）。
  - Workflow 本身：Generators 來源產生器、子 Workflow `BindAsExecutor`、`WorkflowVisualizer`（`ToMermaidString`／`ToDotString`）、共享狀態、Workflow OpenTelemetry。
- **Agent 能力擴充**
  - Agent 互通：Agent 當工具 `AsAIFunction`、`AsIChatClient`、Agent 當 MCP 工具。
  - 狀態與記憶：內建記憶與檢索（`ChatHistoryMemoryProvider` 向量、`TextSearchProvider` RAG、`FileMemoryProvider`）、`ChatHistoryProvider` 外部儲存（Cosmos、Valkey、AzureBlob）。
  - 長對話與長任務：Compaction 壓縮（實驗）、背景回應與續傳 token、`LoopAgent`／`HarnessAgent`（實驗）。
  - 其他：多模型路由（實驗）、Agent Skills。
- **工具與執行環境**
  - 程式碼與指令執行：CodeAct（Hyperlight、LocalCodeAct）、Shell 工具。
  - 雲端託管工具：Code Interpreter、File Search、Web Search、Hosted MCP（類型在 MEAI，Foundry 另有工具）。
  - 本機 MCP 工具（ModelContextProtocol 套件）、MCP Tasks／Skills（`Agents.AI.Mcp`，alpha）。
- **模型與 Agent 服務整合**
  - 其他模型或 Agent 來源：Anthropic、GitHub Copilot、Microsoft Foundry Agents、Azure AI Persistent Agents、Copilot Studio、A2A 遠端 Agent。
  - 註：Ollama、Gemini 只有範例，沒有 AF 套件。
- **託管與對外介面**
  - 託管：`AddAIAgent`、Session store、Claims 隔離。
  - 對外協定端點：A2A 伺服器（`MapA2AJsonRpc`）、AG-UI 伺服器（`MapAGUIServer`）、OpenAI 相容端點（`MapOpenAIResponses`，alpha）。
  - 平台與工具：Foundry 託管、DevUI（含 Aspire）。
- **觀測與治理**：OpenTelemetry（`UseOpenTelemetry`）、`UseLogging`、評估（`EvaluateAsync`、`LocalEvaluator`）、Purview 資料治理（rc1）、Agent-Hooks（alpha）、`ToolApprovalAgent`／`UseToolApproval` 規則式自動核准。

**不在 .NET 1.25 的項目**（文件中不可寫成可用功能）：

| 項目 | 說明 |
|---|---|
| Durable Agents（Azure Functions／Durable Task） | 已移到另一個 repo，最新只到 1.16 preview |
| `Microsoft.Agents.AI.AzureAI` | 已由 `Foundry` 取代 |
| `Mem0` | 沒有 1.25 套件 |
| OpenAI Assistants 整合 | 1.25 沒有 |
| `AgentThread` | 已更名為 `AgentSession` |
| Python 專屬套件 | 不在 .NET |

## 執行方式

沿用 **Subagent-driven**：新分支 `feature/m5-handoff-memory`，以 git index 作檢查點，不自動 commit。Task 1 的結果若需調整後續任務，controller 先裁定再繼續。

## 驗證總結

- **自動**：
  - Task 1 的框架行為測試會長期保留。
  - 各任務的測試，加上品質關卡。
  - Review Focus 五項都已對應到測試。
- **實機**：Task 8 Step 3 全部項目。
- **計畫存檔**：計畫核准後，另存 `docs/prd/售後Agent展示系統-M5實作計畫.md`（含 BOM），並列入索引。
