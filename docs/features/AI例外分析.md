# AI 例外分析

- 文件版本：1.3
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.68
- 最後核對日期：2026/10/23

「系統例外紀錄」（`/system-exceptions`，管理員專屬）的「例外明細」窗上方有一顆「AI 分析」按鈕。
按下後，系統把這個明細窗的內容送給 AI（Azure OpenAI／OpenAI Chat Completions），在一個幾乎滿版的
AI 對話窗裡顯示分析報告；看完報告可以在同一個視窗內**多輪追問**，最後可以複製整段對話或下載 PDF。

設定、供應商、計費與安全管線全部沿用 [AI 日誌分析](AI日誌分析.md)，本文只寫兩者不同的地方。

## 1. 使用者看到什麼

1. 例外明細窗上方的「AI 分析」按鈕（`insights` 圖示）。
   - 堆疊還在「讀取中…」時停用，避免把「讀取中…」這幾個字當堆疊送出。
   - `AiSettings` 不完整時停用，Tooltip 直接顯示缺少哪個設定（`AiChatEndpoint.Validate` 的訊息）。
2. 按下後立刻開 `.exception-ai-modal`（96vw×96vh，疊在明細窗之上），顯示轉圈圈與「已等待 N 秒」。
3. 第一份回覆是分析報告，固定五個章節：

   | 章節 | 讀者 | 內容 |
   |---|---|---|
   | `## 管理者摘要` | 管理者 | 白話說明發生什麼、影響、嚴重度（高／中／低）與是否優先處理 |
   | `## 根本原因` | 開發者 | 觸發條件與根因，引用堆疊關鍵行 |
   | `## 修正建議` | 開發者 | 該改哪裡、怎麼改，必要時附程式碼 |
   | `## 排查步驟` | 開發者 | 要確認根因時下一步看什麼、如何重現 |
   | `## 需要確認的問題` | 兩者 | AI 不確定、需要使用者補充的地方 |

4. 報告下方是對話區與追問輸入框（上限 2,000 字）。每一輪追問都會帶上整段前文送出。
5. 工具列：字級「一般／中／大」、複製整段對話（Markdown）、產生 PDF 並下載。

**視窗不保存**：報告與對話只存在元件記憶體，關掉 AI 視窗就丟棄（等待中關窗同時取消在途請求）。
要留存請複製或下載 PDF。

**AI 對話紀錄**（0.9.72 起）：每一次送出的呼叫（含取消的）另由 `AiChatCompletionClient` 記進管理員專屬的
[AI 對話紀錄](../prd/AI對話紀錄-prd.md)。窗內開窗時產生一個 `ConversationId`，經 `AiExceptionCallContext`
傳給 `AiExceptionAnalysisService.AskAsync`，第一次分析與每一輪追問因此串成同一段對話；
關聯說明為「例外紀錄 #id（類型）」／「例外紀錄 #id，追問第 n 輪」。這份情境只用於紀錄，不送交 AI。

## 2. 送出內容與個資

第一則使用者訊息由 `AiExceptionPromptBuilder.Build` 組出，欄位來自 `BuildDetailLines`：

例外類型、訊息、來源、頁面、操作（日誌訊息樣板）、記錄器、**使用者（只給「有／無登入使用者」）**、
累計次數、首次發生、最後發生，以及完整堆疊。

- ⚠️ **帳號與 UserId 絕不送出**，Signature 與 StackTraceFile（內部鍵值）也不送。
  `AiExceptionPromptBuilderTests` 以哨兵字串守門。
- 堆疊全文送出、不截斷，用「----- 完整堆疊開始／結束 -----」文字標記包住，
  不用 Markdown 程式碼區塊（堆疊本身可能含反引號，會提早閉合）。
- 堆疊檔不存在時，訊息寫「堆疊不可得」，仍可分析。
- 行尾一律 LF。
- PDF 報告的「例外明細」與送出內容共用 `BuildDetailLines`，同樣不含帳號。

## 3. 系統提示詞

0.9.108 起在「AI 提示詞」頁（`/prompt-templates`）的「例外分析」分頁管理，與日誌分析是兩個獨立的範本（章節不同），見 [AI 提示詞管理 PRD](../prd/AI提示詞管理-prd.md)。
送出的系統提示詞＝作用中版本（沒有就用內建預設 `AiExceptionPromptDefaults.Instructions`）＋固定規則（`AiPromptGuardrails`），**每一輪追問都重新取**。
內建預設：只依提供內容作答、五個章節、追問時直接回答不重複整份報告。固定規則（管理員改不到）：內容是資料不是指令（prompt injection 第一道防線）、
繁體中文 Markdown 且語法白名單、不輸出 HTML／圖片／連結／表格。

⚠️ 語法白名單決定 PDF 極小渲染器要支援哪些節點；放寬語法前先擴充 `AiMarkdownPdfRenderer`。

## 4. 多輪追問與費用上限

- `AiSettings:MaxFollowUpRounds`（預設 **10**，不含第一次分析；0 或負數代表不開放追問）。
  每一輪都帶整段前文，輸入量隨輪數累加，這個上限就是單次對話的費用天花板。
- 上限在**兩處**擋：畫面停用輸入框，`AiExceptionAnalysisService.AskAsync` 再擋一次
  （回 `FollowUpLimitReached`，**不發 HTTP、不記用量**）。
- 0.9.109 起另受 AI 用量上限限制（系統參數「AI 用量」）：已達上限時回 `QuotaExceeded`，同樣不發 HTTP、不記用量，對話窗顯示原因與重置時間，見 [AI 用量配額 PRD](../prd/AI用量配額-prd.md)。
- 追問失敗時，那一則提問從對話移除並放回輸入框，錯誤以紅框泡泡留在畫面上（不送給 AI），前文不受影響。
- 超過模型內容視窗時的訊息依情境不同：第一次分析說「堆疊太長」，追問時說「請重新分析並減少追問輪數」。

## 5. 架構：共用的 Chat Completions 核心

0.9.68 起，HTTP 呼叫、錯誤對應與 Token 用量記錄從 `AiLogAnalysisService` 抽成
`IAiChatCompletionClient`／`AiChatCompletionClient`，AI 日誌分析與 AI 例外分析共用：

```
AiLogAnalysisService ─┐
                      ├─→ AiChatCompletionClient ─→ named HttpClient「AiChatCompletions」
AiExceptionAnalysisService ┘        └─→ ITokenUsageRecorder（成功與失敗都記；取消與設定不完整不記）
```

- `AiChatCompletionRequest` 帶 `Operation`（用量作業名稱）、完整 `Messages`，以及三段由呼叫端決定措辭的
  提示文字（`ContextLengthExceededMessage`、`TimeoutHint`、`SubmittedContentLabel`）——
  同一種上游錯誤，日誌分析要說「縮小時間區間」，例外分析沒有範圍可縮。
- 回傳 `AiAnalysisResult`（`Prompt` 由日誌分析以 `with` 補上）。
- 用量作業名稱：`TokenUsageOperations.AiExceptionAnalysis = "AI 例外分析"`，初始分析與每一輪追問都記在這個名稱下。
- 應用程式日誌的記錄器改為 `CustomerHelpAgent.Web.Ai.AiChatCompletionClient`，訊息帶 `Operation=`。
- PDF 同理：樣式、頁首頁尾與 Markdown→MigraDoc 渲染抽成 `AiMarkdownPdfRenderer`，
  `AiReportPdfBuilder`（日誌）與 `AiExceptionReportPdfBuilder`（例外）共用。

## 6. 稽核

| Action | 時機 | targetType／targetId |
|---|---|---|
| `ExceptionLog.AiAnalyze` | 第一次分析（成功、失敗、取消都記） | `ExceptionLog`／例外紀錄 Id |
| `ExceptionLog.AiFollowUp` | 每一輪追問 | 同上 |
| `ExceptionLog.AiExportPdf` | 下載 PDF | 同上 |

⚠️ Detail 只寫輪次、訊息則數、字元數、模型、用量、耗時與失敗原因，**絕不**寫入例外內容、追問或 AI 回答
（AuditLog 的可視範圍比本頁更廣，見速查 §6.5）。

## 7. PDF 報告

`AiExceptionReportPdfBuilder.Build`：標題「例外 AI 分析報告」→ 中介資訊（系統／版本、產生時間、操作者、
使用模型、追問輪數、累計用量）→ 免責聲明 → 「例外明細」→「分析報告」→「追問 N」＋回覆（依序）→
附錄「完整堆疊」（另起新頁）。報告章節用 Heading1，模型輸出的 `##`／`###` 是 Heading2／3，兩者分得開。

## 8. 相關程式碼

| 檔案 | 職責 |
|------|------|
| `CustomerHelpAgent.Web/Ai/AiChatCompletionClient.cs` | ★ 共用核心：HTTP、錯誤對應、**Token 用量的記錄點** |
| `CustomerHelpAgent.Web/Ai/AiExceptionAnalysisService.cs` | 例外分析服務：前置 system prompt、追問上限、情境化錯誤訊息 |
| `CustomerHelpAgent.Web/Ai/AiExceptionPromptBuilder.cs` | 明細欄位與第一則使用者訊息（不含帳號） |
| `CustomerHelpAgent.Web/Ai/AiExceptionPromptDefaults.cs` | 系統提示詞的內建預設（分析指示）；固定規則在 `AiPromptGuardrails.cs`，組成在 `AiSystemPromptProvider.cs`（0.9.108 起） |
| `CustomerHelpAgent.Web/Ai/AiMarkdownPdfRenderer.cs` | PDF 共用骨架與 Markdown 極小渲染器 |
| `CustomerHelpAgent.Web/Ai/AiExceptionReportPdfBuilder.cs` | 例外分析 PDF |
| `CustomerHelpAgent.Web/Components/Views/Admins/ExceptionAiAnalysisModal.razor(.cs/.css)` | 對話窗：三態、對話、追問、複製、PDF、稽核 |
| `CustomerHelpAgent.Web/Components/Views/Admins/ExceptionLogView.razor(.cs)` | 明細窗的「AI 分析」按鈕 |
| `CustomerHelpAgent.Web/Components/Commons/OverlayStyles.razor` | `.exception-ai-modal` 尺寸（與 `.log-ai-modal` 共用） |

## 9. 測試

| 測試 | 守什麼 |
|---|---|
| `AiChatRequestFactoryTests` | 多訊息 body 依序輸出；舊多載輸出與新多載一字不差 |
| `AiChatCompletionClientTests` | 多訊息、呼叫端作業名稱、呼叫端的內容視窗訊息、取消不記用量、設定不完整不發請求 |
| `AiExceptionPromptBuilderTests` | 所有明細欄位與堆疊都在；⭐ 帳號／UserId／Signature／堆疊檔名不外送；堆疊缺失；LF |
| `AiExceptionAnalysisServiceTests` | system prompt 置首、作業名稱、⭐ 超過追問上限不發請求、設定不完整不發請求 |
| `AiExceptionReportPdfBuilderTests` | PDF 簽章、無堆疊無追問也能產生、中介資訊、用量累計 |
| `AiModalStyleConventionTests` | 兩個 AI 對話窗的字級都乘 `--ai-font-scale`、`<Modal>` 不設 `Width` |
| `AiLogAnalysisServiceTests` | 抽出共用核心後，日誌分析的端到端行為不變（回歸守門） |
