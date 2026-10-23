# 售後 Agent 示範系統

- 文件版本：0.4
- 文件狀態：部分實作（M0 計費接軌、M1 傳統流程、M2 單一售後 Agent、M3 展示控制已實作；M4–M6 規劃中）
- 現行系統版本：1.0.5
- 首次實作版本：1.0.2
- 最後核對日期：2026/10/24

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
| `get_effective_policy` | `orderItemId`、`issueType`、`isWorn?`（只在顧客明確說了有沒有穿過時才填） | 規則卡（條文、來源、版本、生效日、簽收天數、不符原因、尚缺資訊） | 非本人 → `not_found`；`no_policy`／`conflict`；未知類型 → `invalid` | 只讀 |
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
| `StepLimitReached` | 達 `MaxSteps`（最後一次不帶工具，迫使模型收尾）；或本輪模型呼叫總數已達 `MaxSteps + 1`（確認請求被程式拒絕後的續跑也累計在內，`TracingChatClient` 在送出前擋下，被額度擋下的呼叫不計） | 程式說明 |
| `ConsecutiveToolFailures` | 實際執行且非 ok 的工具結果連續達上限（`blocked` 不算） | 程式說明 |
| `Forbidden` | 身分核對未通過 | **立即**終止，固定說明請重新登入 |
| `Timeout`／`ServiceUnavailable` | 工具逾時／服務不可用 | 終止，固定說明（逾時：「…不代表您沒有這筆訂單。已為您轉交客服。」） |
| `PolicyConflict` | 規則衝突 | 封鎖建立申請 |
| `QuotaExceeded`／`ModelError`／`Canceled` | 額度用完／模型呼叫失敗／顧客取消 | 中止 |

不確定即停：`not_found`／`timeout`／`conflict`／`no_policy` 之後若模型想建立申請，程式直接封鎖（`blocked`：「資訊不足或規則衝突，請先向顧客確認或轉人工」），不顯示確認卡。
同一輪出現過這些結果、而模型最後的回覆（模型文字，不是程式固定說明）沒有追問就直接下結論時，幕後面板多一列 `Warning`（來源＝程式）：「模型在查無資料／逾時／規則衝突後直接下結論，請留意。」
以下情況不標紅：停止原因為 `PolicyConflict`、最後一段回覆含「轉人工」或「客服」（照提示詞建議轉人工是正確收尾），或同一個工具之後再呼叫回 `ok`（該工具先前的查無／無規則等不確定即清除；寫入封鎖不因此解除）。

### 5.4 確認關卡流程

1. 模型提出 `create_after_sales_request` → 框架轉成核准請求（`ToolApprovalRequestContent`），**工具尚未執行**。
2. Runner 先查是否被封鎖；再查這個商品是否已有本人處理中的同類申請（`AfterSalesRequestService.GetOpenRequestNoAsync`，與送件去重同一條規則），
   有就不顯示卡，回模型「這個商品已有處理中的同類申請（{RequestNo}）」；都沒有才由程式**重新查詢訂單與規則**組成確認卡（商品、規格原 → 新、類型、規則版本、簽收第 N 天、是否穿過、照片張數），並先跑一次 `AfterSalesEligibility.Evaluate`；不符就不顯示卡，把原因交回模型照實說明。
3. 顧客按「確認送出」→ `ConfirmAsync` 以核准回應續跑（回應一律由還原會話裡框架記錄的原始 `ToolApprovalRequestContent` 以 `CreateResponse` 產生，帶原始呼叫的 CallId；`RequestId` 是框架加了 `ficc_` 前綴的值，不能拿來當 CallId，否則下一輪框架對不上而失敗），**工具以核准當時記下的原始參數執行**，執行前再次重核身分與資格 → `AfterSalesRequestService.SubmitAsync`（`Channel=Agent`、`CaseKey=會話 Key`）→ 回查 → 回報申請編號。按「取消」＝拒絕，不寫入。送件一律等到完成、不受顧客關閉頁面（呼叫端取消）影響：申請已寫入時確認卡照樣連到申請，回覆為「申請已建立（編號），但 AI 回覆暫時失敗…」；這一輪若完全沒有結果，卡片下方補一則「這次沒有完成送出，請稍後再試或到「我的申請」確認。」
4. 顧客改打字而不按卡：下一輪框架自動拒絕未回覆的核准請求，卡片作廢，不寫入；已處理或已作廢的卡再按也不會重複送出。決定以條件式更新（`Approved` 仍為空才寫入，`AgentSessionStore.TryClaimConfirmationAsync`）搶下，兩個分頁同時按同一張卡只有一個會送給模型，另一個直接回「這張確認卡已經處理過或已失效…」。改打字與「新對話」的作廢也是條件式更新（`AgentSessionStore.VoidPendingConfirmationAsync`，`Approved == null → false`，1.0.5 起），不會把另一個分頁剛搶下的「同意」改寫成「拒絕」。
5. 同一商品同類型已有處理中的申請時，確認卡顯示前就擋下（第 2 步）；顯示卡之後才出現的重複申請，寫入時仍會被服務層擋下（見 §5.7），模型會照實說明。

### 5.5 會話與軌跡持久化

| 實體 | 用途 |
|---|---|
| `AgentSessionRecord` | MAF `AgentSession` 序列化後的 `StateJson`（含歷史與待回覆的核准請求）、`IsClosed`、`LastStopReason`、`StateVersion`（樂觀版本，1.0.5 起）；`SessionKey` 唯一、`CustomerUserId` 索引。重新整理頁面可接續，「新對話」關閉舊會話並作廢未決卡、清掉待傳照片。存回以 `StateVersion == 本輪開始時讀到的版本` 條件式更新並加 1；另一個分頁已先存回（版本不符）→ 不覆寫，拋 `AgentSessionConflictException`「這段對話已在其他分頁更新，請重新整理頁面。」，頁面以系統訊息顯示（該輪若已建立申請，訊息前帶申請編號；該輪顯示的確認卡一併作廢） |
| `AgentRunTrace`／`AgentRunStep` | 每輪一筆軌跡與逐步事件（`Kind`、`Source`、白話 `PlainText`、`RawJson`）；`GetRunsAsync` 供後台列表（顧客名稱以關閉過濾器的明確查詢取得） |
| `AgentConfirmation` | 確認卡紀錄（`ApprovalRequestId`、`ToolName`、`ArgumentsJson`、`CardJson`、`Approved`、`DecidedAt`、`AfterSalesRequestId`） |

**還原頁面（1.0.5 起）**：對話以執行軌跡重建，不讀會話原文——`GetHistoryAsync` 取該會話所有執行的 `UserMessage` 與每輪最後一步 `TurnResult`（`RawJson`＝`{ replyText, isProgramReply, stopReason, stopExplanation, policyCards[], createdRequestNo }`，不含個資），依時間排列，最後附未決確認卡；程式說明還原成系統訊息、停止說明與規則卡照即時回合的方式呈現，被固定說明取代的模型原文不會出現；額度、模型錯誤、取消的那一輪也照當時畫面還原。幕後面板以 `GetTraceEventsAsync`（`AgentTraceService.GetSessionStepsAsync`，只限本人）還原。1.0.5 之前建立的會話（軌跡裡沒有任何 `TurnResult`）退回讀會話原文（只有文字，回覆一律當 Agent 泡泡，與 1.0.4 相同）；跨版本延續的會話只還原有 `TurnResult` 的那幾輪回覆，之前幾輪只看得到顧客的話。

migration `AddAgentRuntime`、`AddAgentSessionVersion`（1.0.5）；四個實體不做軟刪除，`SessionKey`／`CustomerUserId`／`AfterSalesRequestId` 不建外鍵。
`Responses` API 一律 `store=false`，序列化會話不含 `conversationId`，歷史完全由本表保存。
照片先存在記憶體的 `AgentPhotoInbox`（每會話最多 3 張、每張 10 MB），通過確認才隨申請寫入檔案存放區。

### 5.6 幕後面板與後台紀錄

- 事件種類（`AgentStepKind`）：`UserMessage`、`ModelContext`、`ToolRequest`、`ProgramCheck`、`ToolResult`、`ModelReply`、`ConfirmationShown`、`ConfirmationDecided`、`Stop`、`Warning`（程式提醒，見 §5.3）、`TurnResult`（每輪最後一步、來源＝程式，白話「本輪結果：回覆為程式說明／Agent 文字，規則卡 N 張…」，供還原頁面，見 §5.5）。
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
- 實機（Azure `gpt-6.1-sol`）：Chat Completions＋工具會 400，所以預設 Responses、不送推理強度與溫度；單次模型呼叫約 1.5～12 秒。`ReasoningEffort=low` **已實機驗證**（1.0.5，2026/10/24）：Responses API、`store=false` 下完整跑完劇本 A 的多步工具迴圈（查訂單 → 追問 → 查規則、查庫存 → 確認卡 → 確認建立申請，6 次模型呼叫全部成功），結果與不送推理強度時相同；同一模型設 `none` 會被服務端以 HTTP 400 拒絕（證明設定確實有送出）。`minimal`、`medium`、`high` 未驗證。預設仍為空字串（不送）。
- 實機劇本 A（換大一碼＋確認卡）與 B（一句兩事＋停等照片）均照設計跑完，操作步驟見[企劃規劃說明書 §5.3](../prd/售後Agent示範系統企劃規劃說明書.md)。
- 規則卡由 `get_effective_policy` 產生；顧客明確說了有沒有穿過時，模型會帶 `isWorn`，規則卡就不再寫「尚缺資訊：是否穿過」。模型沒帶時仍以「未知」評估，確認卡上的事實以建立申請時的 `isWorn` 為準。

### 5.9 測試

`AfterSalesAgentToolsTests`（越界：偽造他人 Id、非本人資料、逾時、權限）、`AfterSalesAgentRunnerTests`（腳本化模型：未確認不寫入、改打字不確認、拒絕、重核失敗、逾時／forbidden 終止、`not_found` 後封鎖寫入、已有處理中申請不出卡、步數上限（含跨續跑的每輪總量）、查無後直接下結論標紅（規則衝突建議轉人工、同工具後續成功不標紅）、確認／取消後與還原會話後下一輪可正常對話、上傳照片後續聊、停等照片、額度（被擋下的呼叫不計數）、會話還原後確認只建一筆、軌跡不含地址手機、Responses／Chat Completions 請求內容、以 `TurnResult` 還原（程式說明、規則卡、不出現被取代的模型原文、幕後面板、舊會話退回讀原文）、會話版本衝突）、
`AgentSessionStoreTests`（作廢不改寫已搶下的決定、版本不符拒絕覆寫）、`AgentTraceServiceTests`、`ShopAssistantViewTests`、`AgentRunsViewTests`、`PromptTemplateTests`、`OptionsValidationTests`；實機冒煙 `Category=Live`（需 `CHA_LIVE_AI_*`）。

## 6. 已實作：M3 展示不翻車（1.0.5）

M3 讓一個人就能穩定地展示：一鍵把資料重置成劇本需要的樣子、刻意製造故障、模型不穩時改用錄好的回放，並免密碼切換成示範帳號。
⚠️ **所有示範功能只在 `DemoSettings:Enabled=true` 時存在**；`ASPNETCORE_ENVIRONMENT=Production` 且 `DemoSettings:Enabled=true` 時 `StartupSafetyValidator` 拒絕啟動
（「DemoSettings:Enabled 不可在 Production 開啟…」），出貨的 `appsettings.Production.json` 為 `false`。實作計畫見 [M3 實作計畫](../prd/售後Agent展示系統-M3實作計畫.md)。

### 6.1 展示控制狀態（`DemoControlService`）

- Singleton，啟動時在 migration 與系統參數之後 `LoadAsync()`；狀態存在固定單列的 `DemoControlState`（`Id=1`：`FaultsJson`（故障名稱的 JSON 陣列）、`Mode`（`Live`／`Replay`）、`ActiveRecordingId`、`BackstageDefaultOn`、`UpdatedAt`），讀取走記憶體快照、不查資料庫。
- 寫入（`SetFaultAsync`、`ClearFaultsAsync`、`SetModeAsync`、`SetBackstageDefaultAsync`）先存資料庫成功才換快照並發 `Changed`；故障、模式與幕後面板預設是**全站共用**、對所有人立即生效。
- 示範模式關閉時一律回報：無故障、即時、沒有錄製、幕後面板預設開；所有寫入方法拋 `InvalidOperationException`。`SetModeAsync(Replay, null)` 拒絕（回放一定要指定錄製）。
- 服務本身不寫稽核（Singleton 拿不到登入者）；稽核由展示控制台以登入的管理員寫（§6.8）。

### 6.2 故障模擬（7 種，`DemoFault`）

| 故障 | 注入位置 | 效果 | 影響範圍 |
|---|---|---|---|
| `OrderServiceTimeout` 訂單系統逾時 | `ShopOrderService` 五個讀取方法 | 等 `FaultDelay`（30 秒）後拋 `TimeoutException`；Agent 工具 15 秒逾時先放棄 → 停止原因 `Timeout`，固定說明含「不代表您沒有這筆訂單」 | Agent 與傳統頁面 |
| `OrdersNotFound` 查無訂單 | 同上 | 訂單清單為空、單筆查不到；規格與庫存查詢也回空 | Agent 與傳統頁面 |
| `ShipmentAnomaly` 物流異常 | `ShopOrderService.GetMyOrderDetailAsync` | 物流狀態改為「配送異常」並多一筆異常事件（只改讀出的結果，不寫資料庫） | Agent 與傳統頁面 |
| `PolicyServiceUnavailable` 規則服務停擺 | `AfterSalesPolicyService.GetEffectiveAsync` | 拋例外 → 工具 `unavailable`；傳統送件失敗、不建立申請 | Agent 與傳統頁面 |
| `PolicyConflict` 規則衝突 | 同上 | 多回一份「（衝突版本）」的假規則（`Id=0`、版本 +1，不寫入）→ `conflict`，封鎖建立申請 | Agent 與傳統頁面 |
| `PolicyNotFound` 無適用規則 | 同上 | 回 `NoPolicy` → 工具 `no_policy` | Agent 與傳統頁面 |
| `PermissionExpired` 權限過期 | `AfterSalesAgentTools` 身分核對 | 工具一律 `forbidden`，程式立即停止本輪；組確認卡的程式重查同樣不通過（不出卡） | **只有 Agent**（傳統流程的登入逾時本來就由真實驗證機制處理） |

同時開多個規則類故障時依「停擺 → 無規則 → 衝突」的順序套用。每次注入記一筆英文 Warning 日誌（`Demo fault injected…`）。
逾時故障被工具放棄的那個工作會被觀察（`ObserveFault`），不會在例外紀錄留下假的 Error。故障名稱與說明集中在 `Share/Demo/DemoFaultLabels`（控制台、顧客頁共用；`Ordered` 必須涵蓋每一種 `DemoFault`，`FaultLabels_OrderedContainsEveryFaultExactlyOnce` 守門）。

模型直接提出建立申請時，Runner 組確認卡的程式重查（讀訂單、去重、規則、物流）也走工具的外框 `AfterSalesAgentTools.RunConfirmationCheckAsync`（工具逾時＋身分與權限＋`ObserveFault`/`WaitAsync`）：F1 約工具逾時秒數後停在 `Timeout`（「…不代表您沒有這筆訂單…」）、F3 停在 `ServiceUnavailable`、F2 停在 `Forbidden`，三者都不出確認卡、不寫入，回覆是 Guard 的固定說明。

### 6.3 一鍵重置示範資料（`DemoResetService`，Scoped）

- 一個交易內依外鍵順序刪除：照片附件 → 售後申請 → 確認卡 → Agent 會話 → 物流事件 → 物流單 → 訂單明細 → 訂單 → 規格 → 商品 → 售後規則，再以 `DemoCatalogSeeder.WriteCatalogAsync`（與首次種子同一段程式）重建，日期以現在重新計算（黑鞋簽收 3 天前、白鞋 2 天前…）。失敗整批回滾、檔案不動，稽核 `Demo.Reset` 記一筆失敗。
- 交易提交後才刪照片檔，並通知所有 `IDemoResetParticipant`（照片暫存區 `AgentPhotoInbox`、回放游標 `ReplayCursorStore`）清空記憶體狀態。
- **不刪**：流程埋點、Agent 執行紀錄、Token 用量、AI 對話紀錄、錄製、稽核、展示控制狀態。
- **固定編號**：商品、規格、訂單、明細、物流單與事件、售後規則一律用 `DemoSeedData` 指定的 Id（例：`SO-DEMO-0001` 的明細 Id 1、黑鞋 38～44 為規格 1～7），重置幾次都一樣，錄製裡的工具參數才對得上。之後新增的編號只能往後加，不可重編。售後申請與 Agent 相關資料表仍自動編號。
- 重置時開著的顧客頁再送出或按確認，Runner 找不到會話而拒絕（「找不到這段對話…請重新整理頁面」），舊確認卡不可能再被確認。

### 6.4 錄製（示範模式＋即時模式自動進行）

```text
即時：TracingChatClient → RecordingChatClient → AuditingChatClient → OpenAI SDK
回放：TracingChatClient → ReplayChatClient（不連模型，沒有 AuditingChatClient）
```

- `RecordingChatClient` 在每次模型呼叫前算請求摘要、成功後存回應（串流收齊後才存；失敗、取消、中途放棄都不存）；錄製失敗（含串流合併 `ToChatResponse`、序列化、寫入）只記 Warning，不影響已計費的即時回合。每輪開始時決定是否錄製，中途切換模式不影響該輪。
- 資料表（migration `AddAgentRecording`）：`AgentRecording`（`SessionKey` 唯一、`CustomerUserId`、`Title`＝第一句前 20 字、`IsGolden`、`ScenarioCode`、`ModelName`、`TotalCostTwd`、`CreatedAt`；不對會話與使用者建外鍵，重置後仍在）、`AgentRecordedTurn`（顧客動作：`Send`／`Confirm`／`Reject`／`PhotosUploaded` 與文字）、`AgentRecordedCall`（`RequestDigest`、`ResponseJson`、`UsageJson`）。同一劇本只有一個金劇本（`ScenarioCode` 在 `IsGolden=1` 下的部分唯一索引＋`SetGoldenAsync` 交易內先降級舊的）。
- 費用：每記一筆呼叫就以 AI 對話紀錄（`ConversationId`＝會話）對到 Token 用量重新加總，存入 `TotalCostTwd`；AI 對話紀錄關閉時為空。
- **請求摘要 `ModelRequestDigest`**：只取顧客文字與工具結果（不含系統指示、模型文字、呼叫 Id），工具結果先正規化成固定格式的 JSON；申請編號 `AS\d{12}`、`request_id`、日期時間一律換成佔位字，所以**重置後同樣的資料摘要相同**；Id、天數、庫存、資格不正規化，資料真的不同時摘要就不同。雜湊為 SHA-256。
- 錄製不存系統指示以外的個資：工具結果本來就不含收件人、地址、手機（`RecordedJson_NeverContainsAddressOrPhone` 守門）；顧客自己打的字照存（與執行軌跡相同）。

### 6.5 回放（示範模式＋回放模式）

- 指定錄製後，Runner 改用 `ReplayChatClient`：**不呼叫真實模型、不檢查額度、不寫 Token 用量與 AI 對話紀錄、也不再錄製**；不需要 AI 金鑰（顧客頁不顯示「AI 服務尚未設定」）。工具、Guard、確認卡、寫入前重核照常**真的執行**——回放會真的建立申請。
- 每次模型請求先比對摘要：不同 → 停止原因 `ReplayDiverged`（「回放中止：目前資料或故障開關和錄製時不同，請到展示控制台重置示範資料後再試。」）；錄製用完 → `ReplayFinished`。
- 顧客動作也要和錄製一致：送出的句子、確認或取消、照片張數不同 → `ReplayActionMismatch`（「回放中請送出錄製的句子」等），**不呼叫模型、不推進游標、確認卡維持未決**，照錄製的動作再做一次就能繼續。
- 回放的模型文字若引用申請編號，換成這次真正建立的編號（例：錄製時是 `AS…0003`，回放建立 `AS…0005` 就顯示 `AS…0005`）。
- 游標（`ReplayCursorStore`，Singleton）只在記憶體：「新對話」、重置、換錄製都從頭開始；應用程式重啟後，開著的回放會話會在下一步中止，兩個分頁同時回放同一會話也會中止（展示限制）。
- 回放的那一輪看得出是回放：`AgentRunTrace.ReplayRecordingId`（migration `AddAgentRunReplayMarker`，即時執行為 null）記下用的錄製，後台「Agent 執行紀錄」的「模式」欄顯示「回放」標籤；幕後面板與步驟時間軸的模型步驟寫「回放：使用錄製 #N 的回應（未呼叫模型）…」，不寫「第 N 次呼叫模型」。
- 資料列為回放卻沒有錄製（不一致的 `DemoControlState`）時，`LoadAsync` 改回即時並記 Warning。

### 6.6 展示身分切換

- `DemoSwitchTicketService`（Singleton）發一次性票券（32 位元組亂數、60 秒、用過即焚、綁定發票者的 Cookie `Sid`）；`/Auths/DemoSwitch?ticket=…`（靜態 SSR、`[Authorize]`、`no-referrer`、`no-store`）兌換。
- 切換：只限管理員、示範模式、目標是四個示範帳號之一；簽發的是**目標帳號自己的** Cookie（工作階段版本、帳號狀態、改密碼等檢查照常），另帶展示者 Claims（`demo_presenter_id`、`demo_presenter_stamp`），不持久。顧客導到 `/shop`（或票券帶的本機 `returnUrl`，「準備此劇本」用 `/shop/assistant`），其他人到 `/App`。⚠️ 刻意略過目標帳號的兩步驟驗證（只限管理員＋示範模式）。
- 切回：畫面上方橫幅「展示模式：目前以 {名稱} 身分檢視」的「回到展示者」→ 再次核對展示者與目前帳號的工作階段版本，通過才切回並到 `/demo-console`；版本不符就登出並導到登入頁。
- 任何拒絕都不發 Cookie、寫失敗稽核（`reason=…`），導到 `/App?demoSwitch=failed`（顧客為 `/shop?demoSwitch=failed`），不回顯網址參數。詳見[認證授權與權限機制](../security/認證授權與權限機制.md)。

### 6.7 展示控制台 `/demo-console`

選單 id 69「展示控制台」（系統管理 › 系統設定與維運，只限管理員，權限鍵常數 `角色_展示控制台`）；使用者選單另有「切換展示身分」（`/demo-console#identity`）。示範模式關閉時頁面只顯示提示、全部停用（選單項目仍看得到）。

| 區塊 | 內容 |
|---|---|
| 狀態列 | 模式（即時／回放中 #id）、啟用中的故障、上次重置時間（取最新一筆成功的 `Demo.Reset` 稽核） |
| 劇本 | `Business/Startup/DemoScenarios` 的九個劇本卡（代碼、帳號、故障、顧客台詞、講解重點）＋「準備此劇本」：紅色確認 → 重置 → 只開這個劇本的故障 → 選回放且有金劇本才回放，否則即時（確認窗先說明）→ 取票券切換成劇本帳號並打開線上客服；任何一步失敗就停 |
| 故障模擬 | 七個開關＋「全部關閉」 |
| 示範資料 | 「一鍵重置示範資料」（紅色確認，說明會刪除所有業務資料並中斷進行中的對話） |
| 即時／回放 | 模式切換＋「回放的錄製」（最近 100 筆，金劇本標出劇本代碼）；沒有 AI 設定時提醒只能用回放 |
| 錄製清單 | 伺服器分頁；「設為金劇本」（選劇本）、「刪除」、批次刪除；刪到回放中的錄製自動改回即時 |
| 身分切換 | 四個示範帳號各一顆「以此身分檢視」 |
| 幕後面板預設 | 顧客頁幕後面板一開始是否打開（瀏覽器記住的偏好優先） |

| 劇本 | 帳號 | 故障 | 顧客台詞 |
|---|---|---|---|
| A 換尺碼（旗艦） | customer1 | 無 | 「剛買的鞋尺碼不合適，想換大一碼」→「黑色那雙，還沒穿過」 |
| B 一句兩事（旗艦） | customer1 | 無 | 「這雙想換大一碼，另一雙開膠了也幫我看看」→「黑的換大一碼，白的開膠」 |
| C 退貨逾期 | customer2 | 無 | 「上次買的外套想退貨」→「藏青色那件，還沒穿過」 |
| D 物流催件 | customer2 | 無（可另開物流異常） | 「我的外套怎麼還沒到？幫我催一下」 |
| F1 訂單系統逾時 | customer1 | 訂單系統逾時 | A 的第一句 |
| F2 權限過期 | customer1 | 權限過期 | A 的第一句 |
| F3 規則服務停擺 | customer1 | 規則服務停擺 | A 的兩句 |
| F4 規則衝突 | customer1 | 規則衝突 | A 的兩句 |
| F5 無適用規則 | customer1 | 無適用規則 | A 的兩句 |

A、B 台詞取自設計文件 §3.2；C、D、F 的台詞是實作時擬定的展示用語。台詞是錄製／回放的契約：改字會讓既有金劇本對不上，要同時重新錄製。

### 6.8 稽核代碼（`AuditActions.Demo`）

`Demo.Reset`（`DemoResetService`）、`Demo.FaultChanged`／`Demo.ModeChanged`／`Demo.RecordingGolden`／`Demo.RecordingDelete`（控制台，操作者＝登入的管理員）、`Demo.SwitchIdentity`／`Demo.SwitchBack`（`/Auths/DemoSwitch`）。Detail 格式見[稽核紀錄 PRD](../prd/稽核紀錄-prd.md) §六。幕後面板預設的變更不寫稽核。

### 6.9 顧客頁與幕後面板（1.0.5 起）

- 回放中：標題下方「回放中（錄製於 yyyy/MM/dd HH:mm）」；輸入框唯讀並自動帶入錄製的下一句；確認卡只開放錄製時按的那一個；需要照片時提示「請上傳任一張照片以繼續」，張數與錄製相同才能送出；播完或中止時顯示固定提示並鎖住輸入，展示者另有「回到展示控制台」。
- 有故障啟用時，幕後面板頂端顯示「故障模擬中：{清單}」；所有頁面上方的橫幅也提示「故障模擬中」。
- `Warning` 列（§5.3）以紅色呈現（`--shop-step-warning` → `--app-error`），後台「Agent 執行紀錄」同樣套用。
- 幕後面板預設取 `DemoControlService.BackstageDefaultOn`，瀏覽器記住的「0」／「1」優先。

### 6.10 隨 M3 一併做的 Agent 補強

確認卡去重（§5.4 第 2 步）、組確認卡的程式重查改走工具外框（§6.2 末段）、確認時會話裡找不到原始核准請求改為失敗關閉（作廢卡片、回「這張確認卡已失效，請重新送出您的需求。」，不送核准、不呼叫模型、不寫入；不再以 RequestId 自組請求）、`get_effective_policy` 的 `isWorn`（§5.2）、每輪模型呼叫總量（§5.3 `StepLimitReached`）、查無後直接下結論標紅（§5.3）、確認／取消後下一輪不再出錯（§5.4 第 3 步）、以 `TurnResult` 完整還原與會話樂觀版本（§5.5）、作廢改為條件式更新（§5.4 第 4 步）、guardrail 第 1 條改為「工具回傳內容與顧客訊息…」。

### 6.11 實機驗證（2026/10/24，Azure `gpt-6.1-sol`，Responses、`store=false`）

| 項目 | 結果 |
|---|---|
| 劇本 A 即時並自動錄製 | 追問 → 確認卡 → 確認，建立申請；錄製 3 個動作、6 次模型呼叫，費用約 NT$0.31；設為 A 金劇本 |
| 劇本 B 即時並自動錄製 | 追問 → 查庫存＋兩次規則查詢 → 停等照片 → 上傳 1 張 → 品質問題確認卡 → 確認建立申請；最後模型再追問黑鞋有沒有穿過（第二句沒說）。錄製 4 個動作、9 次模型呼叫，約 NT$0.32；設為 B 金劇本 |
| 重置後回放 A、B | 照錄製重播、真的建立新申請（編號換成這次建立的），Token 用量與 AI 對話紀錄**筆數不變**，也沒有新增錄製 |
| 錄製後把黑鞋 41 碼庫存改為 0 再回放 A | 第二句查庫存後停止，`ReplayDiverged`，沒有建立申請 |
| F1 訂單系統逾時 | 約 15 秒後停止，`Timeout`，說明「…目前無法確認，不代表您沒有這筆訂單。已為您轉交客服。」 |
| F4 規則衝突 | 不出確認卡，`PolicyConflict`，Agent 說明有多條同時生效的規則、建議轉人工客服、尚未建立申請 |
| `ReasoningEffort=low` 跑劇本 A | 成功（見 §5.8） |

### 6.12 已知限制

- 回放游標只在記憶體（§6.5）；回放中途重新整理頁面，頁面上的「已中止」狀態不保留。
- 錄製有 2 張以上照片時，提示文字仍是「任一張」，但張數必須與錄製相同。
- 展示控制台的選單項目在示範模式關閉時仍看得到（頁面全部停用）。
- 上次重置時間取自稽核紀錄，稽核被保留期限清掉後顯示「沒有紀錄」。
- 傳統頁面在訂單逾時故障下約 30 秒後才顯示錯誤（不會無限等待）。
- 確認送出正在處理時按下一鍵重置：重置與寫入沒有互斥，送件若剛好落在重置之後，申請會建立在重置後的新資料裡（展示時避免同時操作；重置後再查「我的申請」即可確認）。
- 應用程式重啟後回放游標遺失（§6.5），開著的回放會話下一步會 `ReplayDiverged` 中止；按「新對話」從錄製第一句重來即可，不必重置資料。
- 示範模式為即時時會一直自動錄製，錄製保存顧客打的字且**不受資料保存期限清理**、重置也不刪；只適用於示範環境，不需要時請在控制台刪除錄製。
- 展示控制台與展示橫幅訂閱 `DemoControlService.Changed`（處置時取消訂閱）；通知若剛好在元件處置之後才到，未等待的 `InvokeAsync(StateHasChanged)` 可能落空或留下一筆可忽略的例外，屬低風險，目前未特別處理。

### 6.13 測試

`DemoControlServiceTests`、`DemoFaultInjectionTests`（七種故障、逾時不留未觀察例外、關閉時不注入）、`DemoResetServiceTests`（日期重算、刪申請與檔案、規則回到 v1、紀錄保留、重置兩次 Id 相同、中途失敗回滾、上次重置時間）、
`AgentRecordingTests`（錄製順序、回放與關閉時不錄、重置後摘要相同、摘要正規化、金劇本唯一、不含地址手機、費用加總、串流合併失敗不影響回合）、`AgentReplayTests`（回放 A 不連模型且建立申請、不碰額度與用量、資料不同中止、播完、動作不符可重試、換回新編號、回放軌跡標記與白話）、`AfterSalesAgentRunnerTests`（`Card_UnderOrderTimeoutFault_ReturnsTimeoutExplanationQuickly`、`Card_UnderPolicyUnavailableFault_ReturnsUnavailable`、`Card_UnderPermissionExpiredFault_NoCardForbidden`、`Confirm_WhenApprovalRequestMissingFromSession_FailsClosed`）、`AgentRunsViewTests.AgentRuns_ReplayRun_ShowsReplayTag`、
`DemoSwitchTests`／`DemoSwitchDisabledTests`（切換、切回、拒絕路徑、returnUrl 只收本機路徑、工作階段版本）、`DemoConsoleViewTests`、`ShopLayoutDemoBannerTests`、`ShopAssistantViewTests`（回放標籤、唯讀帶入、確認卡只開錄製的選擇、照片張數、中止回控制台、幕後面板預設、紅色 Warning、故障清單）、
`ApiIntegrationTests.Startup_ProductionWithDemoEnabled_Fails`。

## 7. 規劃中

M4（客服後台 Workflow）到 M6（效益對照頁）尚未實作，範圍見 [設計文件 §6](../prd/售後Agent展示系統-設計.md)。

> 返回 [features 索引](README.md)
