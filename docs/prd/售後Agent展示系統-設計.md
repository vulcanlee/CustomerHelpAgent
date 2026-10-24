# 售後 Agent 展示系統 — 設計文件

- 文件版本：0.7
- 文件狀態：已實作（M0–M6 全部已實作）
- 現行系統版本：1.0.8
- 首次實作版本：1.0.2（M0）
- 最後核對日期：2026/10/24

> 本文件是需求討論後經使用者核准的設計。M0（1.0.2）、M1（1.0.3）、M2（1.0.4）、M3（1.0.5）、M4（1.0.6）、M5（1.0.7）與 M6（1.0.8）已全部實作（見 [售後 Agent 示範系統](../features/售後Agent示範系統.md)；MAF 功能的運用見 [MAF 功能運用說明](../features/售後Agent示範系統-MAF功能運用說明.md)）；實作與本文的差異見 §6 各里程碑的差異註記，已實作的行為以功能文件為準。
> 配套文件：[對外版企劃書](售後Agent示範系統企劃書.md)、[內部版企劃規劃說明書](售後Agent示範系統企劃規劃說明書.md)。
## 緣由（為什麼做）

使用者想用 Microsoft Agent Framework（MAF）在現有 NET10-Blazor-Starter 腳手架上，自建一套「模擬鞋服電商售後」系統，
把文章《Agent 的開發是什麼》的論述做成可操作的實物：挑料與承接、受控工具、執行迴圈、寫入確認關卡、可停可等的出口。
成品要拿給客戶看（老闆／客服主管／IT 都可能在場），讓他們用**同一件售後案件**看見：傳統系統 vs 嵌入 AI Agent 的系統，
顧客體驗、客服工作、流程與成本差在哪、好在哪。所有「外部」業務系統（訂單、規則、庫存、物流、申請）都自建模擬。
使用者明確要求「盡量發揮 MAF 的功能」。

## 1. 目標／非目標／反目標

**目標**
1. 同一套資料上並存「傳統做法」與「Agent 做法」，可並排對照。
2. 顧客端 Agent 完整迴圈：模型選一步 → 程式核驗執行 → 真實結果交回 → 模型再選；在該停、該等的地方停住。
3. 幕後面板（展示時預設開、可關）：白話層為主，可展開原始層（上下文、被刻意不送的欄位、工具請求、核驗、結果、停止原因）。
4. 客服後台也有 AI：Workflow 編排的申請初審＋客服 AI 助手；決定權永遠在人。
5. 故障模擬開關、示範劇本＋一鍵重置、錄製／回放，確保展示不翻車。
6. 顧客端「單一 Agent」與「多 Agent Handoff」可切換對照。
7. 長期記憶（顧客可檢視／刪除），面板區分「記憶／本次對話／資料庫真實狀態」。
8. 效益對照頁：步驟數、耗時、人工介入次數、AI 成本（新台幣）。

**非目標**：真實金流／物流串接、行動 App、多租戶、文章所述「四階升級」的分階模式（使用者只要傳統 vs 完整 Agent）、
AG-UI／A2A／DevUI／Foundry 託管（皆 preview，列為日後選項）。

**反目標（做到功能但仍算失敗）**——使用者四項全勾：
- 現場跑不出預期劇情 → 劇本＋重置＋錄製回放。
- 面板太工程師 → 預設白話層，原始 JSON 需展開。
- 看不出效益 → 量化對照頁。
- Agent 越界（未確認就寫入、查到別人訂單、編造規則）→ 由程式守門並以自動化測試鎖住。

## 2. 使用者已做的決定

| 項目 | 決定 |
|---|---|
| 觀眾 | 業主、客服主管、IT 都可能 |
| 對照方式 | 傳統流程與 Agent 流程都開發，共用同一套資料 |
| 幕後面板 | 展示時可開關，預設開 |
| 故障模擬 | 要，做成開關 |
| 展示階層 | 只要「傳統 vs 完整 Agent」 |
| 客服端 | 也要有 AI 助手 |
| 模型服務 | Azure OpenAI 與 OpenAI 都支援（沿用 `AiSettings.Provider`） |
| 售後類型 | 換尺碼／換顏色、退貨退款、品質問題（開膠，需照片）、物流查詢／催件 |
| 不確定時 | **一律停下來問**（不自行假設往下走） |
| 顧客端架構 | 單一 Agent 與多 Agent Handoff **兩種都做、可切換** |
| 長期記憶 | 要，顧客可檢視／刪除 |
| 回放 | 要做錄製／回放，畫面標示「回放中」 |
| 里程碑順序 | M1→M6 依下文預設順序 |
| 效益數字 | 實測（步驟、耗時、AI 成本）＋後台可設定的假設值（排隊等審核時間、客服每分鐘成本、月案件量），假設值明確標示，可推估月節省 |
| 使用方式 | 幾乎都是使用者一人展示 → 故障開關、重置、模式**全站共用一份** |
| 顧客畫面 | **另做商店風格顧客版面**（會員中心／我的訂單／線上客服）；客服與店家沿用後台版面 |

## 3. Claude 代為決定的事項（已隨設計核准）

- 賣場：鞋類＋服飾電商；全站繁體中文、新台幣。
- 顧客、客服、店家規則管理全在同一個 Blazor 系統，以角色區分；顧客帳號就是 `MyUser`＋「顧客」角色（需登入，Cookie）。
  顧客頁面放 `/shop/*`，使用新的 `ShopLayout`（商店風格、自有導覽，不走側欄 `Menu.json`）；仍以 `CheckAccessPage` 做權限、
  色票一樣取自 `theme.css :root`（可新增 `--shop-*` 別名層）；顧客頁不掛後台「操作說明」，列入 `PageHelpCatalogTests.RoutesWithoutHelp` 並註明理由。
  顧客登入後導向 `/shop`，後台角色導向原首頁。
- 展示控制台提供「展示者快速切換身分」，**僅在 `DemoSettings.Enabled=true` 時存在**，正式環境設定預設關。
- Agent 程式放在 `CustomerHelpAgent.Web/Agents/`（與現有 `Web/Ai/` 同層，依賴 Business 服務），模擬業務服務放 Business。
- 系統提示詞走既有 `PromptTemplate`（可在後台編輯）＋ `AiPromptGuardrails.Compose`。
- 設計文件存放：`docs/prd/售後Agent展示系統-設計.md`（依 docs 分類規則，不另開 `docs/superpowers/`）。
- 回放語意：只回放「模型回應」，工具仍對已重置的劇本資料真實執行；若工具參數與錄製不符，中止回放並明確提示（不偽造結果）。

### 3.1 審閱後補的決定（Claude 的判斷）
- **傳統 vs Agent 以「管道」切分**：傳統表單送出的申請進入純人工審核佇列（無 AI）；Agent 管道的申請進入 M4 初審 Workflow＋客服 AI 助手。
  M1 的人工佇列在 M4 後保留，對照才成立。
- **「不確定即停」由程式強制**：工具回 `timeout`／`forbidden`／`unavailable` → middleware 直接終止 run，顯示程式產生的固定說明（模型不能改寫）；
  回 `not_found`／`no_policy`／`conflict` → 本 run 封鎖寫入工具，模型只能追問或轉人工。規則條文一律以 `get_effective_policy`
  回傳內容渲染成「規則卡」（含版本），不採模型轉述——「編造規則」的測試即檢查顧客看到的條文都來自本 run 的工具結果。
- **長期記憶寫入也要確認**：`remember_preference` 以 `ApprovalRequiredAIFunction` 包裝（「要我記住你平常穿 41 碼嗎？」），納入越界測試。
- **錄製／回放範圍**：在 `IChatClient` 管線層錄製，因此涵蓋所有模型呼叫（單一 Agent、Handoff、Workflow 內 Agent、客服助手）；
  展示模式下每次 run 自動錄製，展示者可把一次標為「金劇本」。回放時對照頁顯示「錄製當時的成本（標註）」。
- **種子資料**：日期相對於重置時間、Id 固定，讓回放參數一致；重置不清除錄製與指標（以 run id 區隔）。
- **套件**：Workflows／Handoff 自 1.0 起為穩定版，直接使用並釘版本；若核對時發現為 preview，也接受並註記。
- 視覺模型可獨立設定（照片初審用），未設定時沿用主模型。

### 3.2 旗艦劇本（取自使用者提供的文章）
- 登入顧客名下兩筆：**黑色運動鞋 40 碼（簽收 3 天）**、**白色運動鞋 41 碼（簽收 2 天）**；收件地址、手機存在訂單但永不送模型。
- 規則（現行售後說明）：簽收 7 天內、未穿過，可換同款其他尺碼；開膠屬品質問題，需先上傳照片給客服核驗。
- 劇本 A（換碼）：「剛買的鞋尺碼不合適，想換大一碼」→ Agent 查訂單得兩筆 → 停下問「黑 40 還是白 41？穿過沒有？」→
  「黑色那雙，還沒穿過」→ 查規則（程式算簽收天數）→ 查庫存 → 確認「黑色從 40 換 41，對嗎？」→ 確認卡 → 程式重核寫入 → 回查 → 回申請編號。
- 劇本 B（一句兩事）：「這雙想換大一碼，另一雙開膠了也幫我看看」→ 查訂單 → 問哪雙換碼、哪雙開膠 →
  「黑的換大一碼，白的開膠」→ 連續查黑鞋換碼規則、白鞋品質規則 → 回覆「黑鞋照換碼繼續辦；白鞋請先補開膠照片」→ 停等上傳（不空轉）。
- 故障劇本：訂單服務逾時（不得說「您沒有這筆訂單」）、權限過期（立即停）、規則服務不可用（明說暫時無法核實）、
  無適用規則（轉人工）、規則衝突、步數用完。
- 另需劇本：退貨退款、物流查詢／催件，以及「店家改規則後 Agent 下次讀到新版本」。

## 4. 系統組成

### 4.1 模擬業務系統（AccessDatas 實體＋Business 服務；M1）
- `Product`／`ProductVariant`（款式、顏色、尺碼、庫存）
- `SalesOrder`／`SalesOrderItem`（簽收時間、收件地址、手機——後兩者**永不送模型**）
- `Shipment`／`ShipmentEvent`（物流軌跡，可模擬延遲異常）
- `AfterSalesPolicy`：類型、適用品類、條文、**來源、版本、生效起訖、狀態**；店家可改版（舊版保留）
- `AfterSalesRequest`：類型、訂單明細、目標規格、狀態機（草稿→已提交→初審中→待人工→核准／駁回→已完成）、
  建立管道（傳統表單／Agent／Agent-Handoff）、確認紀錄 Id、適用規則版本
- `AfterSalesAttachment`：開膠照片（沿用 `UploadFileTypePolicy`、檔案存放模式比照 `ProjectService.SavePhysicalFileAsync`／`ProjectFileStore`）
- 種子：新 `IDatabaseSeeder`（示範顧客、客服、店家帳號＋角色、商品、訂單、規則）；劇本重置走同一份種子定義

### 4.2 傳統流程（M1）
顧客：我的訂單、物流查詢、售後申請表單（選訂單→選類型→填規格／上傳照片）。
客服：售後申請審核佇列（手動查訂單、對照規則、核准／駁回）。
每一步 UI 操作埋點寫入 `ProcessMetric`（供 M6 對照）。

### 4.3 顧客端 Agent（M2）
- **MAF 物件**：`ChatClientAgent`（`IChatClient` 由 OpenAI SDK 建立；Azure 走 v1 `{Endpoint}/openai/v1/`，沿用 `AiChatEndpoint` 的端點／金鑰規則）。
- **計費／日誌接軌**：新 `AuditingChatClient : DelegatingChatClient`，在每次模型呼叫重做 `AiChatCompletionClient.CompleteAsync`
  的步驟（額度 `IAiQuotaService.CheckAsync`、`ITokenUsageRecorder`、`IAiCallLogRecorder`、`SlowOperationSettings.AiCallMs`）；
  一次 Agent run 的多次模型呼叫各自記帳，共用 `ConversationId = session id`；新增 `TokenUsageOperations` 常數。
- **工具**（`AIFunctionFactory` 建立，每個工具宣告五件事：輸入、輸出、失敗說明、誰能呼叫、是否寫入）：
  | 工具 | 性質 | 備註 |
  |---|---|---|
  | `query_my_orders(color?, keyword?)` | 只讀 | 身分由程式從登入 Claims 帶入，**模型不能填使用者 Id**；只回商品名、顏色、原尺碼、訂單編號、可核對狀態 |
  | `get_effective_policy(orderItemId, issueType)` | 只讀 | 先核對訂單歸屬；回條文＋來源＋版本＋生效範圍 |
  | `check_variant_stock(orderItemId, size?, color?)` | 只讀 | 同款其他規格庫存 |
  | `track_shipment(orderId)` | 只讀 | 物流軌跡 |
  | `create_after_sales_request(...)` | **寫入** | `ApprovalRequiredAIFunction` 包裝 |
  | `request_photo_upload(orderItemId)` | 停等 | 讓迴圈停住等顧客上傳，不空轉 |
- **統一工具結果信封** `ToolOutcome{ status: ok|not_found|timeout|forbidden|unavailable|no_policy|conflict, data, message, retryable }`，
  三種「沒拿到訂單」（查無／逾時／權限不符）與「無適用規則／規則服務不可用」用不同 status 交回。
- **寫入關卡**：模型提出 → UI 顯示確認卡（內容由程式依參數重新查出後組成，不採模型文字）→ 顧客確認 →
  程式重核身分、資格（簽收天數由程式算）、確認紀錄 → Business 寫入 → 回查申請確實存在 → 回傳申請編號。
- **迴圈出口（程式辨識，`AgentStopReason`）**：已給建議／等待使用者回覆／等待上傳／步數上限（`MaximumIterationsPerRequest`）／
  工具連續失敗 N 次／權限不符立即停／規則衝突／使用者取消。
- **不確定即停**：提示詞要求＋程式強制（`not_found`／`timeout`／`conflict` 後若模型試圖給結論而未問使用者，面板標紅；`forbidden` 由 middleware 直接終止 run）。
- **挑料**：工具輸出用專屬投影 DTO，每個工具宣告「刻意不送欄位」清單（地址、手機、付款資訊…）供面板顯示。
- **會話承接**：`AgentSession` 序列化存 SQLite（`AgentSessionRecord`），重新整理頁面可接續。
- **串流**：`RunStreamingAsync`，Blazor 以 `InvokeAsync(StateHasChanged)` 更新；模型輸出一律 `AiMarkdownRenderer.ToSafeHtml`。

### 4.4 幕後面板（M2）
- 事件來源：Agent run middleware＋function invocation middleware 發出 `AgentTraceEvent`（in-process，per run），
  同時持久化 `AgentRunTrace`／`AgentRunStep`（供後台追蹤頁與回放）。
- 每個事件兩層：**白話**（例：「Agent 決定先查你的訂單 → 程式核對你的登入身分 → 查到 2 筆，未送出：地址、手機」）與**原始**（訊息、工具參數、結果 JSON）。
- 來源標色：記憶／本次對話／資料庫真實狀態／規則（含版本）。
- 後台「Agent 執行紀錄」頁可回看任一 run。

### 4.5 展示控制台（M3）
故障模擬開關（`FaultSimulationSetting`：訂單服務逾時、權限過期、規則服務不可用、規則衝突、查無訂單、物流異常；
於模擬業務服務層注入，Agent 與傳統流程同受影響）、示範劇本選擇＋一鍵重置、即時／回放模式（`ReplayChatClient : IChatClient`
依序回放錄製的模型回應）、展示者身分切換、面板開關預設值。

### 4.6 客服後台 AI（M4）
- **申請初審 Workflow**（`Microsoft.Agents.AI.Workflows`）：進件 → 併行 fan-out［照片初審 Agent（視覺模型，Structured Output）、
  規則核對（純程式 Executor）、歷史紀錄 Agent］→ fan-in 摘要 Agent → **人工決策節點**（Workflow 的 request/response 等人）→
  執行（純程式：建立換貨單／退款紀錄、調庫存）→ 通知顧客（沿用既有通知機制）。Checkpoint 存 SQLite，重啟可續跑。
  在 UI 以流程圖標示目前節點——對照「固定流程較穩的地方」與顧客端「模型看情況決定」。
- **客服 AI 助手**：案件頁旁的只讀工具 Agent（查訂單、規則、對話紀錄），轉人工時自動帶上顧客端 Agent 已查過的內容。

### 4.7 多 Agent Handoff 與長期記憶（M5）
- Handoff：分流 Agent → 換退貨專員／品質專員／物流專員（MAF handoff orchestration），每位專員只拿自己需要的工具；
  顧客端頁面可切換「單一／多 Agent」，面板顯示交接事件。
- 長期記憶：自訂 `AIContextProvider` 於每次呼叫前注入 `CustomerMemory`（標註「來自記憶，需與顧客確認」）；
  寫入記憶走 `remember_preference` 工具（面板可見）；顧客「我的 AI 記憶」頁可檢視／刪除。記憶永不當作業務事實。

### 4.8 效益對照頁（M6）
依劇本／案件聚合 `ProcessMetric` 與 `TokenUsageLog`（以 ConversationId 關聯）：顧客操作步數、端到端耗時、客服人工介入次數、
AI 成本（新台幣）、Agent 停下詢問次數。並排呈現傳統 vs Agent（單一／Handoff）。
另有「假設值」區（`DemoAssumption` 設定：排隊等審核時間、客服每分鐘成本、平均來回聯繫次數、月案件量），
以不同底色標示「假設」，與「實測」分開呈現，並算出月人力時數／成本節省推估。

## 5. 腳手架慣例（實作必守，來源 `docs/architecture/開發慣例與限制速查.md`）
- 分層 Web→Business→AccessDatas；Blazor 服務注入 `IDbContextFactory<BackendDBContext>`；分頁 `OrderBy…ThenBy(Id)`。
- 每次模型變更在 `CustomerHelpAgent.AccessDatas/Migrations/` 產生 migration；新設定類別 `AddValidatedOptions`。
- 套件版本只寫 `Directory.Packages.props`，**釘死版本**（MAF minor 版也會 breaking）；`TreatWarningsAsErrors`；`dotnet format` 乾淨；注意遞移相依弱點。
- 新頁：`Menu.json` id＋`SidebarMenuService.MenuPermissionMap`＋`MagicObjectHelper` 常數＋`RolePermissionService` 角色矩陣；
  每頁 `HelpTopics.json`＋`Datas/Help/<路由>.md`（七段、含 BOM）；CRUD 型模組（商品、規則、訂單維護）優先用 `scripts/New-CrudModule.ps1` 產生。
- UI：BlazorMaterialIcons、`ViewNotification`、`ConfirmDialog`、`RequirePermission`；色票只在 `theme.css :root`；浮層樣式在 `OverlayStyles.razor`。
- 日誌不得含姓名／Email／token；API 回 `ApiResult<T>`＋`[HasPermission]`。
- 每次建置 `appsettings.json` 版本 Patch+1（目前 `1.0.0 (2026/10/23)`），不自動 commit；同步更新 docs 與 `docs/README.md` 索引。

## 6. 里程碑與驗證

| # | 內容 | 驗證 |
|---|---|---|
| M0 | 設計文件落地 `docs/prd/`、加入 MAF 套件、`AuditingChatClient`、OpenAI／Azure `IChatClient` 工廠 | 單元測試：每次模型呼叫寫 TokenUsage／AiCallLog、額度擋下；品質關卡通過 |
| M1 | 模擬業務系統＋種子＋`ShopLayout` 顧客版面＋傳統顧客／客服流程＋埋點 | 服務測試（訂單歸屬、規則版本、簽收天數計算）；手動跑傳統換貨全流程 |
| M2 | 單一 Agent＋工具＋確認卡＋幕後面板＋Session 持久化 | **越界測試**（用假 `IChatClient` 腳本化模型回應）：未確認不寫入、偽造他人 Id 無效、forbidden 立即停、步數上限停；實機跑文章劇本（黑 40／白 41） |
| M3 | 故障模擬、劇本＋重置、錄製／回放、身分切換 | 每個故障開關對應測試的停止原因；回放產出與錄製一致；`DemoSettings.Enabled=false` 時控制台與身分切換不存在 |
| M4 | 申請初審 Workflow＋人工節點＋Checkpoint＋客服 AI 助手 | Workflow 測試（假 Agent）：併行節點、人工決策前不執行、重啟從 checkpoint 續跑 |
| M5 | Handoff 切換＋長期記憶頁 | 交接事件出現在面板；刪除記憶後不再注入 |
| M6 | 效益對照頁 | 以種子劇本跑兩種流程後數字合理、AI 成本與 TokenUsage 頁一致 |

> 進度：**M0 已完成（1.0.2）**——`AgentChatClientFactory`＋`AuditingChatClient`；**M1 已完成（1.0.3）**——模擬業務系統（10 個實體、migration `AddShopDomain`）、`ShopLayout` 顧客頁、傳統審核流程、示範帳號種子；**M2 已完成（1.0.4）**——`/shop/assistant` 單一售後 Agent（`AfterSalesAgentRunner`／`AfterSalesAgentGuard`／6 個受控工具）、`ApprovalRequiredAIFunction` 確認卡、幕後面板、`AgentSessionRecord` 會話持久化（migration `AddAgentRuntime`）、`/agent-runs` 後台紀錄頁，並補強 M1 服務（重複申請、目標規格、照片位元組上限、規則版本基準）；**M3 已完成（1.0.5）**——`DemoControlService` 七種故障模擬、`DemoResetService` 一鍵重置（固定編號）、`RecordingChatClient`／`ReplayChatClient` 錄製回放與金劇本、`/Auths/DemoSwitch` 一次性票券身分切換、`/demo-console` 展示控制台（migration `AddAgentSessionVersion`、`AddDemoControl`、`AddAgentRecording`），並補強 M2 Agent（確認卡去重、每輪步數總量、標紅、確認後下一輪、完整還原與多分頁防覆蓋）；**M4 已完成（1.0.6）**——Agent 管道送件自動觸發的 AI 初審 Workflow（進件→照片／規則／歷史三路併行→摘要→人工決策→執行→通知，checkpoint 存 SQLite）、`AfterSalesDecisionService` 核准開單（處理單 `AfterSalesFulfillment`）與顧客通知鈴、審核頁流程圖與 AI 建議面板、客服 AI 助手與轉人工交接摘要、初審錄製回放（migration `AddAfterSalesFulfillment`、`AddPreReviewWorkflow`、`AddRecordingStream`）；**M5 已完成（1.0.7）**——線上客服「單一／多 Agent」切換（MAF 內建 Handoff 編排：分流 Agent → 換退貨／品質／物流專員，每位專員只拿自己的工具子集，面板與對話區顯示交接）、長期記憶（`remember_preference`＋記憶確認卡、`AIContextProvider` 注入、`/shop/memory` 我的 AI 記憶頁）、申請管道「AI 多 Agent」照樣跑 M4 初審、錄製回放保存模式、劇本 H 與 E（migration `AddCustomerMemory`、`AddAgentSessionMode`、`AddAgentRecordingMode`）；**M6 已完成（1.0.8）**——`/benefit-comparison` 效益對照頁（傳統表單、單一 Agent、多 Agent 依情境家族並排的實測指標、差異百分比與長條圖、AI 成本與 Token 用量頁對帳、可調整的假設值與月度推估、每次執行明細、Excel／PDF 匯出）、劇本執行標記（`DemoScenarioRun`＋埋點 `DemoRunId`）、新劇本 A-T／B-T／A-M（migration `AddDemoScenarioRun`、`AddDemoAssumption`）。**M0–M6 全部里程碑已完成。**
> M1 實際狀態機為 `Submitted`／`NeedMoreInfo`／`Approved`／`Rejected`（§4.1 的「草稿／初審中／待人工／已完成」留待 M2、M4 視需要擴充），詳見 [售後 Agent 示範系統](../features/售後Agent示範系統.md) §4。
> M2 實作與本文 §4.3 的差異：`track_shipment` 與 `check_variant_stock` 的參數是 `orderItemId`（不是 `orderId`／規格）；`ToolOutcome` 多了 `blocked`／`invalid` 兩種 status 與 `withheld_fields`；模型呼叫預設走 Responses API（`store=false`），因為實機推論模型在 Chat Completions 帶工具會被拒；`ChatClientAgent` 外加 `TracingChatClient` 與函式中介層 `AfterSalesAgentGuard`；§4.3「不確定即停」中「`not_found`／`timeout`／`conflict` 後模型仍下結論時面板標紅」**M2 未實作**（M2 改以程式封鎖寫入、逾時／權限／服務不可用以固定說明取代模型輸出來強制），延到 M3；實作與使用說明見 [售後 Agent 示範系統 §5](../features/售後Agent示範系統.md)。
> M3 實作與本文 §4.5 的差異：故障列舉為 `DemoFault`（七種，另加「無適用規則」`PolicyNotFound`），狀態存 `DemoControlState` 單列，不叫 `FaultSimulationSetting`；「權限過期」只影響 Agent 工具（傳統流程的登入逾時由真實驗證機制處理），其餘故障 Agent 與傳統流程同受影響；回放以請求摘要（`ModelRequestDigest`）逐次比對，不符即 `ReplayDiverged`，顧客動作不符另有 `ReplayActionMismatch`（可重試）；`DemoSettings.Enabled=false` 時身分切換一律拒絕、控制台頁面全部停用，但選單項目仍顯示給管理員；Production 開啟示範模式拒絕啟動。§4.3「查無後直接下結論標紅」已於 M3 實作。詳見 [售後 Agent 示範系統 §6](../features/售後Agent示範系統.md)。
> M4 實作與本文 §4.6 的差異：**不新增「已完成」狀態**——核准當下就在同一交易內開處理單，顧客看到「已核准」加處理單號；**傳統管道的核准同樣開單、扣庫存、通知顧客**（兩邊差別只在有沒有 AI，對照才公平），所有決定一律經 `AfterSalesReviewCoordinator` 與 `AfterSalesDecisionService.ApplyAsync`；**客服 AI 助手只在即時模式可用**（回放時停用，客服提問每次不同），初審 Workflow 本身支援錄製回放（`AgentRecordedCall.Stream` 分流）；助手的 6 個只讀工具沒有參數、案件綁定在開啟的那一筆，並即時查 `售後審核:view` 權限；客服在初審跑完前就決定允許（先確認），之後該輪標 `Superseded`、AI 結果僅供參考；checkpoint 粒度是 superstep 而不是節點，因此每個節點把結論先存進 `PreReviewRun` 以做到續跑冪等（已存結論的節點不再呼叫模型）；規則核對沒有成功時 AI 的任何建議一律降為「需人工判斷」；Workflows 1.25.0 的 fan-in 訊息從 checkpoint 還原時內容為空，以自訂 JSON 轉換器繞道（升級套件時須重驗）；送模型前的資料一律先遮蔽個資（`PreReviewPrivacy`），圖片不寫進 AI 對話紀錄。詳見 [售後 Agent 示範系統 §7](../features/售後Agent示範系統.md)。
> M5 實作與本文 §4.7 的差異：**多 Agent 建立的申請記在原本沒有使用的 `AgentHandoff` 管道，標籤由「AI 轉人工」改為「AI 多 Agent」**（數值不變），AI 管道一律以 `IsAi()` 判斷，所以同樣跑 M4 初審（§3.1「傳統 vs Agent 以管道切分」延伸為兩個 AI 管道）；**記憶一種偏好只存一筆**（鞋碼、衣服尺碼、顏色、溝通偏好各一筆，新值覆蓋舊值），寫入一律經記憶確認卡，程式重核內容並拒絕電話、Email、地址、姓名與帳號；**記憶以固定的系統指示注入**（`AIContext.Instructions`，不是顧客訊息，不影響回放摘要；注入文字與使用規則由程式產生，不在可編輯的提示詞裡）；**交接採 MAF 內建 Handoff 編排**（`HandoffWorkflowBuilder`＋`workflow.AsAIAgent`），但確認卡回覆前必須呼叫實驗性的 `WorkflowSessionCheckpointRecovery.TryPrepare()`（`MAAI001`，依 §3.1 接受預覽／實驗性 API 並註記），並依 1.25 的框架行為補上五條 Runner 規則（只用外部那一份核准請求、待決卡改打字先明確拒絕、出錯不存回、每輪清 checkpoint、固定參與者 Id），全部由 `MafHandoffBehaviorTests` 釘住；框架沒有交接事件型別，交接事件由程式從串流判斷後發出；**每輪步數上限為 `HandoffMaxSteps + 1`**（預設 14＋1，與單一模式 `MaxSteps + 1` 同一公式）；新增劇本 H（B 的多 Agent 版）與 E（記憶；回放只涵蓋第一段會話）。詳見 [售後 Agent 示範系統 §8](../features/售後Agent示範系統.md)。
> M6 實作與本文 §4.8 的差異：**新增「劇本執行」**（`DemoScenarioRun`，每按一次「準備此劇本」一筆，埋點蓋上 `DemoRunId`）才能依劇本分組，重置不刪；**新增傳統版劇本 A-T（換尺碼）、B-T（一句兩事，兩張表）與多 Agent 版 A-M**，讓兩個情境都有傳統、單一 Agent、多 Agent 三種做法並排（§4.8 只寫「傳統 vs Agent」）；**假設值多 3 個欄位**（排隊等待與每案聯繫次數各分傳統／AI 兩值，另加每次聯繫分鐘數），月度推估的公式才完整，且可在頁面上修改（只有具修改權限者，示範環境為管理員；店家管理者只能看）；**所有指標只讀重置後仍保留的資料表**（劇本執行、埋點、執行軌跡、Token 用量、AI 對話紀錄），不讀申請表；**有效執行**要有顧客操作且有建立申請，回放與無效執行不列入任何平均（明細照列並寫原因）；**月度推估只用「已完成」執行**（有客服決策與開啟紀錄）的同一群平均；**AI 成本口徑**為每案 CaseKey 下三種作業（AI 售後客服 Agent、AI 售後多 Agent、AI 售後初審 Workflow）的 `TokenUsageLog.CostTwd` 加總，經 `AiCallLog.ConversationId` 關聯（不是直接以 ConversationId 欄位關聯 `TokenUsageLog`），並列出與 Token 用量頁的對帳等式；**客服 AI 助手的成本不計入每案成本**（對不到案件，另列「未歸入每案」）；「客服人工介入次數」實作為客服操作步數與客服處理分鐘數；模型呼叫次數以 Token 用量筆數計（不用執行軌跡的次數，回放也會累加它）；另加 Excel／PDF 匯出。詳見 [售後 Agent 示範系統 §9](../features/售後Agent示範系統.md)。

**核准後的第一步**：把本設計存成 `docs/prd/售後Agent展示系統-設計.md`（UTF-8 含 BOM，更新 `docs/prd/README.md` 與 `docs/README.md` 索引），
並寫出兩份企劃書初版（見 §8），再依 superpowers:writing-plans 為 M0＋M1 寫出逐步實作計畫（後續里程碑各自再寫），逐里程碑實作、驗證。

每個里程碑結束：`pwsh ./scripts/Invoke-QualityGate.ps1` 全綠、版本 Patch+1、相關文件與操作說明同步、交由使用者自行 commit。
實作時先以 MAF 官方文件／NuGet 核對 API 名稱（`ApprovalRequiredAIFunction`、`AgentSession`、`AIContextProvider`、
Workflows request/response、Handoff builder 在 .NET 1.25 的實際命名）再寫程式。

## 7. 企劃書（使用者追加要求；兩份、皆放 `docs/prd/`、UTF-8 含 BOM、列入索引）

**A. 對外版 `docs/prd/售後Agent示範系統企劃書.md`**（讀者：老闆／客服主管／IT）
1. 為什麼需要：傳統「表單＋人工對照」的痛點；「接上 AI」vs「會辦事的 Agent」的差別（以自己的話重述文章核心觀念，**不引原文**，註明參考出處）
2. 示範情境：鞋服電商的換貨（雙鞋劇本）
3. 傳統 vs Agent 對照：顧客體驗、客服工作方式、流程與成本
4. 系統特色：受控工具、寫入前確認、該停就停、幕後透明、故障說得清、可刪除的長期記憶、多 Agent 分工、客服 AI 助手、效益量化
5. 白話架構概念圖
6. 套用到您的產業：盤點方法（哪些工作適合 Agent、哪些適合固定流程）＋**醫療／健康、製造業、金融／保險**對照例
7. 導入建議：分階段（盤點流程→只讀助手→受控寫入→擴大範圍）、各階段準備事項與風險、效益評估框架（不含報價）

**B. 內部版 `docs/prd/售後Agent示範系統企劃規劃說明書.md`**（讀者：使用者與團隊）
1. 緣起與目的
2. 需求來龍去脈：原始需求、討論中每項決策與理由（即本文件 §2／§3／§3.1）
3. 完整架構：分層、MAF 元件對應、資料模型、工具五要素、迴圈出口、計費與日誌
4. 各特色的技術做法
5. **30 分鐘展示劇本**：開場話術、操作順序、每一幕觀眾要看到什麼、故障示範時機、常見提問與回答、出狀況切回放的方法
6. 里程碑與驗證
7. 附錄：文章原文全文（使用者貼上的逐字稿）

兩份在 M0 寫初版；每個里程碑完成後同步更新架構／特色／劇本章節，系統做出來後補截圖。

## 8. 留給實作者決定
資料表細欄位、頁面版型細節、白話文案措辭、劇本數量（至少：文章雙鞋劇本、退貨退款、物流催件、各故障情境）、
工具連續失敗門檻與步數上限預設值（設定檔可調）。


> 返回 [PRD 主控台](README.md)
