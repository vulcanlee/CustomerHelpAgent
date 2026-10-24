# 售後 Agent 示範系統：MAF 功能運用說明

- 文件版本：1.1
- 文件狀態：已實作（涵蓋 M0–M6；M6 沒有新增 MAF 功能，見 §2.13）
- 現行系統版本：1.0.8
- 首次實作版本：1.0.7
- 最後核對日期：2026/10/24
- 讀者：對外（客戶、業務、主管）與內部（開發團隊）都適用。每一項先寫一句白話，可以直接拿去跟客戶說明；後面才是技術名稱與程式位置。

本文說明「售後 Agent 示範系統」用了 Microsoft Agent Framework（MAF）1.25.0 的哪些功能、各自解決什麼問題、用在系統哪裡；也列出**沒有用到**的功能與原因，最後總結 MAF 在本系統的角色。
機制細節見 [售後 Agent 示範系統](售後Agent示範系統.md)，設計與里程碑見 [設計文件](../prd/售後Agent展示系統-設計.md)，展示腳本見 [企劃規劃說明書](../prd/售後Agent示範系統企劃規劃說明書.md)。

**套件**（`Directory.Packages.props`，釘死版本）：`Microsoft.Agents.AI.OpenAI` 1.25.0、`Microsoft.Agents.AI.Workflows` 1.25.0；遞移帶入 `Microsoft.Agents.AI`（Agent 核心）、`Microsoft.Extensions.AI`（MEAI）10.x 與 OpenAI SDK。

> **MAF 與 MEAI 的分工**：MAF 建立在 Microsoft.Extensions.AI（MEAI）之上。`IChatClient`、函式工具 `AIFunction`、核准請求的內容型別、`DataContent`、函式呼叫迴圈（`FunctionInvokingChatClient`）**定義在 MEAI**；
> MAF 在上面加了 Agent（`ChatClientAgent`）、會話（`AgentSession`）、Agent 中介層、`AIContextProvider`、Workflows 與多 Agent 編排。本文每一項都標出「來源」：**MAF**、**MEAI**，或**本系統自訂**，避免把自己寫的東西說成框架功能。

---

## 1. 一頁看懂

**一句話**：MAF 是 AI Agent 的「作業系統」。模型負責「想下一步」，MAF 負責把這一步變成真的動作：呼叫工具、在該停的地方停住、等顧客按確認後再繼續、把對話存起來下次接著聊、讓好幾位 AI 專員分工交接，並把固定流程排成可以中斷後續跑的工作流程。本系統在 MAF 之上加了自己的安全規則（身分、權限、停止條件、個資遮蔽）與展示功能（幕後面板、錄製回放、計費）。

| 白話功能 | MAF 元件（來源） | 系統哪裡看得到 |
|---|---|---|
| AI 會「想一步、做一步」，直到把事情辦完 | `ChatClientAgent`（MAF） | 顧客「線上客服」`/shop/assistant`、客服 AI 助手、初審的 AI 節點 |
| 回覆邊產生邊顯示 | `RunStreamingAsync`（MAF） | 線上客服、客服 AI 助手 |
| 對話記得前面講過的話，重新整理、重啟都能接續 | `AgentSession` 序列化（MAF） | 線上客服重新整理後接續、確認卡在重啟後仍可按 |
| AI 只能用我們準備好的工具 | `AIFunctionFactory`（MEAI）＋`ToolOutcome`（本系統） | 7 個售後工具、6 個客服助手工具 |
| 會改資料的動作，一定先問顧客 | `ApprovalRequiredAIFunction`（MEAI）＋Agent／Workflow 的暫停與續跑（MAF） | 確認卡、記憶確認卡 |
| 每次 AI 要用工具前，程式先把關 | Agent 函式中介層 `AsBuilder().Use`（MAF） | 身分與權限核對、停止規則、幕後面板的每一步 |
| 不讓 AI 無限繞圈 | `UseFunctionInvocation` 上限（MEAI）＋每輪總量（本系統） | 停止原因「步數上限」 |
| AI 回答固定格式，程式可以直接讀 | `RunAsync<T>` 結構化輸出（MAF） | 初審的照片、歷史、摘要結論 |
| AI 看照片 | `DataContent`（MEAI） | 初審的「照片初審」 |
| 固定流程、三路並行、停下來等人決定、重啟後續跑 | Workflows：Executor、fan-out／fan-in、`RequestPort`、checkpoint（MAF） | 客服審核頁的 AI 初審流程圖 |
| 好幾位 AI 專員分工，一位接一位交接 | Handoff 編排＋`AsAIAgent`（MAF） | 線上客服的「多 Agent」模式 |
| 記住顧客的偏好，下次先拿來確認 | `AIContextProvider`（MAF） | 記憶注入、「我的 AI 記憶」`/shop/memory` |
| 每次呼叫模型都記帳、可錄製、可回放 | `IChatClient` 管線（MEAI 介面，類別本系統自訂） | Token 用量、AI 對話紀錄、展示控制台的回放；M6 效益對照頁的每案 AI 成本也從這些紀錄算出 |

---

## 2. 本系統用到的 MAF 功能

每一項的格式：**白話**（給客戶）→ **技術名稱**與**來源** → **用在哪裡** → **為什麼需要** → **若沒有會怎樣**。程式路徑相對於 `src/CustomerHelpAgent/`。

### 2.1 Agent 基礎

#### 2.1.1 `ChatClientAgent`：會自己跑「模型 → 工具 → 模型」迴圈的 Agent

- **白話**：把模型包成一位「會辦事的助理」：它決定下一步要查什麼，系統去查，把真實結果交回給它，它再決定下一步，直到能回答顧客為止。
- **技術名稱**：`Microsoft.Agents.AI.ChatClientAgent`、`ChatClientAgentOptions`（`Id`、`Name`、`Description`、`ChatOptions.Instructions／Tools`、`AIContextProviders`）。來源：**MAF**。
- **用在哪裡**：
  - 顧客「線上客服」單一模式：`CustomerHelpAgent.Web/Agents/AfterSales/AfterSalesAgentRunner.cs`（`BuildAgentAsync`）。
  - 多 Agent 模式的四位參與者：同檔 `BuildHandoffAgentAsync`。
  - 客服 AI 助手：`Agents/StaffAssistant/StaffAssistantRunner.cs`。
  - 初審的三個模型節點：`Agents/PreReview/PreReviewChatClientProvider.cs`。
- **為什麼需要**：售後案件常要查好幾次（訂單 → 規則 → 庫存）才能回答，Agent 要能自己決定順序，並在中途停下來問。
- **若沒有**：要自己寫「解析模型要呼叫哪個工具 → 執行 → 把結果組回對話 → 再送模型」的迴圈，還要處理多個工具呼叫、錯誤與歷史格式。

#### 2.1.2 `RunStreamingAsync`：邊產生邊顯示

- **白話**：Agent 一邊想一邊把字打出來，顧客不用乾等。
- **技術名稱**：`AIAgent.RunStreamingAsync(message, session)`，回傳 `AgentResponseUpdate` 串流。來源：**MAF**。
- **用在哪裡**：`AfterSalesAgentRunner.cs`（單一與多 Agent 模式都用它跑每一輪）、`StaffAssistantRunner.cs`。多 Agent 模式的交接事件、確認請求、錯誤，也都是從這個串流的 update 判斷出來的（見 §2.9）。
- **為什麼需要**：一輪可能 5～20 秒；串流讓畫面與幕後面板能即時更新。
- **若沒有**：要自己串接模型 API 的 SSE，並在工具呼叫之間維持串流狀態。

#### 2.1.3 `AgentSession` 序列化：對話可以存起來、下次接著聊

- **白話**：顧客重新整理頁面、甚至伺服器重啟，對話與還沒按的確認卡都還在。
- **技術名稱**：`AgentSession`、`agent.SerializeSessionAsync(session)`、`agent.DeserializeSessionAsync(json)`、`session.StateBag`。來源：**MAF**（`AgentThread` 在 1.25 已更名為 `AgentSession`）。
- **用在哪裡**：`AfterSalesAgentRunner.cs` 每輪開始還原、結束存回；資料存在 `AgentSessionRecord.StateJson`（`AccessDatas/Models/Agents/AgentSessionRecord.cs`，經 `Business/Services/DataAccess/AgentSessionStore.cs`，以 `StateVersion` 做樂觀版本）。多 Agent 模式存的是 Workflow 會話，另把「尚未回覆的核准請求」記在 `StateBag`（鍵 `AfterSalesOpenApprovals`）。
- **為什麼需要**：Responses API 一律 `store=false`（不在模型服務端留存對話），歷史只能由我們自己保存；確認卡等待中重啟也不能丟。
- **若沒有**：要自己定義歷史格式（文字、工具呼叫、工具結果、核准請求）並保證存回後框架讀得懂。

### 2.2 受控工具：`AIFunctionFactory`（MEAI）與 `ToolOutcome` 信封（本系統自訂）

- **白話**：AI 不能直接碰資料庫，只能用我們寫好的幾個「受控入口」，每個入口都定好輸入、輸出、失敗時怎麼說、誰能用、會不會改資料。
- **技術名稱**：`AIFunctionFactory.Create(method, AIFunctionFactoryOptions)` 產生 `AIFunction`，放進 `ChatOptions.Tools`。來源：**MEAI**。統一回傳信封 `ToolOutcome{status, data, message, retryable, withheld_fields}` 是**本系統自訂**（`Agents/AfterSales/ToolOutcome.cs`）。
- **用在哪裡**：
  - 顧客端 7 個工具：`Agents/AfterSales/AfterSalesAgentTools.cs`（`query_my_orders`、`get_effective_policy`、`check_variant_stock`、`track_shipment`、`request_photo_upload`、`create_after_sales_request`、`remember_preference`）。多 Agent 模式以 `CreateFunctions(params string[] names)` 給每位專員自己的子集。
  - 客服 AI 助手 6 個沒有參數的只讀工具：`Agents/StaffAssistant/StaffAssistantTools.cs`。
- **為什麼需要**：工具參數 schema 不含任何使用者識別欄位，身分一律由程式從登入狀態帶入；模型多傳的引數會被忽略。`withheld_fields` 讓幕後面板列出「刻意不送的欄位」。
- **若沒有**：要自己從 C# 方法產生 JSON schema、解析模型給的引數並轉型。

### 2.3 寫入前確認：`ApprovalRequiredAIFunction`／`ToolApprovalRequestContent`

- **白話**：會改資料的動作（建立售後申請、記住偏好），AI 只能「提議」；系統會停下來顯示確認卡，顧客按下確認才真的執行。
- **技術名稱**：寫入工具以 `ApprovalRequiredAIFunction` 包裝；模型提出呼叫時，框架**不執行工具**，改回傳 `ToolApprovalRequestContent`；顧客決定後以 `request.CreateResponse(approved, reason)` 產生 `ToolApprovalResponseContent` 送回，工具才以當初記下的參數執行。來源：型別在 **MEAI**；在 Agent 會話與 Handoff Workflow 中暫停、保存、續跑由 **MAF** 處理（多 Agent 模式的核准請求以 `RequestInfoEvent` 出現）。
- **用在哪裡**：`AfterSalesAgentTools.cs`（`create_after_sales_request`、`remember_preference`）；Runner 的 `PrepareConfirmationAsync`（程式重新查詢組成確認卡，不採模型文字）、`ConfirmAsync`；確認卡紀錄 `AgentConfirmation`（多 Agent 模式另存外部核准請求原文 `ApprovalRequestJson`）；畫面 `Components/Views/Shop/Assistant/AgentConfirmCardView.razor`。
- **為什麼需要**：這是「模型沒有寫入權」的關鍵；確認卡等待中，顧客關頁、重新整理或伺服器重啟，回來仍可按，而且只會建立一筆（`TryClaimConfirmationAsync` 條件式認領）。
- **若沒有**：要自己在迴圈中途攔下工具呼叫、把「待核准的呼叫」連同對話狀態存起來、重啟後再接回去續跑，這是整套 Agent 最容易出錯的部分。

### 2.4 函式中介層：`AfterSalesAgentGuard`（程式的把關點）

- **白話**：AI 每次要用工具前、用完之後，程式都先檢查一次：是不是本人、有沒有權限、該不該停下來，並把每一步寫進幕後面板。
- **技術名稱**：`agent.AsBuilder().Use(guard.InvokeAsync).Build()`（`AIAgentBuilder.Use` 的函式呼叫中介層，拿得到 `FunctionInvocationContext`，可設 `Terminate = true` 終止本輪）。來源：**MAF**。
- **用在哪裡**：`Agents/AfterSales/AfterSalesAgentGuard.cs`（單一模式掛在 Agent 上；多 Agent 模式同一個實例掛在每位參與者上，停止規則兩種模式完全相同）；客服 AI 助手用同一機制把工具結果轉成「查了：…」標籤（`StaffAssistantRunner.cs`）。
- **為什麼需要**：停止規則要由程式強制，不能只靠提示詞：`forbidden`／`timeout`／`unavailable` 立即終止；`not_found`／`no_policy`／`conflict` 之後封鎖建立申請（`blocked`）；工具連續失敗停止。
- **若沒有**：要把檢查寫進每個工具或包一層迴圈；中途終止整輪而不留下「沒有結果的工具呼叫」也要自己處理。

### 2.5 迴圈上限：`UseFunctionInvocation`（MEAI）＋每輪總量（本系統）

- **白話**：AI 一輪最多走幾步是固定的，走完就停並說明，不會無限繞圈、無限花錢。
- **技術名稱**：`chatClient.AsBuilder().UseFunctionInvocation(f => { f.MaximumIterationsPerRequest = MaxSteps; f.MaximumConsecutiveErrorsPerRequest = MaxConsecutiveToolFailures; })`。來源：**MEAI**（`FunctionInvokingChatClient`）。「每輪模型呼叫總數」（單一 `MaxSteps + 1`、多 Agent `HandoffMaxSteps + 1`，跨續跑累計）是**本系統自訂**，由 `TracingChatClient` 在送出前擋下（`AgentTurnBudgetExceededException`）。
- **用在哪裡**：`AfterSalesAgentRunner.CreateFunctionInvokingClient`、`StaffAssistantRunner.cs`；設定 `AfterSalesAgentSettings`（`MaxSteps` 預設 8、`HandoffMaxSteps` 預設 14）。
- **為什麼需要**：框架的上限只管「單次執行」；確認請求被程式拒絕後的續跑、多 Agent 的多位參與者都會再呼叫模型，所以要有跨執行的總量。
- **若沒有**：要自己計數並在上限時做「最後一次不帶工具、逼模型收尾」。

### 2.6 結構化輸出：`RunAsync<T>`

- **白話**：要 AI 照固定欄位回答（例如「建議核准／駁回、理由、檢查清單」），程式可以直接讀，不用從一段文字裡猜。
- **技術名稱**：`agent.RunAsync<T>(message, serializerOptions: ...)`（以 JSON schema 要求模型輸出）。來源：**MAF**。
- **用在哪裡**：`Agents/PreReview/PreReviewChatClientProvider.cs`，由 `ModelPhotoReviewer`（照片判斷、信心）、`ModelHistoryReviewer`（歷史紀錄）、`ModelPreReviewSummarizer`（建議、理由、檢查清單）使用。格式不合時改用程式摘要（建議改為「需人工判斷」）。
- **為什麼需要**：初審結論要畫在流程圖、要由程式檢查「規則核對不符資格卻建議核准」並降級。
- **若沒有**：要自己寫 JSON schema、解析與容錯。

### 2.7 多模態：`DataContent`（看照片）

- **白話**：顧客上傳的開膠照片可以直接交給 AI 看，判斷照片和描述是否一致。
- **技術名稱**：`new DataContent(bytes, contentType)` 放進 `ChatMessage.Contents`。來源：**MEAI**。
- **用在哪裡**：`Agents/PreReview/ModelPhotoReviewer.cs`（最多 3 張）；`Agents/AuditingChatClient.cs` 寫 AI 對話紀錄時把圖片換成佔位文字，不存 base64。
- **為什麼需要**：品質問題要看照片；實機驗證圖片＋JSON schema 在 Responses `store=false` 下可用。
- **若沒有**：要自己依各家 API 格式組圖片內容。

### 2.8 Workflows：固定流程、並行、人工關卡、重啟續跑（M4 初審）

- **白話**：客服後台的「AI 初審」是一張固定流程圖：照片、規則、歷史三件事同時跑，跑完整理摘要，然後**停下來等客服決定**；就算伺服器重啟，也從原來的進度接著跑，已完成的步驟不會再花一次錢。
- **技術名稱**（來源：**MAF**，`Microsoft.Agents.AI.Workflows`）：
  - 節點：繼承 `Executor<TInput>`，覆寫 `ConfigureProtocol` 以 `SendsMessageTypes(...)`／`YieldsOutputTypes(...)` 宣告送出型別；節點內 `context.SendMessageAsync`、`context.YieldOutputAsync`。
  - 圖：`new WorkflowBuilder(intake).AddFanOutEdge(intake, [photo, rule, history]).AddFanInBarrierEdge([photo, rule, history], summary)…`。
  - 共用狀態：`context.QueueStateUpdateAsync`／`ReadStateAsync`（進件把案件內容寫進 Workflow 狀態，摘要節點讀回）。
  - 人工節點：`RequestPort.Create<DecisionRequest, StaffDecision>`；等待時串流出現 `RequestInfoEvent`，客服決定後 `streaming.SendResponseAsync(request.CreateResponse(decision))`。
  - checkpoint：`InProcessExecution.RunStreamingAsync(..., CheckpointManager)`／`ResumeStreamingAsync(workflow, latest, manager)`；`CheckpointManager.CreateJson(store, options)`；store 是**本系統自訂**的 `SqliteWorkflowCheckpointStore : JsonCheckpointStore`（存資料表 `WorkflowCheckpoint`）。
- **用在哪裡**：`Agents/PreReview/PreReviewWorkflowFactory.cs`（圖）、`PreReviewExecutors.cs`（8 個節點）、`PreReviewWorkflowRunner.cs`（執行、續跑、送出決定）、`SqliteWorkflowCheckpointStore.cs`、`PreReviewWorker.cs`（背景服務、啟動救回）；畫面是審核頁的流程圖（`/after-sales-review`）。
- **為什麼需要**：初審步驟每次都一樣，用固定流程比讓 Agent 自己決定更穩、更好稽核；人工決策可能隔很久，期間重啟不能丟進度。
- **若沒有**：要自己寫並行排程、等三路到齊、暫停等外部輸入、狀態持久化與續跑。這是本系統若不用 MAF 時工作量最大的一塊。

### 2.9 Handoff 編排＋`AsAIAgent`：多位 AI 專員分工交接（M5）

- **白話**：「多 Agent」模式下，顧客先跟「分流 Agent」說話，它判斷需求後交給換退貨專員、品質專員或物流專員；專員辦完或遇到不屬於自己的事，再交回分流。每位專員只拿得到自己需要的工具，例如物流專員查不了庫存。
- **技術名稱**（來源：**MAF**，`Microsoft.Agents.AI.Workflows`）：
  - `AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)` 取得 `HandoffWorkflowBuilder`，`.WithHandoffs(from, targets)`、`.WithHandoffInstructions(中文)`、`.EnableReturnToPrevious()`、`.Build()`。
  - `workflow.AsAIAgent(id, name, executionEnvironment: InProcessExecution.OffThread.WithCheckpointing(CheckpointManager.CreateJson(new SqliteWorkflowCheckpointStore(...))))`：把整個交接 Workflow 包成一個 Agent，對外一樣用 `RunStreamingAsync`／`SerializeSessionAsync`。
  - 核准回覆前呼叫 `session.GetService<WorkflowSessionCheckpointRecovery>().TryPrepare()`：⚠️ **實驗性 API**（`[Experimental("MAAI001")]`），程式只在一處以 `#pragma warning disable MAAI001` 使用。
- **用在哪裡**：
  - 拓樸：`Agents/AfterSales/HandoffTopologyFactory.cs`（四位參與者，固定 Id＝Name：`triage`／`exchange`／`quality`／`logistics`；路線 分流 → [換退貨, 品質, 物流]，三位專員 → [分流]）。
  - 執行：`AfterSalesAgentRunner.cs`（`BuildHandoffAgentAsync`、`RunHandoffTurnAsync`），依會話的 `AgentSessionRecord.Mode` 選擇單一或多 Agent。
  - 中文交接說明：`AfterSalesHandoffPromptDefaults.cs`。
  - 畫面：線上客服的「單一 Agent ｜ 多 Agent」切換（`AgentModeSwitch.razor`）、發言者徽章、「已轉給品質專員」列；幕後面板的交接列（`AgentStepTimeline.razor`）。
- **為什麼需要**：設計要求「盡量發揮 MAF」，並要能把單一 Agent 與多 Agent 放在一起對照（M6）；用框架內建的交接，交接工具、對話歷史傳遞、「下一輪由上一位專員接手」都不用自己寫。
- **若沒有**：要自己寫路由工具、在 Agent 之間轉交對話歷史、記住目前是誰、跨 Agent 共用步數預算與確認卡（規劃時評估「由程式自己路由」的備案約 200–300 行，且交接事件、理由都要自己定義）。

### 2.10 `AIContextProvider`：長期記憶注入（M5）

- **白話**：顧客同意後，Agent 會記住「平常穿 41 碼」這類偏好；下次對話時先拿出來**跟顧客確認**，不會直接當成事實下單。顧客可以在「我的 AI 記憶」隨時刪除，刪除後下一句就不會再被提起。
- **技術名稱**：`CustomerMemoryContextProvider : AIContextProvider`，覆寫 `ProvideAIContextAsync(InvokingContext, ct)`，回傳 `AIContext { Instructions = ... }`；以實例掛在 `ChatClientAgentOptions.AIContextProviders`。來源：**MAF**。
- **用在哪裡**：`Agents/AfterSales/CustomerMemoryContextProvider.cs`；單一模式掛在 Agent 上，多 Agent 模式同一個實例掛在每位參與者上（每輪建立一次，建構時傳入顧客 Id）。寫入走 `remember_preference` 工具＋記憶確認卡；資料 `CustomerMemory`、服務 `Business/Services/DataAccess/CustomerMemoryService.cs`、內容政策 `Business/Helpers/MemoryContentPolicy.cs`；頁面 `/shop/memory`。
- **為什麼需要**：記憶要在**每一次**模型呼叫前、以**系統指示**（不是顧客訊息）注入：不影響回放的請求摘要、注入文字由程式固定產生（管理員改提示詞也拿不掉「需與顧客確認、不可當作業務事實」的規則）、每次都查資料庫（不快取），刪除立即生效。
- **若沒有**：要在每個 Agent、每位參與者的每次呼叫前自己組指示，Handoff 參與者內部建立的會話也接不到。

### 2.11 `IChatClient` 管線：記帳、錄製、回放、幕後面板（介面 MEAI，類別本系統自訂）

- **白話**：每一次呼叫模型都會查額度、記 Token 用量與費用、留下對話紀錄；展示時可以把真實模型的回應錄下來，之後不連模型照樣重播。
- **技術名稱**：MEAI 的 `IChatClient` 介面與 `DelegatingChatClient` 基底類別；模型連線用 `OpenAIClient.GetResponsesClient().AsIChatClient(model)`（`Microsoft.Extensions.AI.OpenAI`，隨 MAF OpenAI 套件帶入）。以下類別都是**本系統自訂**，不是 MAF 內建功能：

| 類別 | 做什麼 |
|---|---|
| `Agents/AuditingChatClient.cs`（`DelegatingChatClient`） | 額度檢查、Token 用量、AI 對話紀錄（M0） |
| `Agents/AfterSales/TracingChatClient.cs`（`DelegatingChatClient`） | 發「模型看到什麼」事件給幕後面板、每輪總量上限 |
| `Agents/AfterSales/RecordingChatClient.cs`（`DelegatingChatClient`） | 錄下請求摘要與回應（M3） |
| `Agents/AfterSales/ReplayChatClient.cs`（直接實作 `IChatClient`） | 回放：不連模型，送出錄製的回應，摘要不符就中止（M3） |

```text
即時：Agent → UseFunctionInvocation → TracingChatClient → RecordingChatClient → AuditingChatClient → OpenAI Responses（store=false）
回放：Agent → UseFunctionInvocation → TracingChatClient → ReplayChatClient（不連模型）
```

- **用在哪裡**：`Agents/AgentChatClientFactory.cs` 組鏈；多 Agent 模式四位參與者共用同一條鏈（錄製時依序記錄）。
- **為什麼需要**：MAF 的 Agent 只依賴 `IChatClient`，所以計費、錄製、回放都能「插」在模型前面，Agent 與工具完全不用改。
- **若沒有**（指 MEAI 的這層抽象）：記帳與回放要寫進每個呼叫模型的地方。

### 2.12 來源總表

| 功能 | MAF | MEAI | 本系統自訂 |
|---|---|---|---|
| `ChatClientAgent`、`RunStreamingAsync`、`AgentSession`、`RunAsync<T>`、Agent 中介層 `AsBuilder().Use`、`AIContextProvider` | ✔ | | |
| Workflows（Executor、fan-out／fan-in、共用狀態、`RequestPort`、checkpoint、`JsonCheckpointStore` 基底）、Handoff 編排、`AsAIAgent`、`WorkflowSessionCheckpointRecovery` | ✔ | | |
| `IChatClient`、`AIFunctionFactory`、`ApprovalRequiredAIFunction`／`ToolApprovalRequestContent`、`DataContent`、`UseFunctionInvocation` | | ✔ | |
| `ToolOutcome`、`AfterSalesAgentGuard` 的規則、`SqliteWorkflowCheckpointStore`、`CustomerMemoryContextProvider` 的內容、Auditing／Tracing／Recording／Replay 四個 client、每輪總量上限、確認卡組成與重核 | | | ✔ |
| 效益對照（M6）：`BenefitMetricsService`、`BenefitEstimator`、劇本執行標記 | | | ✔ |

### 2.13 M6 效益對照頁：沒有新增 MAF 功能（純資料彙總）

- **白話**：效益對照頁只是把系統已經留下的紀錄加總、平均、並排，再套上假設值算推估；它不呼叫模型，也不需要 Agent 能力，所以沒有用到任何新的 MAF 功能。
- **為什麼做得到**：M0 起每一次模型呼叫都經過 `IChatClient` 管線裡的 `AuditingChatClient`（§2.11），不論是單一 Agent、多 Agent 的四位參與者，還是 M4 初審 Workflow 的 AI 節點，都會留下：
  - 一筆 **Token 用量**（`TokenUsageLog`：作業名稱、Token 數、`CostTwd` 新台幣費用，以 `CallId` 識別）；
  - 一筆 **AI 對話紀錄**（`AiCallLog`：同一個 `CallId`，加上 `ConversationId`＝售後會話或申請的 CaseKey）。

  效益頁以 `CallId` 把兩者接起來，就知道每一筆費用屬於哪一個案件、哪一次劇本執行；再加上流程埋點（`ProcessMetric`）與 Agent 執行軌跡（`AgentRunTrace`：停止原因、是否回放），就能算出步數、時間、每案 AI 成本與「停下詢問」次數。
- **關鍵設計**：因為記帳點放在框架的函式呼叫迴圈**內側**（每一回模型呼叫各記一筆），多 Agent 交接、初審三路並行這些由 MAF 自動多出來的呼叫一筆都不會漏，效益頁的 AI 成本才能和「Token 用量」頁逐筆對得上。回放走 `ReplayChatClient`、不經 `AuditingChatClient`，不產生用量紀錄，所以效益頁另以執行軌跡的回放標記把回放執行整筆排除，避免 AI 看起來不花錢。
- **用在哪裡**：`Business/Services/DataAccess/BenefitMetricsService.cs`（彙總，唯讀）、`Business/Helpers/BenefitEstimator.cs`（推估，純函式）、`Web/Components/Views/AfterSales/BenefitComparisonView.razor`（頁面）。細節見 [售後 Agent 示範系統 §9](售後Agent示範系統.md)。
- **若沒有**這層一致的 `IChatClient` 管線：每種 Agent 結構的費用要各自記、各自對應案件，對照頁很難保證與用量頁一致。

---

## 3. 本系統沒有用到的 MAF 功能

依規劃時核對的 1.25.0 套件清單（34 個 .NET 套件與 tag `dotnet-1.25.0`）。□ 表示本系統未使用。

### 3.1 工作流程與編排

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | Sequential 編排 | 多個 Agent 依固定順序接力 | 顧客對話不是固定接力；初審的固定順序用自訂 Executor 圖較好控制（節點冪等、遮蔽、回放） | 文件逐段加工（擷取 → 翻譯 → 校稿） |
| □ | Concurrent 編排 | 同一件事同時交給多個 Agent 再合併 | 初審三路性質不同（規則核對是純程式、不是 Agent），用自訂 fan-out／fan-in 較合適 | 多模型交叉比對答案 |
| □ | GroupChat | 多個 Agent 輪流發言，由管理者決定下一位 | 售後要可預期、可稽核的路線，不適合讓 Agent 互相討論 | 內部腦力激盪、多角色評審 |
| □ | Magentic | 主控 Agent 動態規劃並指派工作 | 任務範圍固定；開放式規劃難保證安全與成本 | 研究型、開放問題 |
| □ | 宣告式 Agent（YAML，`Declarative`，rc1） | 用設定檔定義 Agent | Agent 要掛 Guard、工具子集、記憶 provider，程式碼定義才測得到；且仍是 rc 版 | 讓非開發人員調整 Agent |
| □ | 宣告式 Workflow（`Workflows.Declarative`，含 PowerFx、MCP） | 用 YAML 定義流程 | 初審需要節點冪等、個資遮蔽、回放比對等程式邏輯 | 低程式碼流程、與 Copilot Studio 類工具互通 |
| □ | Generators 來源產生器 | 自動產生 Executor 的路由樣板 | 只有 8 個節點，手寫 `ConfigureProtocol` 已夠清楚 | 節點很多時減少樣板 |
| □ | 子 Workflow `BindAsExecutor` | 把一個 Workflow 當成另一個 Workflow 的節點 | 只有一層流程 | 初審再拆子流程（例如照片多階段審查） |
| □ | `WorkflowVisualizer`（`ToMermaidString`／`ToDotString`） | 把流程圖輸出成 Mermaid／DOT | 審核頁的流程圖要顯示即時狀態，自行繪製 | 文件自動產生流程圖 |
| □ | Workflow OpenTelemetry | Workflow 執行的標準追蹤 | 節點狀態已存 `PreReviewNodeState` 並即時推播 | 正式上線接 APM |

> 註：規劃清單把「共享狀態」列在未使用，實際上 M4 初審**有用** Workflow 共用狀態（`QueueStateUpdateAsync`／`ReadStateAsync`，見 §2.8），本文以程式為準。

### 3.2 Agent 能力擴充

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | Agent 當工具 `AsAIFunction` | 把一個 Agent 包成另一個 Agent 的工具 | 多 Agent 選用 Handoff（顧客直接和專員對話），不是主管 Agent 在背後呼叫子 Agent | 主管 Agent 彙整多個子 Agent 的查詢結果 |
| □ | `AsIChatClient`（Agent 當聊天介面） | 把 Agent 當成一般 `IChatClient` 接進既有程式 | UI 直接呼叫 Runner | 接進既有的聊天框架或管線 |
| □ | Agent 當 MCP 工具 | 把 Agent 公開給其他 MCP 用戶端 | 示範不對外提供 Agent 服務 | 讓企業內其他 AI 工具呼叫售後 Agent |
| □ | 內建記憶與檢索（`ChatHistoryMemoryProvider` 向量、`TextSearchProvider` RAG、`FileMemoryProvider`） | 框架提供的記憶與文件檢索 | 記憶只有 4 種結構化偏好，必須經確認卡與內容政策；規則一律由工具讀系統當下版本，系統刻意不做 RAG | FAQ、產品手冊問答 |
| □ | `ChatHistoryProvider` 外部儲存（Cosmos、Valkey、AzureBlob） | 把對話歷史存到外部服務 | 會話序列化後存 SQLite（本系統只支援 SQLite） | 多台伺服器、雲端部署 |
| □ | Compaction 壓縮（實驗） | 長對話自動壓縮歷史 | 示範對話短；多 Agent 會話 JSON 雖會成長但在可接受範圍 | 長時間客服對話 |
| □ | 背景回應與續傳 token | 長任務在背景跑，斷線後續接 | 每輪約數秒到十幾秒；Responses 一律 `store=false` | 長時間研究或報表任務 |
| □ | `LoopAgent`／`HarnessAgent`（實驗） | 反覆執行直到條件達成的 Agent | 迴圈出口由本系統的停止規則掌控 | 自動重試型任務 |
| □ | 多模型路由（實驗） | 依請求自動選不同模型 | 只用一個部署（照片可另設 `VisionModel`） | 成本最佳化（簡單問題用小模型） |
| □ | Agent Skills | 可插拔的技能包 | 工具集固定且受控 | 技能多、要依情境載入時 |

### 3.3 工具與執行環境

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | CodeAct（Hyperlight、LocalCodeAct） | 讓模型寫程式並在沙箱執行 | 售後只允許受控工具，刻意不給模型執行程式的能力 | 資料分析型助手（需另做安全評估） |
| □ | Shell 工具 | 讓 Agent 執行命令列 | 同上，不應開放 | 開發維運工具 |
| □ | 雲端託管工具（Code Interpreter、File Search、Web Search、Hosted MCP；類型在 MEAI，Foundry 另有工具） | 由模型服務端執行的工具 | 資料都在自家系統；不讓模型上網找規則；`store=false` | 需要網路搜尋或服務端檔案檢索的情境 |
| □ | 本機 MCP 工具（ModelContextProtocol 套件） | 連接外部 MCP 伺服器提供的工具 | 工具都是自家服務，直接寫成 `AIFunction` | 接企業既有的 MCP 服務（ERP、CRM） |
| □ | MCP Tasks／Skills（`Agents.AI.Mcp`，alpha） | MCP 的長任務與技能 | alpha 版，且不需要 MCP | MCP 生態成熟後 |

### 3.4 模型與 Agent 服務整合

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | Anthropic、GitHub Copilot 連接器 | 其他模型供應商 | 只支援 Azure OpenAI 與 OpenAI（`AiSettings` 切換） | 客戶指定其他模型 |
| □ | Microsoft Foundry Agents、Azure AI Persistent Agents | 由雲端服務代管 Agent 與對話 | 會把對話存在服務端，與本系統 `store=false`、自己保存歷史的隱私取向相反 | 全面上 Azure Foundry 的客戶 |
| □ | Copilot Studio | 接 Copilot Studio 建的 Agent | 不需要 | 與 Power Platform 整合 |
| □ | A2A 遠端 Agent | 呼叫別的系統上的 Agent | 所有 Agent 都在本系統內 | 跨公司或跨系統的 Agent 協作 |

> 註：Ollama、Gemini 只有範例，沒有 Agent Framework 套件。

### 3.5 託管與對外介面

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | 託管 `AddAIAgent`、Session store、Claims 隔離 | 以 DI 註冊 Agent，由框架管理會話與身分隔離 | Runner 自己管理會話（SQLite、樂觀版本、軌跡、回放） | 把多個 Agent 做成服務 |
| □ | A2A 伺服器（`MapA2AJsonRpc`） | 把 Agent 公開成 A2A 端點 | 不對外提供 Agent | 讓合作夥伴的 Agent 呼叫 |
| □ | AG-UI 伺服器（`MapAGUIServer`） | 標準化的 Agent 前端協定 | UI 是 Blazor Server，直接呼叫 Runner | 換成 Web 前端框架時 |
| □ | OpenAI 相容端點（`MapOpenAIResponses`，alpha） | 把 Agent 包成 OpenAI 相容 API | 不需要 | 讓既有 OpenAI 用戶端直接使用 Agent |
| □ | Foundry 託管、DevUI（含 Aspire） | 雲端託管與開發除錯介面 | 已有幕後面板與 `/agent-runs` 執行紀錄 | 開發階段快速除錯 |

### 3.6 觀測與治理

| | 功能 | 一句說明 | 這次為什麼不需要 | 未來何時可能用到 |
|---|---|---|---|---|
| □ | OpenTelemetry（`UseOpenTelemetry`）、`UseLogging` | 標準追蹤與記錄 | 已有 `AuditingChatClient`（Token 用量、AI 對話紀錄）、`TracingChatClient`（幕後面板）與 NLog | 正式上線接 APM、分散式追蹤 |
| □ | 評估（`EvaluateAsync`、`LocalEvaluator`） | 自動評分 Agent 回答品質 | 以腳本化模型的測試與金劇本回放守住行為；M6 效益頁是流程指標 | 換模型或改提示詞前的品質回歸 |
| □ | Purview 資料治理（rc1） | 接 Microsoft Purview 的資料保護政策 | 個資以自家遮蔽與投影處理 | 企業已導入 Purview |
| □ | Agent-Hooks（alpha） | Agent 生命週期掛勾 | 函式中介層與 `IChatClient` 管線已足夠 | — |
| □ | `ToolApprovalAgent`／`UseToolApproval` 規則式自動核准 | 依規則自動核准部分工具呼叫 | 寫入一律要顧客親自確認，**刻意不允許自動核准** | 內部低風險工具（例如只讀但耗費高的查詢） |

### 3.7 不在 .NET 1.25 的項目（不可寫成可用功能）

| 項目 | 說明 |
|---|---|
| Durable Agents（Azure Functions／Durable Task） | 已移到另一個 repo，最新只到 1.16 preview |
| `Microsoft.Agents.AI.AzureAI` | 已由 `Foundry` 套件取代 |
| `Mem0` | 沒有 1.25 套件 |
| OpenAI Assistants 整合 | 1.25 沒有 |
| `AgentThread` | 已更名為 `AgentSession` |
| Python 專屬套件 | 不在 .NET |

---

## 4. 使用 MAF 時踩到的框架行為

以下都已有測試「釘住」：升級 MAF 後若行為改變，測試會先失敗。升級套件時務必重跑 `MafHandoffBehaviorTests`、`AfterSalesHandoffRunnerTests` 與 `PreReviewWorkflowTests` 的重啟測試。

| 框架行為（1.25.0） | 影響 | 本系統的處理 | 守門測試 |
|---|---|---|---|
| Workflows 的 fan-in 訊息從 checkpoint 還原時內容為空（框架缺陷，不拋例外） | 重啟後摘要節點拿到空結論 | `PreReviewFindingJsonConverter` 剝掉多包的外殼；摘要另把結論存進 Workflow 共用狀態 | `PreReviewWorkflowTests.Restart_*` |
| checkpoint 粒度是 superstep，不是節點 | 同一 superstep 先完成的節點，重啟後會再執行一次（再花一次 Token） | 每個 AI 節點先把結論存進 `PreReviewRun`，進入時已有就直接重送 | `Restart_DuringReviewers_FinishedReviewerNotRerun`、`Restart_ResumesFromCheckpoint_CompletedNodesNotRerun` |
| 交接工具名稱是 `handoff_to_N`（N 依來源 Agent 的目標註冊順序），**沒有交接事件型別** | 不能從工具名稱直接知道交給誰 | `HandoffTopologyFactory.TargetOf` 依路線表對應；Runner 從 update 的作者改變與交接呼叫發出自己的 `Handoff` 事件 | `MafHandoffBehaviorTests.Handoff_Wrapped_StreamsAuthorNamePerAgent`、`AfterSalesHandoffRunnerTests.Handoff_TriageToQuality_EmitsHandoffEvent` |
| 核准請求在 update 裡出現兩份；回覆前必須呼叫實驗性的 `TryPrepare()` | 用錯那一份或不呼叫 `TryPrepare`，工具其實已執行但 checkpoint 沒前進，之後會話壞掉 | 只取 `RawRepresentation is RequestInfoEvent` 的外部那份；回覆前一律 `TryPrepare`；只建立一筆另由資料庫認領保證 | `Handoff_ApprovalSurfacesAndResumesAfterSessionRoundTrip`、`Handoff_ApprovalResponse_WithoutRecoveryPrepare_FailsWithSharedStateConflict`、`Handoff_ConfirmAfterRestart_CreatesOnce` |
| 確認卡等待中直接送一般訊息，Handoff 會話從此每輪失敗（單一模式「下一輪自動拒絕」在這裡不存在） | 顧客改打字就把對話弄壞 | 先替留下的請求明確送出拒絕（多一次模型呼叫），再送新訊息 | `Handoff_NewMessageWhileApprovalPending_BreaksSession`、`Handoff_RejectPendingApprovalThenNewMessage_Recovers`、`Handoff_NewMessageWhileCardPending_RejectsThenSends` |
| 模型鏈的例外不會從 `RunStreamingAsync` 拋出，而是變成 `ErrorContent` update；出錯那一輪的會話存回後，下一輪必定失敗 | 額度、回放分歧、步數上限都抓不到例外 | 掃描 `ErrorContent`，從 `ExecutorFailedEvent.Data` 取原始例外對應停止原因；出錯不存回（回滾到上一輪的會話） | `Handoff_ModelChainException_SurfacesAsErrorUpdates`、`Handoff_ModelError_DoesNotCorruptNextTurn`、`Handoff_StepBudget_Stops` |
| `AsAIAgent` 沒給外部 checkpoint 庫時，會話 JSON 會塞進整個 checkpoint 庫（2 輪約 56 KB，持續成長） | 會話越來越大 | 一律 `WithCheckpointing(SQLite)`；每輪成功後只留最後一筆 checkpoint | `Handoff_SessionJson_StaysSmallWithCheckpointManager`、`StartNew_Handoff_ClosesPreviousAndDeletesItsCheckpoints` |
| executor id、核准 port id、外部 RequestId 都由參與者的 `Name_Id` 組成 | 改名會讓進行中的會話與待決確認卡失效 | 參與者 Id／Name 固定為常數 | `Handoff_ConfirmAfterRestart_CreatesOnce` |
| 框架預設的交接指示是英文（而且寫 `handoff_to_<agent_id>`，與實際名稱不符） | 中文提示詞後面接一段英文 | `WithHandoffInstructions` 改用固定中文版 | `Handoff_TriageToQuality_EmitsHandoffEvent` |
| 單一模式的核准 `RequestId` 帶 `ficc_` 前綴，不能當 CallId | 用它自組回應會讓下一輪對不上 | 一律以還原會話裡框架記錄的原始請求 `CreateResponse` | `Confirm_WhenApprovalRequestMissingFromSession_FailsClosed` |
| 核准（或拒絕）後，`FunctionInvokingChatClient`（MEAI 10.10）從歷史複製出呼叫訊息再送出，連同「來自歷史」的 `_attribution` 標記；Handoff 把它原封不動轉給其他參與者，對方的 `InMemoryChatHistoryProvider` 依預設過濾器不存「來自歷史」的請求訊息，只存下沒有標記的工具結果（框架缺陷） | 該參與者下次再被呼叫時，送出沒有對應呼叫的工具結果（孤兒），Responses API 回 HTTP 400（實機劇本 H：分流第二次被呼叫就失敗） | `HandoffTopologyFactory.ScrubHistoryAttribution` 包在每位參與者外面，拿掉送出 update 的標記（複製後再改，不動共用字典）；不改成 `HandoffToolCallFilteringBehavior.All`（其他參與者會看不到申請編號等工具結果）。假模型以 `ResponsesApiPairing` 比照 Responses API 嚴格檢查成對 | `MafHandoffBehaviorTests.Handoff_AfterDecision_ForwardedCallTaggedAsHistory_SecondVisitSeesOrphanResult`（釘住框架行為：**升級後若它失敗，代表框架已修正，可評估移除 scrubber**）、`Handoff_AfterDecision_ScrubbedParticipants_KeepToolCallsPaired`、`AfterSalesHandoffRunnerTests.Handoff_DecisionThenSameTurnHandoffs_ToolCallsStayPaired`、`Handoff_QualitySpecialistSecondVisitAfterDecision_ToolCallsStayPaired` |

---

## 5. 總結：MAF 在本系統扮演的角色

### 5.1 白話版

MAF 在本系統裡扮演 **AI Agent 的作業系統**：

1. **執行迴圈**：讓 AI 想一步、系統做一步、把真實結果交回，直到事情辦完。
2. **受控工具**：AI 只能透過我們準備的入口做事。
3. **暫停與續跑**：會改資料的動作先停下來等顧客確認；等多久、中間重啟都沒關係。
4. **多 Agent 分工**：分流 Agent 與三位專員之間的交接、對話歷史傳遞、「下一句由上一位專員接手」。
5. **固定流程**：客服初審的三路並行、等客服決定的人工關卡、重啟後從原進度續跑。
6. **記憶注入**：每次呼叫前把顧客同意保存的偏好，以「需要確認」的方式交給 AI。

本系統自己負責的是**規則與把關**：身分與權限、停止條件、確認卡內容、個資遮蔽、計費、錄製回放、幕後面板。兩者分工清楚：框架管「怎麼跑」，我們管「准不准、對不對」。

### 5.2 如果沒有 MAF，要自己做哪些事

| 要自己寫的部分 | 難在哪裡 | 粗估 |
|---|---|---|
| 工具呼叫迴圈與上限 | 解析工具呼叫、執行、組回歷史、錯誤處理、步數上限與最後收尾 | 數百行，加上各家 API 差異 |
| 核准中斷與續跑 | 在迴圈中途攔下寫入、保存「待核准的呼叫」、重啟後接回原呼叫續跑、只執行一次 | 本系統最容易出錯的部分之一 |
| 會話保存 | 定義含工具呼叫與核准請求的歷史格式、序列化與相容 | 中等 |
| 並行工作流程引擎＋checkpoint | 並行排程、等三路到齊、暫停等外部輸入、狀態持久化、從 checkpoint 續跑 | 工作量最大，約一個小型框架 |
| 多 Agent 交接 | 路由、歷史傳遞、目前發言者、回到上一位、跨 Agent 的確認卡與步數 | 規劃時評估約 200–300 行（不含事件與測試） |
| 記憶注入 | 在每個 Agent、每次呼叫前組指示 | 小 |

以本專案經驗粗估，自己寫等於另做一個小型 Agent 框架：程式量以千行計，並需要相當於 M2–M5 各里程碑同等規模的測試才能放心；而且展示時「這是業界標準框架的做法」的說服力也會消失。
另一個常被低估的損失是**一致性與可觀察性**：MAF 讓單一 Agent、多 Agent、Workflow 都用同一套 `IChatClient`、工具、核准與會話模型，所以計費、錄製回放、幕後面板只要做一次就全部適用。

### 5.3 誠實的取捨

- **實驗性 API**：多 Agent 確認卡依賴 `WorkflowSessionCheckpointRecovery.TryPrepare()`（`MAAI001`），升級時可能改名或改行為。
- **框架缺陷要繞道**：Workflows 1.25.0 的 fan-in checkpoint 還原缺陷、Handoff 的核准與錯誤行為（§4），都靠自己的程式繞過，並以測試釘住。
  其中「核准後重新送出的呼叫訊息帶著『來自歷史』標記，Handoff 轉給其他參與者後被丟掉、只留下工具結果」這一項只在實機（Responses API 回 HTTP 400）才會現形，自動測試原本抓不到；現在靠 `ScrubHistoryAttribution` 繞過，並讓假模型比照 Responses API 嚴格檢查工具呼叫與結果成對（含重複的 CallId）。升級 Microsoft.Agents.AI 或 Microsoft.Extensions.AI 時，若 `MafHandoffBehaviorTests` 的框架行為測試失敗，代表框架已修正，可評估移除這層繞道。
- **版本必須釘死**：MAF 的 minor 版也可能 breaking，升級要重跑框架行為測試。
- **背景執行緒**：多 Agent 模式的 Workflow 在背景執行緒跑（`InProcessExecution.OffThread`），事件寫入要排隊（Runner 的事件閘門），測試也要用獨立連線的資料庫。
- **框架內部狀態**：Handoff 會話保存整段可見對話與框架內部格式（含交接呼叫、"Transferred."），會話 JSON 會隨輪數成長，也不能用單一模式的方式直接讀歷史（還原一律以執行軌跡為主）。

整體來說，MAF 讓本系統能把力氣花在「業務規則與安全把關」與「讓客戶看得懂」上，而不是重寫 Agent 的基礎設施；代價是要接受預覽／實驗性 API 的風險，並用測試把框架行為釘住。

> 返回 [features 索引](README.md)
