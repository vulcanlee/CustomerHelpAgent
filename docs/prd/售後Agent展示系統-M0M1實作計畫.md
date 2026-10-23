# 售後 Agent 展示系統 M0＋M1 Implementation Plan

- 文件版本：0.1
- 文件狀態：規劃中（未實作）
- 現行系統版本：1.0.1
- 首次實作版本：尚未實作
- 最後核對日期：2026/10/23

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Context

設計文件（`docs/prd/售後Agent展示系統-設計.md`，已核准並推上 main）把系統拆成 M0–M6。本計畫只做前兩個里程碑：
**M0** 接上 Microsoft Agent Framework，讓之後所有 Agent 的每一次模型呼叫都沿用腳手架既有的額度、Token 用量與 AI 對話紀錄；
**M1** 自建模擬鞋服電商（商品、訂單、物流、售後規則、售後申請）、示範帳號與角色、商店風格顧客版面，以及**傳統做法**（顧客填表、客服人工審核）與操作埋點。
M1 完成後即可單獨展示「傳統做法」，也是 M2 Agent 工具要呼叫的業務服務。

**Goal:** 完成 MAF 計費接軌與可操作的傳統售後流程（顧客端＋客服端），全部通過品質關卡。

**Architecture:** M0 在 `CustomerHelpAgent.Web/Agents/` 新增 `AgentChatClientFactory`，以 OpenAI SDK 建 `IChatClient`，外包 `AuditingChatClient : DelegatingChatClient` 重做 `AiChatCompletionClient.CompleteAsync` 的額度／用量／對話紀錄步驟。
M1 在 AccessDatas 加實體與 migration，Business 加 `Shop*`／`AfterSales*` 服務（`IDbContextFactory`），Web 加 `ShopLayout` 與 `/shop/*` 顧客頁、後台「售後審核」「售後規則」兩頁。

**Tech Stack:** .NET 10、Blazor Server、AntDesign 1.6、EF Core 10 SQLite、xUnit 2.9 + bUnit、`Microsoft.Agents.AI.OpenAI` 1.25.0（帶入 `Microsoft.Extensions.AI.OpenAI` 10.10.1、`OpenAI` 2.14.0）。

**Spec:** `docs/prd/售後Agent展示系統-設計.md`（§3.2 旗艦劇本、§4.1 模擬業務系統、§4.2 傳統流程、§4.3「計費／日誌接軌」）。

## Global Constraints

- 路徑以 `src/CustomerHelpAgent/` 為根；分層 Web → Business → AccessDatas；Models／Dtos 只相依 Share；Razor 不得 `using BackendDBContext`。
- 套件版本只寫 `Directory.Packages.props`：`<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.25.0" />`（釘死）。
- `TreatWarningsAsErrors`、`dotnet format --verify-no-changes` 乾淨。
- Blazor 資料服務只有**一個**建構子，注入 `IDbContextFactory<BackendDBContext>`；所有 `Skip/Take` 前必有 `OrderBy…ThenBy(x => x.Id)`。
- 日誌樣板英文、PascalCase 佔位符，不得含 phone／mobile／email／token 等字（地址、手機永不入日誌）。
- 稽核代碼只能是 `AuditActions` 常數（格式 `Shop.Request.Submit`）。
- Icon 只用 BlazorMaterialIcons；通知只走 `ViewNotification`；確認只走 `ConfirmDialog`；`<Modal>` 必有 `Class` 且 `*-modal` 類別登記在 `Components/Commons/OverlayStyles.razor`；含 `EditForm` 的 Modal 依 `FormModalConventionTests`（`form-modal`、`MaskClosable="false"`、`OnCancel`、`Keyboard="true"`、無 `Width`）。
- 色票只在 `wwwroot/theme.css :root`（新增 `--shop-*` 別名）；不得 `@onkeydown` 於 `<EditForm>`；不得直接連結 `auths/logout`。
- **不自動 commit**（CLAUDE.md）：每個 Task 結束改為「檢查點」，由使用者自行 commit。
- 版本：M0 結束 `1.0.1 → 1.0.2`、M1 結束 `1.0.2 → 1.0.3`（`appsettings.json` `SystemSettings.SystemInformation.SystemVersion`，格式 `x.y.z (2026/MM/DD)`）。
- 文件 UTF-8 含 BOM；里程碑結束跑 `pwsh ./scripts/Invoke-QualityGate.ps1` 全綠。
- 測試指令：`dotnet test CustomerHelpAgent.Tests --filter "FullyQualifiedName~<類別名>"`（於 `src/CustomerHelpAgent/` 執行）。

## Review Focus

1. 顧客改網址查別人的訂單／申請／附件（`/shop/orders/{id}`、下載端點）→ 一律視同查無，不洩漏存在與否。→ Task 6、Task 7、Task 10 的擁有權測試。
2. 客服同時對同一申請按核准與要求補件（兩個分頁）→ 只有一個成功，另一個收到「狀態已變更」。→ Task 7 `DecideAsync_WhenStatusChanged_ShouldFail`。
3. 上傳非圖片或超過大小的「開膠照片」→ 拒絕並說明，不留下孤兒檔案。→ Task 7 `SubmitAsync_WithDisallowedFile_ShouldFailWithoutWritingFile`。
4. 店家新增規則版本後，舊版仍被引用的申請要能看出當時依據的版本 → `AppliedPolicyId` 不因改版改變。→ Task 5 `AddVersionAsync_ShouldCloseOldVersionAndKeepItQueryable`。
5. Azure OpenAI 端點填法（含／不含 `/openai/v1`、結尾斜線）→ 都導向同一個 `…/openai/v1/` 基底。→ Task 1 `CreateBaseUri_*` 測試。

---

## 檔案結構

| 檔案 | 責任 |
|---|---|
| `Web/Agents/AgentChatClientFactory.cs` | 驗證設定、建 OpenAI／Azure 內層 `IChatClient`，外包 `AuditingChatClient` |
| `Web/Agents/AuditingChatClient.cs` | 每次模型呼叫：額度、`TokenUsageEntry`、`AiCallLogEntry`、慢呼叫警告 |
| `Web/Agents/AiQuotaExceededException.cs` | 額度擋下時拋出，讓 Agent run 停止 |
| `Web/Ai/AiChatEndpoint.cs`（改） | 新增 `CreateBaseUri`，`Create` 改用它 |
| `Share/Shop/ShopEnums.cs` | `AfterSalesType`、`AfterSalesStatus`、`AfterSalesChannel`、`ProcessActor`、`ProductCategories` |
| `AccessDatas/Models/Shop/*.cs` | `Product`、`ProductVariant`、`SalesOrder`、`SalesOrderItem`、`Shipment`、`ShipmentEvent`、`AfterSalesPolicy`、`AfterSalesRequest`、`AfterSalesAttachment`、`ProcessMetric` |
| `Models/Shop/*.cs` | 顧客／客服畫面用的唯讀模型與輸入模型 |
| `Business/Helpers/AfterSalesEligibility.cs` | 純函式：簽收天數、資格判定（M2 Agent 重用） |
| `Business/Services/DataAccess/AfterSalesPolicyService.cs` | 有效規則查詢、版本清單、新增版本 |
| `Business/Services/DataAccess/ShopOrderService.cs` | 顧客本人訂單、明細、物流 |
| `Business/Services/DataAccess/AfterSalesRequestService.cs` | 送件、補件、審核佇列、決定 |
| `Business/Services/DataAccess/ProcessMetricService.cs` | 埋點寫入 |
| `Business/Services/Other/AfterSalesFileStore.cs` | 照片存放與路徑解析（比照 `ProjectFileStore`） |
| `Business/Startup/DemoAccountSeeder.cs`、`DemoCatalogSeeder.cs`、`DemoSeedData.cs` | 示範帳號角色；示範商品訂單規則（資料定義集中在 `DemoSeedData`，M3 重置重用） |
| `Models/Systems/DemoSettings.cs`＋`Web/Configuration/Validation/DemoSettingsValidator.cs` | 示範模式設定（Business 的 seeder 需讀取，故放 Models） |
| `Web/Components/Layout/ShopLayout.razor(.cs/.css)` | 商店風格版面、自行驗證登入 |
| `Web/Components/Pages/Shop/*.razor`＋`Views/Shop/*` | 顧客頁 |
| `Web/Components/Pages/AfterSales/*.razor`＋`Views/AfterSales/*` | 後台售後審核、售後規則 |
| `Web/Controllers/AfterSalesFileController.cs` | 照片下載（Cookie＋擁有權／權限） |

---

## M0 — MAF 接軌

### Task 1: 套件與 `IChatClient` 工廠

**Files:**
- Modify: `Directory.Packages.props`、`CustomerHelpAgent.Web/CustomerHelpAgent.Web.csproj`、`CustomerHelpAgent.Web/Ai/AiChatEndpoint.cs`
- Create: `CustomerHelpAgent.Web/Agents/AgentChatClientFactory.cs`
- Test: `CustomerHelpAgent.Tests/AgentChatClientFactoryTests.cs`

**Interfaces:**
- Produces:
  - `public static Uri AiChatEndpoint.CreateBaseUri(AiSettings settings)`：OpenAI → `{Endpoint 或 https://api.openai.com}/v1/`；Azure → `{Endpoint 去掉結尾 / 與 /openai/v1}/openai/v1/`。`Create` 改為 `new Uri(CreateBaseUri(s), "chat/completions")`，原有測試不變。
  - `public sealed class AgentChatClientFactory`（Scoped）：
    `public AgentChatClientFactory(IOptionsMonitor<AiSettings> aiOptions, IOptionsMonitor<SlowOperationSettings> slowOptions, ITokenUsageRecorder tokenUsageRecorder, IAiCallLogRecorder aiCallLogRecorder, IAiQuotaService quotaService, IAuditLogService auditLogService, CurrentUserService currentUserService, ILoggerFactory loggerFactory)`
    `public string? Validate()`（委派 `AiChatEndpoint.Validate`）
    `public IChatClient Create(string operation, Guid conversationId)`（設定無效時拋 `InvalidOperationException(Validate())`）
    `internal Func<AiSettings, IChatClient> InnerClientFactory { get; set; }`（預設 `CreateOpenAiChatClient`，測試可換掉）
    `internal static IChatClient CreateOpenAiChatClient(AiSettings s, HttpClient? httpClient = null)`：`new OpenAIClient(new ApiKeyCredential(s.ApiKey), new OpenAIClientOptions { Endpoint = CreateBaseUri(s), NetworkTimeout = TimeSpan.FromSeconds(s.TimeoutSeconds), Transport = httpClient is null ? null : new HttpClientPipelineTransport(httpClient) }).GetChatClient(s.Model).AsIChatClient()`
- DI：`ServiceCollectionExtensions.AddApplicationServices` 內加 `services.AddScoped<AgentChatClientFactory>();`

- [ ] **Step 1: 寫失敗測試**
  - `CreateBaseUri_Azure_WithOrWithoutSuffix_ShouldNormalize`：`[Theory]` 輸入 `https://x.openai.azure.com`、`https://x.openai.azure.com/`、`https://x.openai.azure.com/openai/v1`、`https://x.openai.azure.com/openai/v1/` → 皆等於 `https://x.openai.azure.com/openai/v1/`。
  - `CreateBaseUri_OpenAi_EmptyEndpoint_ShouldUseDefault` → `https://api.openai.com/v1/`。
  - `CreateOpenAiChatClient_Azure_ShouldPostToV1ChatCompletions`：用 `StubHttpMessageHandler.Json(OK, <chat completion JSON>)` 建 `HttpClient`，呼叫 `GetResponseAsync("hi")`；斷言 `handler.Requests[0].RequestUri == https://unit-test.openai.azure.com/openai/v1/chat/completions`、body 含 `"model":"my-deployment"`、回應 `Text == "pong"`。
  - `Create_WhenSettingsIncomplete_ShouldThrow`：`ApiKey=""` → `InvalidOperationException`。
- [ ] **Step 2: 執行確認失敗**（`--filter FullyQualifiedName~AgentChatClientFactoryTests`，預期編譯失敗）
- [ ] **Step 3: 加套件、實作上列成員**
- [ ] **Step 4: 執行 `AgentChatClientFactoryTests` 與既有 `AiChatEndpointTests` 全過**
- [ ] **Step 5: 檢查點**（`dotnet build -c Release` 無警告；`dotnet list package --vulnerable --include-transitive` 無弱點，若有依 `Directory.Packages.props` 既有註解方式直接參考修補版）

### Task 2: `AuditingChatClient` 與 MAF 迴圈逐次記帳

**Files:**
- Create: `CustomerHelpAgent.Web/Agents/AuditingChatClient.cs`、`CustomerHelpAgent.Web/Agents/AiQuotaExceededException.cs`
- Modify: `CustomerHelpAgent.Web/Agents/AgentChatClientFactory.cs`（`Create` 回傳 `new AuditingChatClient(inner, …)`）
- Test: `CustomerHelpAgent.Tests/AuditingChatClientTests.cs`（重用 `RecordingTokenUsageRecorder`、`FakeAiCallLogRecorder`、`StubAiQuotaService`、`RecordingAuditLogService`、`StaticOptionsMonitor<T>`；自寫 `ScriptedChatClient : IChatClient` 依序回傳預設 `ChatResponse`）

**Interfaces:**
- Consumes: Task 1 的 `AgentChatClientFactory.InnerClientFactory`、`CreateBaseUri`；既有 `AiCallCapture`（internal）、`TokenUsageEntry`、`AiCallLogEntry`、`AiQuotaService.BlockedMessage`、`AiQuotaService.Money`、`AuditActions.Ai.QuotaBlocked`、`SlowOperationSettings.IsSlow`。
- Produces:
  - `internal sealed class AuditingChatClient : DelegatingChatClient`，覆寫 `GetResponseAsync` 與 `GetStreamingResponseAsync`。
  - `public sealed class AiQuotaExceededException(string message) : Exception(message)`。
- 行為（對照 `AiChatCompletionClient.CompleteAsync`，逐條相同）：
  1. 額度 `CheckAsync(callerId)` 擋下 → 寫 `AuditActions.Ai.QuotaBlocked`（detail 格式與既有相同）→ 拋 `AiQuotaExceededException(AiQuotaService.BlockedMessage(reached))`，不記用量。
  2. `AiCallCapture(operation, provider, settings.Model, account, userId, CreateBaseUri(settings), requestBody, relatedInfo: null, conversationId)`；`requestBody = JsonSerializer.Serialize(new { messages, tools = options?.Tools?.Select(t => t.Name) }, AIJsonUtilities.DefaultOptions)`；呼叫前 `MarkSent()`。
  3. 成功：`HttpStatus=200`、`ResponseText=response.Text`、`FinishReason=response.FinishReason?.Value`、`ResponseModel=response.ModelId`、`ResponseBody=Serialize(response.Messages)`；用量由 `response.Usage` 映射（`InputTokenCount→InputCount`、`OutputTokenCount→OutputCount`、`TotalTokenCount→TotalCount`、`CachedInputTokenCount→CachedInputCount`、`ReasoningTokenCount→ReasoningCount`），`RawUsageJson=Serialize(response.Usage)`，`CallKind=TokenUsageCallKinds.Chat`、`CallId=capture.CallId`。
  4. 例外：呼叫端取消 → `SetOutcome(false,"Canceled")`、不記用量、重拋；非取消的 `TaskCanceledException` → 用量 `Timeout`、重拋；`System.ClientModel.ClientResultException` → `HttpStatus=ex.Status`，401/403→`Unauthorized`、429→`RateLimited`、其他→`UpstreamError`，記用量後重拋；其他 → `Unexpected`、重拋。
  5. `finally`：`AiCallCapture.RecordSafelyAsync(recorder, capture, elapsed, logger)`；慢呼叫用既有英文樣板 `"Slow AI chat completion. Operation={Operation}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}"`。
  6. 串流：收集全部 `ChatResponseUpdate` 後 `updates.ToChatResponse()`，再走第 3 步。

- [ ] **Step 1: 寫失敗測試**
  - `GetResponseAsync_Success_ShouldRecordUsageAndCallLog`：Scripted 回 `Text="ok"`、`Usage{Input=11,Output=7,Total=18}`、`ModelId="gpt-x"` → `Entries.Single()`：`InputCount==11`、`Success`、`Operation=="測試"`、`UserId==7`；CallLog `ConversationId==convId`、`ResponseText=="ok"`、兩者 `CallId` 相同。
  - `GetResponseAsync_QuotaBlocked_ShouldThrowAndAuditWithoutCalling`：`StubAiQuotaService.Block = new AiQuotaLimitStatus(User, Daily, 50, 51.25, …)` → 拋 `AiQuotaExceededException`、Scripted 呼叫 0 次、稽核 detail `"operation=測試; limit=50; used=51.25"`、用量 0 筆。
  - `GetResponseAsync_UpstreamFailure_ShouldRecordFailureAndRethrow`：Scripted 拋 `ClientResultException` 狀態 429 → 重拋、用量 `FailureReason=="RateLimited"`、CallLog `HttpStatus==429`。
  - `GetResponseAsync_CallerCanceled_ShouldNotRecordUsage`。
  - `GetStreamingResponseAsync_ShouldRecordOnceAfterStreamCompletes`：三段 update 合計文字 `"abc"` → 1 筆用量、CallLog `ResponseText=="abc"`。
  - `ChatClientAgent_WithTool_ShouldRecordEachModelCallUnderOneConversation`：Scripted 第一回 `FunctionCallContent("echo", {"text":"x"})`、第二回文字 `"done"`；`new ChatClientAgent(factory.Create("測試", convId), instructions: "test", tools: [AIFunctionFactory.Create((string text) => text, "echo")])`；`await agent.RunAsync("hi")` → 用量 2 筆、CallLog 2 筆且 `ConversationId` 皆為 `convId`、最終回覆 `"done"`。
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**（若 MAF 1.25 的 `ChatClientAgent` 建構子簽章與上式不同，依 IntelliSense 調整呼叫，測試語意不變）
- [ ] **Step 4: 執行 `AuditingChatClientTests`、`AiChatCompletionClientTests`、`AiQuotaTests`、`LoggingConventionTests` 全過**
- [ ] **Step 5: 實機冒煙（使用者有金鑰時）**：在 `AuditingChatClientTests` 加 `LiveSmoke_WhenEnvConfigured_ShouldGetReply`，讀環境變數 `CHA_LIVE_AI_PROVIDER/ENDPOINT/KEY/MODEL`，未設定直接 `return`；設定後呼叫一次並斷言回覆非空。Azure 若回 401，改以 `OpenAIClientOptions.AddPolicy(...)` 加 `api-key` 標頭並重測。
- [ ] **Step 6: M0 收尾**：版本 `1.0.1 → 1.0.2`；新增 `docs/features/售後Agent示範系統.md`（含 BOM；寫「已實作：M0 計費接軌」與 `AgentChatClientFactory` 用法），更新 `docs/features/README.md`、`docs/README.md` 索引、`docs/architecture/開發慣例與限制速查.md` §6.7 補一句「Agent 呼叫一律經 `AgentChatClientFactory.Create`」、設計文件與兩份企劃書的里程碑狀態；跑品質關卡；**檢查點**。

---

## M1 — 模擬業務系統＋傳統流程

### Task 3: 列舉、實體、migration

**Files:**
- Create: `CustomerHelpAgent.Share/Shop/ShopEnums.cs`、`CustomerHelpAgent.AccessDatas/Models/Shop/*.cs`（10 個實體）
- Modify: `CustomerHelpAgent.AccessDatas/BackendDBContext.cs`；Create migration `AddShopDomain`
- Test: `CustomerHelpAgent.Tests/ShopSchemaTests.cs`

**Interfaces（Produces，命名空間 `CustomerHelpAgent.Share.Shop`／`CustomerHelpAgent.AccessDatas.Models`）：**
- `enum AfterSalesType { SizeExchange = 1, ColorExchange = 2, ReturnRefund = 3, QualityIssue = 4, ShipmentUrge = 5 }`
- `enum AfterSalesStatus { Submitted = 1, NeedMoreInfo = 2, Approved = 3, Rejected = 4 }`
- `enum AfterSalesChannel { Traditional = 1, Agent = 2, AgentHandoff = 3 }`
- `enum ProcessActor { Customer = 1, Staff = 2 }`
- `static class ProductCategories { Shoes = "鞋類"; Apparel = "服飾"; All = "全部"; }`
- 實體（int `Id`，時間欄位 `DateTime`）：
  - `Product { Code, Name, Category, Price(decimal) }`（`Code` unique）
  - `ProductVariant { ProductId, Color, Size, Stock }`（unique `ProductId+Color+Size`）
  - `SalesOrder { OrderNo, CustomerUserId(→MyUser), OrderedAt, DeliveredAt?, RecipientName, ShippingAddress, RecipientPhone }`（`OrderNo` unique）
  - `SalesOrderItem { SalesOrderId, ProductVariantId, Quantity, UnitPrice }`
  - `Shipment { SalesOrderId, Carrier, TrackingNo, StatusText, ShippedAt? }`、`ShipmentEvent { ShipmentId, OccurredAt, Location, Description }`
  - `AfterSalesPolicy { Code, Type(AfterSalesType), Category, Title, Content, Source, Version, EffectiveFrom, EffectiveTo?, WindowDays?, RequiresUnworn, RequiresPhoto, CreatedAt, CreatedBy? }`（unique `Code+Version`）
  - `AfterSalesRequest { RequestNo, CaseKey(Guid), CustomerUserId, SalesOrderItemId, Type, Status, Channel, TargetSize?, TargetColor?, IsWorn?, Description, StaffNote?, AppliedPolicyId?, CreatedAt, UpdatedAt, DecidedAt?, DecidedByUserId? }`（`RequestNo` unique，格式 `AS{yyyyMMdd}{Id:D4}`）
  - `AfterSalesAttachment { AfterSalesRequestId, OriginalFileName, StoredFileName, RelativePath, ContentType, FileSize, CreatedAt }`
  - `ProcessMetric { CaseKey(Guid), Channel, Actor, Action(string ≤64), UserId?, AfterSalesRequestId?, OccurredAt }`（index `CaseKey`）
- **刻意決定**：這些實體**不**實作 `ISoftDeletable`／`IConcurrencyStamped`——規則以「新增版本」取代就地編輯，申請以狀態條件更新（Task 7）防衝突，皆無刪除畫面；因此不需登記 `SoftDeleteUserRoleTests`／`OptimisticConcurrencyTests` 清單。

- [ ] **Step 1: 寫失敗測試** `FreshSchema_ShouldEnforceShopUniqueIndexes`：in-memory SQLite `EnsureCreatedAsync`，插入兩筆相同 `Product.Code` → `DbUpdateException`；兩筆相同 `AfterSalesPolicy(Code,Version)` → `DbUpdateException`。
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作實體與 `OnModelCreating`**，產生 migration：`dotnet ef migrations add AddShopDomain --project CustomerHelpAgent.AccessDatas --startup-project CustomerHelpAgent.Web`
- [ ] **Step 4: 執行 `ShopSchemaTests`、`DatabaseInitializerTests` 全過**
- [ ] **Step 5: 檢查點**

### Task 4: 權限鍵、示範設定、示範帳號與資料種子

**Files:**
- Modify: `CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs`、`CustomerHelpAgent.Business/Services/Other/RolePermissionService.cs`、`CustomerHelpAgent.Business/Helpers/AuditActions.cs`、`CustomerHelpAgent.Web/Extensions/ServiceCollectionExtensions.cs`、`appsettings.json`、`appsettings.Production.json`、`CustomerHelpAgent.Tests/PasswordPolicyTests.cs`（允許清單加 `DemoAccountSeeder.cs`）
- Create: `CustomerHelpAgent.Models/Systems/DemoSettings.cs`、`CustomerHelpAgent.Web/Configuration/Validation/DemoSettingsValidator.cs`；`CustomerHelpAgent.Business/Startup/DemoSeedData.cs`、`DemoAccountSeeder.cs`、`DemoCatalogSeeder.cs`
- Test: `CustomerHelpAgent.Tests/DemoSeederTests.cs`

**Interfaces（Produces）：**
- `MagicObjectHelper`：`角色_顧客商店 = "顧客商店"`、`角色_售後服務 = "售後服務"`（群組）、`角色_售後審核 = "售後審核"`、`角色_售後規則 = "售後規則管理"`、`顧客角色 = "顧客"`、`客服角色 = "客服人員"`、`店家角色 = "店家管理者"`。
- `RolePermissionService.GetRoleListPermissionAllName()` 於 `角色_登出` 群組前加入 `[角色_售後服務, 角色_售後審核, 角色_售後規則]` 與 `[角色_顧客商店]`。
- `AuditActions.Shop`：`RequestSubmit = "Shop.Request.Submit"`、`RequestResubmit = "Shop.Request.Resubmit"`、`RequestMoreInfo = "Shop.Request.MoreInfo"`、`RequestDecide = "Shop.Request.Decide"`、`PolicyAddVersion = "Shop.Policy.AddVersion"`。
- `DemoSettings { const SectionName = "DemoSettings"; bool Enabled; string DemoUserPassword = ""; }`；Validator：`Enabled` 時密碼長度 ≥ 8。`AddValidatedOptions<DemoSettings, DemoSettingsValidator>`。設定值：`appsettings.json` → `{ "Enabled": true, "DemoUserPassword": "demo1234" }`；`appsettings.Production.json` → `{ "Enabled": false, "DemoUserPassword": "" }`。
- `DemoSeedData`（static）：帳號、商品、訂單、規則定義（相對時間以 `now` 參數計算）。內容：
  | 帳號 | 名稱 | 角色 |
  |---|---|---|
  | `customer1` | 王大同 | 顧客 |
  | `customer2` | 陳怡君 | 顧客 |
  | `agent1` | 客服小李 | 客服人員（`售後服務、售後審核、首頁、登出`） |
  | `merchant1` | 店長阿明 | 店家管理者（再加 `售後規則管理`） |
  顧客角色鍵：`顧客商店、登出`。
  商品：`SH-RUN01 城市慢跑鞋`（鞋類，NT$2,680；黑／白 × 38–44，黑 41 庫存 5、白 42 庫存 0，其餘 3）、`AP-JKT01 輕量防風外套`（服飾，NT$1,980；藏青／灰 × S–XL，各 4）。
  訂單（customer1）：黑 40（`DeliveredAt = now-3d`）、白 41（`now-2d`）——旗艦劇本；（customer2）：藏青 M 外套（`now-10d`，已過 7 天）、灰 L 外套（已出貨未送達，`ShippedAt = now-4d`，3 筆物流事件）。收件地址與手機填虛構值。
  規則（`Source = "售後服務說明（2026 年版）"`、`Version = 1`、`EffectiveFrom = now-90d`）：
  | Code | Type | Category | WindowDays | RequiresUnworn | RequiresPhoto | Content |
  |---|---|---|---|---|---|---|
  | `SIZE-EXCHANGE` | SizeExchange | 全部 | 7 | true | false | 簽收 7 天內、商品未穿過（未下水、吊牌完整），可申請換同款其他尺碼，一次為限。 |
  | `COLOR-EXCHANGE` | ColorExchange | 全部 | 7 | true | false | 簽收 7 天內、商品未穿過，可申請換同款其他顏色，一次為限。 |
  | `RETURN-REFUND` | ReturnRefund | 全部 | 7 | true | false | 簽收 7 天內可申請退貨退款，商品需保持全新、包裝完整；退款於收到退貨後 5 個工作天內退回原付款方式。 |
  | `QUALITY-ISSUE` | QualityIssue | 全部 | 180 | false | true | 商品出現開膠、脫線等品質問題，簽收 180 天內可申請，需先上傳照片供客服核驗，核驗通過後可換新或退款。 |
  | `SHIPMENT-URGE` | ShipmentUrge | 全部 | null | false | false | 訂單出貨後超過 3 天未送達可申請催件，客服將於 1 個工作天內回覆物流進度。 |
- `DemoAccountSeeder : IDatabaseSeeder`（`Order = 50`）：`DemoSettings.Enabled == false` 直接返回；角色依名稱找不到才建（`TabViewJson` = 角色鍵），並以 `IRbacWriteService.SyncRolePermissionsAsync(context, roleId, keys)`；帳號依 `Account` 找不到才建（`SecurePasswordHasher.HashPassword(DemoUserPassword)`、`Status = true`、`RoleViewId`），再 `SyncUserRolesAsync`。
- `DemoCatalogSeeder : IDatabaseSeeder`（`Order = 60`）：`Enabled` 且 `!Product.Any()` 才寫入；注入 `TimeProvider` 取 `now`。
- 兩者 `AddScoped<IDatabaseSeeder, …>()` 於既有 seeder 註冊處。

- [ ] **Step 1: 寫失敗測試**（in-memory SQLite＋真實 `RbacWriteService`；參考 `DatabaseInitializerTests` 的建構方式）
  - `DemoSeeders_WhenEnabled_ShouldCreateAccountsWithPermissions`：跑 10/20/30/50/60 seeder → `customer1` 經 `PermissionChecker.GetEffectivePermissionKeysAsync` 含 `顧客商店`、不含 `首頁`；`agent1` 含 `售後審核`、不含 `售後規則管理`；`merchant1` 含 `售後規則管理`。
  - `DemoCatalogSeeder_ShouldSeedFlagshipOrders`：`customer1` 有 2 筆訂單，黑 40 與白 41 的 `DeliveredAt` 分別為 `now-3d`、`now-2d`（`ManualTimeProvider(2026-10-23T02:00Z)`）；5 條規則皆 `Version == 1`。
  - `DemoSeeders_RunTwice_ShouldNotDuplicate`。
  - `DemoSeeders_WhenDisabled_ShouldDoNothing`。
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**；`docs/guides/腳手架開發指引.md` 與 `docs/operations/日誌與設定檔說明.md` 加 `DemoSettings` 說明（`DocumentationConventionTests` 要求）
- [ ] **Step 4: 執行 `DemoSeederTests`、`DatabaseInitializerTests`、`DatabaseInitializerConventionTests`、`PasswordPolicyTests`、`AdminOnlyPermissionTests`、`DocumentationConventionTests`、`OptionsValidationTests`、`AuditConventionTests` 全過**
- [ ] **Step 5: 檢查點**

### Task 5: 售後規則服務與資格判定

**Files:**
- Create: `CustomerHelpAgent.Business/Helpers/AfterSalesEligibility.cs`、`CustomerHelpAgent.Business/Services/DataAccess/AfterSalesPolicyService.cs`、`CustomerHelpAgent.Models/Shop/AfterSalesPolicyModels.cs`
- Modify: DI、AutoMapping（若用映射）、`DataAccessServiceLifetimeTests`、`ApiIntegrationTests.DataAccessServices_ShouldResolveFromContainer`
- Test: `CustomerHelpAgent.Tests/AfterSalesPolicyServiceTests.cs`、`AfterSalesEligibilityTests.cs`

**Interfaces（Produces）：**
- `AfterSalesPolicyModel`（`Id, Code, Type, Category, Title, Content, Source, Version, EffectiveFrom, EffectiveTo, WindowDays, RequiresUnworn, RequiresPhoto`；`Clone()`，登記 `AdapterModelCloneTests`）
- `enum PolicyLookupStatus { Found, NoPolicy, Conflict }`；`sealed record PolicyLookupResult(PolicyLookupStatus Status, IReadOnlyList<AfterSalesPolicyModel> Policies)`
- `AfterSalesPolicyService(IDbContextFactory<BackendDBContext>, IAuditLogService, CurrentUserService, TimeProvider, ILogger<AfterSalesPolicyService>)`：
  - `Task<PolicyLookupResult> GetEffectiveAsync(AfterSalesType type, string productCategory, DateTime at)`：`Category ∈ {productCategory, 全部}` 且 `EffectiveFrom ≤ at < (EffectiveTo ?? ∞)`；0 筆 `NoPolicy`、1 筆 `Found`、≥2 筆 `Conflict`。
  - `Task<List<AfterSalesPolicyModel>> GetVersionsAsync(bool includeExpired)`（依 `Code, Version desc` 排序）
  - `Task<VerifyRecordResult> AddVersionAsync(AfterSalesPolicyModel draft)`：同 `Code` 現行版本 `EffectiveTo = now`，新列 `Version = max+1`、`EffectiveFrom = now`；稽核 `AuditActions.Shop.PolicyAddVersion`。
- `static class AfterSalesEligibility`：
  - `int? DaysSinceDelivery(DateTime? deliveredAt, DateTime now)`（以本地日期差，未簽收回 null）
  - `EligibilityResult Evaluate(AfterSalesPolicyModel policy, DateTime? deliveredAt, bool? isWorn, bool hasPhoto, DateTime now)`；`sealed record EligibilityResult(bool IsEligible, IReadOnlyList<string> UnmetReasons, IReadOnlyList<string> MissingInfo)`——未簽收／超過 `WindowDays` 進 `UnmetReasons`；`RequiresUnworn && isWorn == true` 進 `UnmetReasons`；`RequiresUnworn && isWorn == null` 進 `MissingInfo("是否穿過")`；`RequiresPhoto && !hasPhoto` 進 `MissingInfo("照片")`。

- [ ] **Step 1: 寫失敗測試**
  - `Evaluate_Day3Unworn_SizeExchange_ShouldBeEligible`；`Evaluate_Day10_ShouldFailWindow`（customer2 外套情境）；`Evaluate_WornUnknown_ShouldAskWhetherWorn`；`Evaluate_QualityWithoutPhoto_ShouldAskPhoto`；`DaysSinceDelivery_CrossMidnight_ShouldUseLocalDate`。
  - `GetEffectiveAsync_ShouldReturnFoundNoPolicyConflict`：種入兩條同類同品類規則 → `Conflict`；查無 → `NoPolicy`。
  - `AddVersionAsync_ShouldCloseOldVersionAndKeepItQueryable`：v1 `EffectiveTo == now`、`GetEffectiveAsync` 回 v2、`GetVersionsAsync(true)` 仍含 v1。
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**
- [ ] **Step 4: 執行上述測試＋`DataAccessServiceLifetimeTests`、`AdapterModelCloneTests` 全過**
- [ ] **Step 5: 檢查點**

### Task 6: 顧客訂單服務

**Files:**
- Create: `CustomerHelpAgent.Business/Services/DataAccess/ShopOrderService.cs`、`CustomerHelpAgent.Models/Shop/ShopOrderModels.cs`
- Modify: DI、`DataAccessServiceLifetimeTests`、`ApiIntegrationTests`
- Test: `CustomerHelpAgent.Tests/ShopOrderServiceTests.cs`

**Interfaces（Produces）：**
- `ShopOrderSummary(int OrderId, string OrderNo, DateTime OrderedAt, DateTime? DeliveredAt, string StatusText, IReadOnlyList<ShopOrderItemView> Items)`
- `ShopOrderItemView(int OrderItemId, string ProductName, string ProductCategory, string Color, string Size, int Quantity, decimal UnitPrice)`
- `ShopOrderDetail(ShopOrderSummary Summary, string RecipientName, string ShippingAddress, ShopShipmentView? Shipment)`；`ShopShipmentView(string Carrier, string TrackingNo, string StatusText, DateTime? ShippedAt, IReadOnlyList<ShopShipmentEventView> Events)`；`ShopShipmentEventView(DateTime OccurredAt, string Location, string Description)`
- `ShopOrderService(IDbContextFactory<BackendDBContext>, ILogger<ShopOrderService>)`：
  - `Task<IReadOnlyList<ShopOrderSummary>> GetMyOrdersAsync(int customerUserId)`（`OrderedAt desc, Id desc`）
  - `Task<ShopOrderDetail?> GetMyOrderDetailAsync(int customerUserId, int orderId)`（非本人 → `null`）
  - `Task<ShopOrderItemView?> GetMyOrderItemAsync(int customerUserId, int orderItemId)`（Task 7、M2 用）
  - `StatusText`：`DeliveredAt` 有值 → `已簽收`；有 `ShippedAt` → `配送中`；否則 `處理中`。
  - 手機 `RecipientPhone` **不出現在任何回傳模型**。

- [ ] **Step 1: 寫失敗測試**：`GetMyOrders_ShouldOnlyReturnOwnOrders`；`GetMyOrderDetail_OtherCustomer_ShouldReturnNull`；`GetMyOrderDetail_ShouldIncludeShipmentEventsInTimeOrder`；`ShopOrderModels_ShouldNotExposePhone`（反射檢查所有 `Shop*` 模型屬性名不含 `Phone`）。
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `DataAccessServiceLifetimeTests`）
- [ ] **Step 5: 檢查點**

### Task 7: 售後申請、附件、埋點服務

**Files:**
- Create: `CustomerHelpAgent.Business/Services/DataAccess/AfterSalesRequestService.cs`、`ProcessMetricService.cs`、`CustomerHelpAgent.Business/Services/Other/AfterSalesFileStore.cs`、`CustomerHelpAgent.Models/Shop/AfterSalesRequestModels.cs`、`CustomerHelpAgent.Models/Shop/ProcessMetricActions.cs`
- Modify: `CustomerHelpAgent.Models/Systems/SystemSettings.cs`（`ExternalFileSystem.AfterSalesFilePath`）、`appsettings.json`（`"AfterSalesFilePath": "C:\\temp\\CustomerHelpAgent\\AfterSalesFile"`）、DI、`DataAccessServiceLifetimeTests`、`ApiIntegrationTests`、`docs/operations/日誌與設定檔說明.md`
- Test: `CustomerHelpAgent.Tests/AfterSalesRequestServiceTests.cs`、`ProcessMetricServiceTests.cs`

**Interfaces（Produces）：**
- `SubmitAfterSalesInput { Guid CaseKey; int OrderItemId; AfterSalesType Type; string? TargetSize; string? TargetColor; bool? IsWorn; string Description; AfterSalesChannel Channel = Traditional; }`
- `AfterSalesUploadFile { string FileName; long FileSize; Stream Content; }`
- `AfterSalesRequestView`（列表／明細共用：`Id, RequestNo, CaseKey, Type, Status, Channel, OrderNo, ProductName, ProductCategory, Color, Size, TargetSize, TargetColor, IsWorn, Description, StaffNote, DeliveredAt, AppliedPolicyId, CreatedAt, UpdatedAt, CustomerName, IReadOnlyList<AfterSalesAttachmentView> Attachments`）；`AfterSalesAttachmentView(int Id, string OriginalFileName, long FileSize)`
- `sealed record SubmitResult(bool Success, string Message, int? RequestId, string? RequestNo)`
- Consumes: Task 5 `AfterSalesPolicyService.GetEffectiveAsync`、Task 6 `ShopOrderService.GetMyOrderItemAsync`（擁有權與品類）。
- `AfterSalesRequestService(IDbContextFactory<BackendDBContext>, AfterSalesPolicyService, ShopOrderService, AfterSalesFileStore, IAuditLogService, CurrentUserService, TimeProvider, ILogger<AfterSalesRequestService>)`（客服動作的操作者取 `CurrentUserService.CurrentUser.Id`）：
  - `Task<SubmitResult> SubmitAsync(int customerUserId, SubmitAfterSalesInput input, IReadOnlyList<AfterSalesUploadFile> files)`：驗證明細屬本人（否則「找不到這筆訂單」）；`SizeExchange` 需 `TargetSize`、`ColorExchange` 需 `TargetColor`；附件只允許 `jpg/jpeg/png/webp`、單檔 ≤ 10 MB、最多 3 張；先寫檔再存 DB，DB 失敗刪除剛寫的檔；`AppliedPolicyId` = 送件當下 `GetEffectiveAsync` 為 `Found` 的那條（否則 null）；`Status = Submitted`；稽核 `RequestSubmit`。
  - `Task<IReadOnlyList<AfterSalesRequestView>> GetMyRequestsAsync(int customerUserId)`
  - `Task<SubmitResult> ResubmitAsync(int customerUserId, int requestId, bool? isWorn, string supplement, IReadOnlyList<AfterSalesUploadFile> files)`：僅 `NeedMoreInfo` 且本人；`Description` 追加 `\n［補充］{supplement}`；狀態回 `Submitted`。
  - `Task<DataRequestResult<AfterSalesRequestView>> GetReviewQueueAsync(DataRequest request, AfterSalesStatus? status)`（`CreatedAt asc, Id asc`）
  - `Task<AfterSalesRequestView?> GetReviewDetailAsync(int requestId)`
  - `Task<VerifyRecordResult> RequestMoreInfoAsync(int requestId, string note)`、`Task<VerifyRecordResult> DecideAsync(int requestId, bool approve, string? note)`：以 `ExecuteUpdateAsync(... WHERE Id == id && Status == Submitted)` 更新，影響 0 列回 `Build(false, "這筆申請的狀態已經變更，請重新整理後再操作。")`；記 `DecidedAt/DecidedByUserId`；稽核。
  - `Task<(string FullPath, string ContentType, string FileName)?> GetAttachmentForAsync(int attachmentId, int userId, bool canReview)`：非本人且非審核者 → null。
- `AfterSalesFileStore(IOptions<SystemSettings>, ILogger<AfterSalesFileStore>)`：`Task<string> SaveAsync(Stream content, string extension, DateTime now)` 回相對路徑 `yyyy/MM/{guid:N}{ext}`；`string? TryResolveFullPath(string? relativePath)`（逸出根目錄回 null）；`bool Delete(string? relativePath)`。
- `ProcessMetricService(IDbContextFactory<BackendDBContext>, TimeProvider, ILogger<ProcessMetricService>)`：`Task RecordAsync(Guid caseKey, AfterSalesChannel channel, ProcessActor actor, string action, int? userId, int? requestId = null)`（不拋例外，失敗記 Warning）。
- `ProcessMetricActions` 常數：顧客 `OpenForm, SelectOrderItem, SelectType, FillDetail, UploadPhoto, Submit, ViewRequests, Resubmit, ViewShipment, UrgeShipment`；客服 `OpenRequest, ViewOrder, ViewPolicy, RequestMoreInfo, Approve, Reject`。

- [ ] **Step 1: 寫失敗測試**（`AfterSalesFilePath` 指向測試暫存資料夾，結束刪除）
  - `SubmitAsync_OtherCustomersItem_ShouldFail`
  - `SubmitAsync_SizeExchangeWithoutTarget_ShouldFail`
  - `SubmitAsync_WithDisallowedFile_ShouldFailWithoutWritingFile`（`.exe`、11 MB 各一）→ 暫存資料夾仍為空
  - `SubmitAsync_Valid_ShouldStoreAppliedPolicyAndRequestNo`（`RequestNo` 符合 `^AS\d{8}\d{4}$`、`AppliedPolicyId` = `SIZE-EXCHANGE` v1）
  - `RequestMoreInfo_ThenResubmit_ShouldReturnToSubmittedWithSupplement`
  - `DecideAsync_WhenStatusChanged_ShouldFail`（先 `RequestMoreInfoAsync` 再 `DecideAsync` → `Success == false`）
  - `GetAttachmentFor_OtherCustomer_ShouldReturnNull`
  - `ProcessMetricService_Record_ShouldPersistWithCaseKey`
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `LoggingConventionTests`、`AuditConventionTests`、`DataAccessServiceLifetimeTests`）
- [ ] **Step 5: 檢查點**

### Task 8: 商店版面、顧客登入導向、我的訂單與物流

**Files:**
- Create: `CustomerHelpAgent.Web/Components/Layout/ShopLayout.razor(.cs/.css)`；`Components/Pages/Shop/ShopOrdersPage.razor`（`@page "/shop"`）、`ShopOrderDetailPage.razor`（`@page "/shop/orders/{OrderId:int}"`）；`Components/Views/Shop/ShopOrdersView.razor(.cs)`、`ShopOrderDetailView.razor(.cs)`
- Modify: `wwwroot/theme.css`（`--shop-*` 別名）、`Components/Views/<HomeWelcomeView 所在資料夾>/HomeWelcomeView.razor.cs`、`CustomerHelpAgent.Tests/PageHelpCatalogTests.cs`（`RoutesWithoutHelp`）
- Test: `CustomerHelpAgent.Tests/Components/ShopLayoutTests.cs`、既有 `PageAuthorizationTests` 自動涵蓋 `/shop`

**要點：**
- `ShopLayout`：與 `MainLayout` 相同的 `isAuthenticated` 閘門（`AuthenticationStateHelper.Check` 完成前不渲染）；頂部列＝店名「示範鞋服商店」、導覽「我的訂單（/shop）」「售後申請（/shop/after-sales）」、使用者名稱、登出（經 `LogoutConfirm.RequestAsync`）；**不含**側欄、`OverlayStyles`、操作說明。展示者若具後台權限（`CheckAccessPage(角色_首頁)`）另顯示「回到後台（/App）」。
- 顧客頁 `@layout ShopLayout`，View 依既有 `isAccessChecked` 樣式並 `CheckAccessPage(MagicObjectHelper.角色_顧客商店)`。
- `HomeWelcomeView`：既有「無首頁權限」分支前，若 `CheckAccessPage(角色_顧客商店)` 成立 → `NavigationManager.NavigateTo("/shop")` 並 return。
- 訂單頁：卡片列出訂單與商品（名稱、顏色、尺碼、簽收日），每個商品有「申請售後」連到 `/shop/after-sales/new?orderItemId={id}`；明細頁顯示物流時間軸，未簽收且已出貨超過 3 天時顯示「申請催件」（建立 `ShipmentUrge` 申請）。進頁、看物流、催件皆 `ProcessMetricService.RecordAsync`（`CaseKey` 由頁面 `Guid.NewGuid()`，催件時沿用）。
- `RoutesWithoutHelp` 加 `/shop`、`/shop/orders/{OrderId:int}`（理由：「顧客商店版面沒有頂欄操作說明按鈕」）。

- [ ] **Step 1: 寫失敗測試** `ShopLayout_BeforeAuthCheck_ShouldRenderNothing`、`ShopLayout_ShouldNotLinkDirectlyToLogout`（bUnit，比照 `MainLayout` 測試做法；若 `MainLayout` 無 bUnit 測試則以原始碼掃描斷言 `ShopLayout.razor` 含 `@if (isAuthenticated)`）
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**
- [ ] **Step 4: 執行 `ShopLayoutTests`、`PageAuthorizationTests`、`PageHelpCatalogTests`、`ThemeConventionTests`、`ButtonIconConventionTests`、`LogoutConfirmationConventionTests` 全過**
- [ ] **Step 5: 手動驗證**：`dotnet run --project CustomerHelpAgent.Web`，以 `customer1 / demo1234` 登入 → 自動到 `/shop` → 看到黑 40、白 41 兩筆；改網址 `/shop/orders/<customer2 的訂單 Id>` → 顯示「找不到這筆訂單」。
- [ ] **Step 6: 檢查點**

### Task 9: 顧客售後申請表單與我的申請

**Files:**
- Create: `Components/Pages/Shop/ShopAfterSalesNewPage.razor`（`@page "/shop/after-sales/new"`，`[SupplyParameterFromQuery] int? OrderItemId`）、`ShopAfterSalesListPage.razor`（`@page "/shop/after-sales"`）；`Components/Views/Shop/ShopAfterSalesFormView.razor(.cs)`、`ShopAfterSalesListView.razor(.cs)`
- Modify: `OverlayStyles.razor`（`.shop-resubmit-modal`）、`PageHelpCatalogTests.RoutesWithoutHelp`
- Test: `CustomerHelpAgent.Tests/Components/ShopAfterSalesFormViewTests.cs`

**要點：**
- 表單步驟（**刻意保留傳統的多步驟**）：①選訂單商品（下拉，預填 `OrderItemId`）②選申請類型（5 種，中文標籤）③依類型填：換尺碼→目標尺碼下拉；換顏色→目標顏色；退貨／換貨→「是否穿過」；品質問題→說明＋上傳照片（`InputFile`，最多 3 張）④問題描述 ⑤送出（`ConfirmDialog.AskAsync`）→ 成功顯示申請編號。每一步變更記一次埋點（`SelectOrderItem/SelectType/FillDetail/UploadPhoto/Submit`）；進頁記 `OpenForm`。
- 表單**不顯示**規則條文與資格判定（傳統做法：顧客自行判斷，由客服審）。
- 我的申請：列表顯示編號、商品、類型、狀態、客服備註；`NeedMoreInfo` 列有「補充資料」→ `form-modal` 形式的 `shop-resubmit-modal`（是否穿過、補充說明、照片）→ `ResubmitAsync`；記 `ViewRequests/Resubmit`。

- [ ] **Step 1: 寫失敗測試** `Form_QualityIssueSelected_ShouldShowPhotoUpload`、`Form_SizeExchangeSelected_ShouldShowTargetSizeOnly`（bUnit；以 `ComponentTestBase` 註冊假服務或 in-memory 真服務）
- [ ] **Step 2–4:** 失敗 → 實作 → 通過（含 `FormModalConventionTests`、`ModalKeyboardConventionTests`、`PageHelpCatalogTests`）
- [ ] **Step 5: 手動驗證**：`customer1` 為黑 40 送「換尺碼 → 41、未穿過」，為白 41 送「品質問題＋照片」，兩筆在「我的申請」顯示 `已提交`。
- [ ] **Step 6: 檢查點**

### Task 10: 後台售後審核、售後規則、照片下載

**Files:**
- Create: `Components/Pages/AfterSales/AfterSalesReviewPage.razor`（`@page "/after-sales-review"`、`@layout MainLayout`）、`AfterSalesPolicyPage.razor`（`@page "/after-sales-policies"`）；`Components/Views/AfterSales/AfterSalesReviewView.razor(.cs/.css)`、`AfterSalesPolicyView.razor(.cs)`；`Web/Controllers/AfterSalesFileController.cs`；`Datas/Help/after-sales-review.md`、`Datas/Help/after-sales-policies.md`（含 BOM、七段格式）
- Modify: `Datas/Menu.json`（新群組 `id 9`「售後服務」icon `support_agent`，子項 `91`「售後審核」icon `fact_check` → `/after-sales-review`、`92`「售後規則管理」icon `rule` → `/after-sales-policies`）、`SidebarMenuService.MenuPermissionMap`（`[9]=角色_售後服務, [91]=角色_售後審核, [92]=角色_售後規則`）、`Datas/HelpTopics.json`、`MenuIconTests.AllowedIcons`、`MenuPermissionConsistencyTests.ViewToMenuId`（`AfterSalesReviewView.razor.cs → 91`、`AfterSalesPolicyView.razor.cs → 92`）、`OverlayStyles.razor`（`.after-sales-review-modal`、`.after-sales-policy-modal`）
- Test: `CustomerHelpAgent.Tests/AfterSalesFileControllerTests.cs`（`[Collection(nameof(IntegrationHostCollection))]`）

**要點：**
- 審核頁：AntDesign `Table`（狀態篩選：已提交／待補件／已核准／已駁回；欄位：編號、顧客、商品、類型、管道、狀態、送件時間）；點列開 `after-sales-review-modal`。**傳統做法刻意分開**：Modal 內預設只顯示申請內容，另有三個按鈕「查看訂單」「查看售後規則」「查看照片」，各自展開對應區塊並記埋點（`ViewOrder/ViewPolicy`）；訂單區顯示簽收日與 `AfterSalesEligibility.DaysSinceDelivery`（事實，不顯示判定）；規則區顯示 `AppliedPolicyId` 那一版的條文、版本與來源。動作：「要求補件」（必填備註）、「核准」「駁回」（`ConfirmDialog`），失敗訊息經 `ViewNotification`。開啟記 `OpenRequest`。
- 規則頁：表格列出所有版本（含已失效，標「已失效」）；`RequirePermission` 包住「新增版本」→ `form-modal` 形式的 `after-sales-policy-modal`（以選取的現行版本預填）→ `AddVersionAsync`。無刪除（故不需 BatchDelete）。
- 下載端點 `GET /api/after-sales-files/{id}/download`：Cookie 驗證（比照 `ProjectFileController`）；`canReview = CheckAccess(角色_售後審核)`；`GetAttachmentForAsync` 為 null → 404；回 `PhysicalFile`。

- [ ] **Step 1: 寫失敗測試**
  - `Download_AsOwner_ShouldReturnFile`、`Download_AsOtherCustomer_ShouldReturn404`、`Download_AsReviewer_ShouldReturnFile`、`Download_Anonymous_ShouldRedirectOrUnauthorized`（依 `ProjectFileController` 既有測試的期望值）
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作頁面、說明檔、選單登記與端點**
- [ ] **Step 4: 執行 `AfterSalesFileControllerTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`FormModalConventionTests`、`PageAuthorizationTests`、`BatchDeleteConventionTests` 全過**
- [ ] **Step 5: 手動驗證（傳統全流程）**：`agent1` 登入 → 售後審核看到 Task 9 兩筆 → 黑鞋按「查看訂單」「查看售後規則」→ 核准；白鞋「查看照片」→「要求補件：請補拍鞋底開膠處」→ `customer1` 補件 → `agent1` 核准；`merchant1` 新增 `SIZE-EXCHANGE` v2（10 天）→ 規則頁 v1 標「已失效」；`agent1` 無「售後規則管理」選單；`ProcessMetric` 表依 `CaseKey` 可串出顧客與客服各步驟。
- [ ] **Step 6: 檢查點**

### Task 11: M1 文件與收尾

**Files:** `appsettings.json`（`1.0.2 → 1.0.3`）、`docs/features/售後Agent示範系統.md`、`docs/prd/售後Agent展示系統-設計.md`、`docs/prd/售後Agent示範系統企劃規劃說明書.md`（第三、五、六章：標示 M1 已完成、展示劇本 5.0／5.2 改為實際帳號與路由）、`docs/prd/售後Agent示範系統企劃書.md`（第三章不變，第八章註明傳統流程可展示）、`docs/prd/README.md`（狀態改「部分實作（M0、M1）」）、`docs/architecture/資料模型與資料庫.md`（新增 Shop 實體一節）、`docs/architecture/Web API 端點目錄.md`（下載端點）

- [ ] **Step 1: 更新上列文件**（UTF-8 含 BOM）
- [ ] **Step 2: 執行 `pwsh ./scripts/Invoke-QualityGate.ps1`**，預期最後一行為全部通過（建置、格式、測試、文件編碼、弱點掃描）
- [ ] **Step 3: 檢查點**：回報結果，建議 commit 訊息「M0–M1：MAF 計費接軌與模擬售後傳統流程；版本 1.0.1 → 1.0.3」，由使用者決定是否 commit。

---

## 執行方式建議

建議 **Subagent-driven**：11 個 Task 之間有明確的介面相依（Task 5–7 的服務被 Task 8–10 頁面與 M2 Agent 共用），每個 Task 交給新的實作者並由獨立審閱者把關，能及早攔下命名或擁有權檢查的錯誤；代價是較多 token。

## 驗證總結

- 自動：各 Task 指定測試＋最後完整品質關卡。
- 手動：Task 8／9／10 的手動步驟串成完整傳統劇本（顧客送件 → 客服查訂單／規則／照片 → 補件往返 → 核准；店家改版規則）。
- M0 實機：使用者提供金鑰時跑 `LiveSmoke_WhenEnvConfigured_ShouldGetReply`。
- 本計畫於核准後另存 `docs/prd/售後Agent展示系統-M0M1實作計畫.md`（含 BOM）並列入 `docs/prd/README.md`。


> 返回 [PRD 主控台](README.md)
