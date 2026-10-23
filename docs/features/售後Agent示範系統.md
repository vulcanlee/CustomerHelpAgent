# 售後 Agent 示範系統

- 文件版本：0.3
- 文件狀態：部分實作（M0 計費接軌、M1 傳統流程、M2 單一售後 Agent 已實作；M3–M6 規劃中）
- 現行系統版本：1.0.4
- 首次實作版本：1.0.2
- 最後核對日期：2026/10/23

以 Microsoft Agent Framework（MAF，`Microsoft.Agents.AI.OpenAI` 1.25.0）示範「傳統售後流程 vs AI Agent」的鞋服電商對照系統。
需求、設計與里程碑見 [售後 Agent 展示系統設計](../prd/售後Agent展示系統-設計.md)；本文只記錄**已實作**的機制。

## 1. 已實作：M0 計費接軌（1.0.2）

MAF 的 Agent 會自己跑「模型 → 工具 → 模型」的迴圈，一次執行可能呼叫模型好幾次。
腳手架既有的 AI 功能都經由 `AiChatCompletionClient` 記帳；M0 讓 Agent 的**每一次**模型呼叫也走同一套規則：

| 項目 | 行為（與 `AiChatCompletionClient.CompleteAsync` 相同） |
|---|---|
| AI 用量上限 | 送出前 `IAiQuotaService.CheckAsync`；擋下時寫稽核 `Ai.QuotaBlocked`，拋 `AiQuotaExceededException`（訊息可直接給使用者看），不送出、不記用量 |
| Token 用量 | 成功、上游失敗（401／403→`Unauthorized`、429→`RateLimited`、其他→`UpstreamError`）、逾時（`Timeout`）都記一筆；使用者取消與非預期錯誤不記 |
| AI 對話紀錄 | 只要送出就記一筆（含取消與非預期錯誤），帶 `ConversationId`；與 Token 用量以同一個 `CallId` 關聯 |
| 慢呼叫 | 沿用 `SlowOperationSettings.AiCallMs` 與既有的 `Slow AI chat completion` 警告 |
| 串流 | 收齊所有片段後才記一筆；中途拋例外依上列失敗規則記一筆 |
| 串流被放棄（1.0.4 起） | 呼叫端沒取消、直接停止列舉（`DisposeAsync`）→ 以已收到的片段記一筆用量與對話紀錄（`Success=false`、`FailureReason=Abandoned`）；有取消則照取消規則（`Canceled`、不記用量） |
| 記錄防護（1.0.4 起） | 請求快照、`ResponseBody`、`RawUsageJson` 序列化失敗時改存 `{"snapshotError":"<例外型別>"}` 並記警告；成功那一支的記錄步驟自身出錯只記警告，不讓成功的模型呼叫拋出 |

與 `AiChatCompletionClient` 不同的地方：失敗一律**重拋**（Agent 框架要看到例外）；回應文字為空不算失敗（呼叫工具的那一回本來就沒有文字）。

## 2. 用法

```csharp
// 注入 AgentChatClientFactory（Scoped）
if (factory.Validate() is { } error)
{
    // AiSettings 不完整，error 是給使用者看的中文說明
}

var conversationId = Guid.NewGuid();   // 同一次 Agent 執行（或同一段對話）共用
var agent = new ChatClientAgent(
    factory.Create("售後 Agent", conversationId),
    instructions: "…",
    tools: [AIFunctionFactory.Create(…)]);
var response = await agent.RunAsync("…");
```

- ⚠️ **Agent 的模型呼叫一律經 `AgentChatClientFactory.Create`**，不可自行 `new OpenAIClient(...)`：
  自己建的 client 不會查額度，也不會出現在「Token 用量」與「AI 對話紀錄」頁。
- `Create` 回傳的是 `AuditingChatClient`（`DelegatingChatClient`）包住 OpenAI SDK 的 `IChatClient`。
  `ChatClientAgent` 會在外面再包一層函式呼叫迴圈，所以記帳點在迴圈**內側**，每一回模型呼叫各記一筆。
- 端點規則沿用 `AiChatEndpoint.CreateBaseUri`（Azure OpenAI v1 與 OpenAI 共用 `/v1/` 底下的路徑）。
  `Create(operation, conversationId, apiStyle, reasoningEffort)`（1.0.4 起）可選 API 形式：
  `AgentApiStyle.ChatCompletions`（`/v1/chat/completions`，未指定時的預設）或 `AgentApiStyle.Responses`（`/v1/responses`，
  一律送 `store=false`，不用服務端儲存的對話，會話歷史仍由 MAF `AgentSession` 保存）；推理強度空字串＝不送。
  售後 Agent 由 `AfterSalesAgentSettings` 的 `ApiStyle`／`ReasoningEffort`／`Temperature` 決定（預設 Responses、不送推理強度、不送溫度）。
  ⚠️ 實測：推論模型（例如 `gpt-6.1-sol`）在 Chat Completions「帶函式工具」時只接受 `reasoning_effort=none`，
  而該模型又不支援 `none`、也不接受 `temperature` —— 工具呼叫只能走 Responses。
- `operation` 會成為 Token 用量的「作業」欄位；新增正式作業名稱時依速查表 §6.7 登記 `TokenUsageOperations` 常數。

## 3. 測試

| 測試 | 守住什麼 |
|---|---|
| `AgentChatClientFactoryTests` | 端點正規化、實際 HTTP 路徑、設定不完整時拒絕建立；1.0.4 起另含 Responses 走 `/v1/responses` 且 `store=false`、推理強度分別送 `reasoning.effort`／`reasoning_effort`、空字串不送 |
| `AuditingChatClientTests` | 成功記帳、額度擋下（稽核 detail 格式）、429 記 `RateLimited`、401 記 `Unauthorized`、逾時記 `Timeout`、非預期例外只記對話紀錄、取消不記用量、串流只記一筆、`ChatClientAgent` 帶工具時兩回模型呼叫各記一筆且共用對話 Id；1.0.4 起另含串流放棄記 `Abandoned`、串流中途取消、串流工具呼叫寫進 `ResponseBody`、串流中途失敗、無法序列化的 `AIContent` 仍成功（`snapshotError`）、用量記錄器丟例外仍回傳 |
| `AuditingChatClientTests.LiveSmoke_WhenEnvConfigured_ShouldGetReply` | 實機冒煙：設定環境變數 `CHA_LIVE_AI_PROVIDER`／`CHA_LIVE_AI_ENDPOINT`／`CHA_LIVE_AI_KEY`／`CHA_LIVE_AI_MODEL` 才真的連線，否則直接通過 |

## 4. 已實作：M1 模擬業務系統＋傳統售後流程（1.0.3）

M1 做出「鞋服電商」的模擬業務資料與**傳統流程**（顧客填表單、客服人工審核），作為 M2 起 Agent 做法的對照基準。程式只服務展示，**不是**正式電商。

**資料（`AccessDatas/Models/Shop`，migration `AddShopDomain`）**

| 實體 | 說明 |
|---|---|
| `Product`／`ProductVariant` | 商品（`Code` 唯一）與顏色×尺寸規格（含庫存；`ProductId+Color+Size` 唯一） |
| `SalesOrder`／`SalesOrderItem` | 訂單（`OrderNo` 唯一、`CustomerUserId` 索引、`DeliveredAt` 簽收時間）與明細；收件人姓名、地址、手機屬個資，不進日誌 |
| `Shipment`／`ShipmentEvent` | 物流單與歷程；已出貨未簽收的訂單可「申請催件」 |
| `AfterSalesPolicy` | 售後規則，**版本化**：`Code+Version` 唯一，改規則＝新增版本（舊版保留），欄位含 `WindowDays`、`RequiresUnworn`、`RequiresPhoto`、`EffectiveFrom/To` |
| `AfterSalesRequest` | 售後申請：`RequestNo`（`AS{yyyyMMdd}{Id:D4}`）、`CaseKey`（補件往返共用）、`Status`（`Submitted`／`NeedMoreInfo`／`Approved`／`Rejected`）、`Channel`（`Traditional`／`Agent`／`AgentHandoff`）、`AppliedPolicyId`（送件當下套用的規則版本） |
| `AfterSalesAttachment` | 申請照片（每筆申請最多 3 張、每張 10 MB、只收 jpg／png／webp） |
| `ProcessMetric` | 流程埋點（`CaseKey`、`Channel`、`Actor`、`Action`），供 M6 效益對照 |

⚠️ 這十個實體**不做軟刪除、也沒有 `ConcurrencyStamp`**（刻意）：規則改版用新增版本取代修改，申請以「狀態守門的條件式更新」處理併發。
顧客（`MyUser`）被軟刪除後，他的申請仍會出現在審核佇列（顧客名稱以關掉過濾器的明確查詢取得，不走導覽屬性）；仍有訂單或售後申請的顧客不會被「已刪除資料清理」永久刪除（算「使用中」留到下次）。

**服務（`Business/Services/DataAccess`）**：`AfterSalesPolicyService`、`ShopOrderService`、`AfterSalesRequestService`、`ProcessMetricService`、`AfterSalesFileStore`（照片落地至 `ExternalFileSystem:AfterSalesFilePath`，已納入系統備份與 `scripts/Restore-Backup.ps1`），以及純函式 `AfterSalesEligibility`（簽收天數、是否在期限內等）。所有查詢以顧客 Id 限定擁有權。

**示範資料**：`DemoAccountSeeder`（Order 50）與 `DemoCatalogSeeder`（Order 60），由 `DemoSettings`（`Enabled`、`DemoUserPassword`）控制；`appsettings.Production.json` 關閉，正式環境不會建立。

| 帳號 | 角色 | 內容（密碼 `demo1234`） |
|---|---|---|
| `customer1` 王大同 | 顧客 | 黑鞋 40 碼（簽收 3 天前）、白鞋 41 碼（簽收 2 天前） |
| `customer2` 陳怡君 | 顧客 | 外套（簽收 10 天前）、另一件外套已出貨 4 天尚未簽收 |
| `agent1` 客服小李 | 客服人員 | 有「售後審核」，**沒有**「售後規則管理」 |
| `merchant1` 店長阿明 | 店家管理者 | 有「售後規則管理」 |

**頁面**

| 路由 | 版面 | 用途 |
|---|---|---|
| `/shop`、`/shop/orders/{OrderId}` | `ShopLayout` | 我的訂單、訂單明細＋物流歷程＋申請催件 |
| `/shop/after-sales/new?orderItemId=` | `ShopLayout` | 五步驟申請表單 |
| `/shop/after-sales` | `ShopLayout` | 我的申請；「待補件」可開窗補充說明與照片 |
| `/after-sales-review`（選單 91） | `MainLayout` | 審核佇列；開窗後「查看訂單」「查看售後規則」「查看照片」**各自展開**，再「要求補件／核准／駁回」 |
| `/after-sales-policies`（選單 92） | `MainLayout` | 規則清單與歷史版本、新增版本 |

只有顧客權限的帳號進 `/App` 會被導到 `/shop`。照片下載端點 `GET /api/after-sales-files/{id}/download`（Cookie 驗證，申請人本人或具「售後審核」檢視權限者；inline），見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)。

**權限鍵**：`顧客商店`、`售後服務`（選單群組 9）、`售後審核`、`售後規則管理`。

⚠️ **「預設角色」會自動取得新頁面**：`DefaultRoleViewSeeder` 每次啟動都把新頁面加入「預設角色」，所以新建帳號與 Google 自動建立的帳號會看得到「售後審核」「售後規則管理」「顧客商店」（1.0.4 起再加「Agent 執行紀錄」）。正式部署時，管理員應到角色管理把這些頁從「預設角色」移除（見[正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)）。

**稽核代碼**：`Shop.Request.Submit`／`Shop.Request.Resubmit`／`Shop.Request.MoreInfo`／`Shop.Request.Decide`／`Shop.Policy.AddVersion`／`Shop.Attachment.Download`（常數在 `AuditActions.Shop`；Detail 只記狀態與編號，不含地址、手機、說明內容）。

**測試**：`ShopSchemaTests`、`AfterSalesEligibilityTests`、`AfterSalesPolicyServiceTests`、`ShopOrderServiceTests`、`AfterSalesRequestServiceTests`、`AfterSalesFileControllerTests`、`DemoSeederTests`。

**手動驗證傳統劇本**：見 [企劃規劃說明書 §5.2](../prd/售後Agent示範系統企劃規劃說明書.md)。

## 5. 已實作：M2 單一售後 Agent（1.0.4）

顧客在 `/shop/assistant`「線上客服」用自然語言辦理售後；Agent 只能透過受控工具讀資料，**寫入一定要顧客在確認卡上按下確認**。
同一個畫面的右側（窄螢幕在下方）是「幕後面板」，把每一步用白話說明並標出資料來源。後台 `/agent-runs`（選單 93）可回看任何一輪。

### 5.1 架構

```text
顧客（Blazor ShopAssistantView）
   │  文字／確認／照片
   ▼
AfterSalesAgentRunner（Scoped；每輪一個 AfterSalesAgentGuard）
   │  ChatClientAgent ── 中介層 AfterSalesAgentGuard（函式呼叫前後）
   │     └ UseFunctionInvocation(MaxSteps, MaxConsecutiveToolFailures)
   │          └ TracingChatClient（發「模型看到什麼」事件）
   │               └ AuditingChatClient（額度／Token 用量／AI 對話紀錄，M0）
   │                    └ OpenAI SDK（預設 Responses API，store=false）
   ▼
AfterSalesAgentTools（6 個 AIFunction）→ ShopOrderService／AfterSalesPolicyService／AfterSalesRequestService
   ▼
AgentSessionStore（會話）／AgentTraceService（軌跡）→ SQLite
```

- 工具由 `AIFunctionFactory` 建立，**參數 schema 不含任何使用者識別欄位**：身分一律由程式取自登入狀態（`CurrentUserService`），並在每次工具呼叫前以 `IPermissionChecker` 即時核對「顧客商店:view」。
  模型多給 `customerUserId` 之類的參數會被忽略。
- 模型**永遠看不到**：使用者 Id、收件人姓名、地址、手機、付款資訊；價格與庫存數量也不送（庫存只回有貨／無貨）。幕後面板每個工具結果都會列出「未送出：…」。
- 顧客看到的規則條文一律由工具結果渲染的「規則卡」呈現（`AgentPolicyCardView`），不採模型轉述；簽收第 N 天由程式計算。
- 提示詞可在 `/prompt-templates` 的「AI 售後客服 Agent」分頁調整（範本鍵 `AfterSalesAgent`，預設值 `AfterSalesAgentPromptDefaults.Instructions`）；固定附加的規則仍由 `AiPromptGuardrails` 加在後面。

### 5.2 工具五要素

統一回傳信封 `ToolOutcome{status, data, message, retryable, withheld_fields}`（JSON 欄位小寫蛇形）；`status`：`ok`／`not_found`／`timeout`／`forbidden`／`unavailable`／`no_policy`／`conflict`／`blocked`／`invalid`。
每次呼叫另有逾時（`ToolTimeoutSeconds`）；誰能呼叫＝已登入且具「顧客商店」檢視權限、且為會話擁有者的顧客。

| 工具 | 輸入 | 輸出 | 失敗說明 | 性質 |
|---|---|---|---|---|
| `query_my_orders` | `color?`、`keyword?` | 訂單商品清單（訂單編號、品名、顏色、尺碼、配送狀態） | 無符合 → `not_found` | 只讀 |
| `get_effective_policy` | `orderItemId`、`issueType` | 規則卡（條文、來源、版本、生效日、簽收天數、不符原因、尚缺資訊） | 非本人 → `not_found`；`no_policy`／`conflict`；未知類型 → `invalid` | 只讀 |
| `check_variant_stock` | `orderItemId` | 同款各規格有貨／無貨（不含數量） | 無資料 → `not_found` | 只讀 |
| `track_shipment` | `orderItemId` | 物流商、狀態、歷程、可否催件 | 非本人 → `not_found` | 只讀 |
| `request_photo_upload` | `orderItemId`、`reason` | 已上傳張數／上限 | 非本人 → `not_found` | 停等（本輪停在等待上傳） |
| `create_after_sales_request` | `orderItemId`、`issueType`、`description`、`targetSize?`、`targetColor?`、`isWorn?` | 申請編號（寫入後回查確認存在才回報） | 資格不符 → `invalid`（附不符原因、尚缺資訊）；送件拋例外 → `unavailable` | **寫入**（`ApprovalRequiredAIFunction`） |

`issueType`：`size_exchange`／`color_exchange`／`return_refund`／`quality_issue`／`shipment_urge`。

### 5.3 停止原因（`AgentStopReason`，由程式判定）

白話文字集中在 `Share/Agents/AgentStopLabels`，顧客面板與後台共用。

| 停止原因 | 何時 | 是否繼續 |
|---|---|---|
| `Answered` | Agent 已回覆（或申請已建立） | 結束本輪 |
| `AwaitingUserReply` | 最後一段回覆含「？」，等顧客回答 | 等顧客 |
| `AwaitingConfirmation` | 顯示確認卡 | 等顧客按確認／取消；改打字視為不確認 |
| `AwaitingPhotoUpload` | `request_photo_upload` 成功 | 等顧客上傳，完成後自動送「（已上傳 N 張照片）」 |
| `StepLimitReached` | 達 `MaxSteps`（最後一次不帶工具，迫使模型收尾） | 程式說明 |
| `ConsecutiveToolFailures` | 實際執行且非 ok 的工具結果連續達上限（`blocked` 不算） | 程式說明 |
| `Forbidden` | 身分核對未通過 | **立即**終止，固定說明請重新登入 |
| `Timeout`／`ServiceUnavailable` | 工具逾時／服務不可用 | 終止，固定說明（逾時：「…不代表您沒有這筆訂單。已為您轉交客服。」） |
| `PolicyConflict` | 規則衝突 | 封鎖建立申請 |
| `QuotaExceeded`／`ModelError`／`Canceled` | 額度用完／模型呼叫失敗／顧客取消 | 中止 |

不確定即停：`not_found`／`timeout`／`conflict`／`no_policy` 之後若模型想建立申請，程式直接封鎖（`blocked`：「資訊不足或規則衝突，請先向顧客確認或轉人工」），不顯示確認卡。

### 5.4 確認關卡流程

1. 模型提出 `create_after_sales_request` → 框架轉成核准請求（`ToolApprovalRequestContent`），**工具尚未執行**。
2. Runner 先查是否被封鎖；沒有則由程式**重新查詢訂單與規則**組成確認卡（商品、規格原 → 新、類型、規則版本、簽收第 N 天、是否穿過、照片張數），並先跑一次 `AfterSalesEligibility.Evaluate`；不符就不顯示卡，把原因交回模型照實說明。
3. 顧客按「確認送出」→ `ConfirmAsync` 以核准回應續跑，**工具以核准當時記下的原始參數執行**，執行前再次重核身分與資格 → `AfterSalesRequestService.SubmitAsync`（`Channel=Agent`、`CaseKey=會話 Key`）→ 回查 → 回報申請編號。按「取消」＝拒絕，不寫入。送件一律等到完成、不受顧客關閉頁面（呼叫端取消）影響：申請已寫入時確認卡照樣連到申請，回覆為「申請已建立（編號），但 AI 回覆暫時失敗…」；這一輪若完全沒有結果，卡片下方補一則「這次沒有完成送出，請稍後再試或到「我的申請」確認。」
4. 顧客改打字而不按卡：下一輪框架自動拒絕未回覆的核准請求，卡片作廢，不寫入；已處理或已作廢的卡再按也不會重複送出。決定以條件式更新（`Approved` 仍為空才寫入，`AgentSessionStore.TryClaimConfirmationAsync`）搶下，兩個分頁同時按同一張卡只有一個會送給模型，另一個直接回「這張確認卡已經處理過或已失效…」。
5. 同一商品同類型已有處理中的申請時，寫入被服務層擋下（見 §5.7），模型會照實說明。目前確認卡顯示前**不**預先檢查重複申請（按下確認後才被擋）。

### 5.5 會話與軌跡持久化

| 實體 | 用途 |
|---|---|
| `AgentSessionRecord` | MAF `AgentSession` 序列化後的 `StateJson`（含歷史與待回覆的核准請求）、`IsClosed`、`LastStopReason`；`SessionKey` 唯一、`CustomerUserId` 索引。重新整理頁面可接續，「新對話」關閉舊會話並作廢未決卡、清掉待傳照片 |
| `AgentRunTrace`／`AgentRunStep` | 每輪一筆軌跡與逐步事件（`Kind`、`Source`、白話 `PlainText`、`RawJson`）；`GetRunsAsync` 供後台列表（顧客名稱以關閉過濾器的明確查詢取得） |
| `AgentConfirmation` | 確認卡紀錄（`ApprovalRequestId`、`ToolName`、`ArgumentsJson`、`CardJson`、`Approved`、`DecidedAt`、`AfterSalesRequestId`） |

migration `AddAgentRuntime`；四個實體不做軟刪除，`SessionKey`／`CustomerUserId`／`AfterSalesRequestId` 不建外鍵。
`Responses` API 一律 `store=false`，序列化會話不含 `conversationId`，歷史完全由本表保存。
照片先存在記憶體的 `AgentPhotoInbox`（每會話最多 3 張、每張 10 MB），通過確認才隨申請寫入檔案存放區。

### 5.6 幕後面板與後台紀錄

- 事件種類（`AgentStepKind`）：`UserMessage`、`ModelContext`、`ToolRequest`、`ProgramCheck`、`ToolResult`、`ModelReply`、`ConfirmationShown`、`ConfirmationDecided`、`Stop`。
- 來源色標（`AgentDataSource`）：對話、資料庫、規則、記憶（M5 才會出現）、程式。
- 每列有白話與可展開的原始資料（`RawJson`，不含地址／手機）；面板開關記在瀏覽器（預設開）。
- 後台 `/agent-runs`（選單 93、權限鍵「Agent 執行紀錄」，歸「售後服務」群組，客服、店家與管理員有）：唯讀，列表＋逐步時間軸，與顧客面板共用 `AgentStepTimeline`。
- 埋點（`ProcessMetricActions`）：`AgentSendMessage`、`AgentConfirm`、`AgentReject`、`AgentUploadPhoto`（`Channel=Agent`、`Actor=Customer`）。Token 用量的作業名稱：`TokenUsageOperations.AfterSalesAgent`＝「AI 售後客服 Agent」。
- 沒有新增稽核代碼（`AuditActions` 不變）；模型呼叫的額度擋下沿用 `Ai.QuotaBlocked`，申請寫入沿用 `Shop.Request.Submit`。

### 5.7 M1 服務補強（隨 M2 一併實作）

| 項目 | 規則與訊息 |
|---|---|
| 重複申請 | 同商品、同類型，已有 `Submitted`／`NeedMoreInfo` 的申請 → 「這個商品已有處理中的同類申請（{RequestNo}），請到「我的申請」查看。」（檔案寫入前檢查，寫入交易內再檢查一次） |
| 目標規格驗證 | 換尺碼／換顏色：目標與原規格相同 →「目標規格與原規格相同」；沒有該規格 →「找不到這個尺碼」／「找不到這個顏色」 |
| 照片位元組上限 | 以實際寫入的位元組計（不信任宣告大小），超過 →「照片超過 10 MB」，已寫入的檔案清除；`FileSize` 存實際位元組 |
| 規則版本基準 | `AfterSalesPolicyModel.BaseVersion`：新增版本時若現行版本已不是開窗時的版本 →「規則已被其他人更新，請重新整理後再新增版本。」 |
| 審核備註 | 核准／駁回時 `StaffNote` 空白即清除（不殘留先前的補件說明） |
| 催件資格 | `AfterSalesEligibility.CanRequestUrge`／`UrgeAfter` 移到 Business，訂單明細頁與 Agent 共用；`Evaluate` 多收 `shippedAt` |
| 庫存查詢 | `ShopOrderService.GetVariantStockAsync` |
| 示範角色權限同步 | `DemoAccountSeeder` 每次啟動把既有示範角色的 `TabViewJson` 與權限同步到 `DemoSeedData`（管理員手動調整會在重啟後被蓋回） |

### 5.8 設定與實機注意

`AfterSalesAgentSettings`（`MaxSteps`、`MaxConsecutiveToolFailures`、`ToolTimeoutSeconds`、`ApiStyle`、`ReasoningEffort`、`Temperature`）見 [腳手架開發指引](../guides/腳手架開發指引.md)與[日誌與設定檔說明](../operations/日誌與設定檔說明.md) §4.3.1.2。

- 需要可用的 AI 金鑰（`AiSettings`）；未設定時頁面顯示「AI 服務尚未設定，請洽管理員。」並停用輸入。
- 實機（Azure `gpt-6.1-sol`）：Chat Completions＋工具會 400，所以預設 Responses、不送推理強度與溫度；單次模型呼叫約 1.5～12 秒。`ReasoningEffort` 可設 `low` 等值（依模型支援值），但**尚未**在多步工具迴圈（Responses API、`store=false`、帶推理）下實機驗證，採用前必須先完整跑過劇本確認。
- 實機劇本 A（換大一碼＋確認卡）與 B（一句兩事＋停等照片）均照設計跑完，操作步驟見[企劃規劃說明書 §5.3](../prd/售後Agent示範系統企劃規劃說明書.md)。
- 已知限制：規則卡由 `get_effective_policy` 產生，該工具固定以「是否穿過＝未知」評估，所以規則卡可能寫「尚缺資訊：是否穿過」，即使顧客已回答；確認卡上的事實才是以顧客回答為準。

### 5.9 測試

`AfterSalesAgentToolsTests`（越界：偽造他人 Id、非本人資料、逾時、權限）、`AfterSalesAgentRunnerTests`（腳本化模型：未確認不寫入、改打字不確認、拒絕、重核失敗、逾時／forbidden 終止、`not_found` 後封鎖寫入、步數上限、停等照片、額度、會話還原後確認只建一筆、軌跡不含地址手機、Responses／Chat Completions 請求內容）、
`AgentTraceServiceTests`、`ShopAssistantViewTests`、`AgentRunsViewTests`、`PromptTemplateTests`、`OptionsValidationTests`；實機冒煙 `Category=Live`（需 `CHA_LIVE_AI_*`）。

## 6. 規劃中

M3（故障模擬、劇本、回放）到 M6（效益對照頁）尚未實作，範圍見 [設計文件 §6](../prd/售後Agent展示系統-設計.md)。

> 返回 [features 索引](README.md)
