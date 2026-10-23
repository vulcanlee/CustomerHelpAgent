# 售後 Agent 示範系統

- 文件版本：0.2
- 文件狀態：部分實作（M0 計費接軌、M1 傳統流程已實作；M2–M6 規劃中）
- 現行系統版本：1.0.3
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
| 串流 | 收齊所有片段後才記一筆 |

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
- 端點規則沿用 `AiChatEndpoint.CreateBaseUri`（Azure OpenAI v1 與 OpenAI 共用 `/v1/chat/completions`）。
- `operation` 會成為 Token 用量的「作業」欄位；新增正式作業名稱時依速查表 §6.7 登記 `TokenUsageOperations` 常數。

## 3. 測試

| 測試 | 守住什麼 |
|---|---|
| `AgentChatClientFactoryTests` | 端點正規化、實際 HTTP 路徑、設定不完整時拒絕建立 |
| `AuditingChatClientTests` | 成功記帳、額度擋下（稽核 detail 格式）、429 記 `RateLimited`、取消不記用量、串流只記一筆、`ChatClientAgent` 帶工具時兩回模型呼叫各記一筆且共用對話 Id |
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

⚠️ **「預設角色」會自動取得新頁面**：`DefaultRoleViewSeeder` 每次啟動都把新頁面加入「預設角色」，所以新建帳號與 Google 自動建立的帳號會看得到「售後審核」「售後規則管理」「顧客商店」。正式部署時，管理員應到角色管理把這三頁從「預設角色」移除（見[正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)）。

**稽核代碼**：`Shop.Request.Submit`／`Shop.Request.Resubmit`／`Shop.Request.MoreInfo`／`Shop.Request.Decide`／`Shop.Policy.AddVersion`／`Shop.Attachment.Download`（常數在 `AuditActions.Shop`；Detail 只記狀態與編號，不含地址、手機、說明內容）。

**測試**：`ShopSchemaTests`、`AfterSalesEligibilityTests`、`AfterSalesPolicyServiceTests`、`ShopOrderServiceTests`、`AfterSalesRequestServiceTests`、`AfterSalesFileControllerTests`、`DemoSeederTests`。

**手動驗證傳統劇本**：見 [企劃規劃說明書 §5.2](../prd/售後Agent示範系統企劃規劃說明書.md)。

## 5. 規劃中

M2（單一 Agent）到 M6（效益對照頁）尚未實作，範圍見 [設計文件 §6](../prd/售後Agent展示系統-設計.md)。

> 返回 [features 索引](README.md)
