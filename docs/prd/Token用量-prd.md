# Token 用量 PRD

- 文件版本：2.1
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.14
- 最後核對日期：2026/10/23

## 一、目標與範圍

觀察系統對 LLM API 的使用情況：**用在什麼地方、用了多少 token、能統計與篩選**。

- 範圍：一套通用的用量記錄基礎設施（任何 LLM 呼叫點都能接）、七個統計頁籤（含折線趨勢圖）、
  CSV／PDF 匯出、單筆刪除／批次清除／清空全部，以及依保留天數自動清除（0.9.96 起，見 §4.8）。
- 0.9.17 起每一列另有**費用估算**（USD／TWD）。單價與匯率設定、計算規則、模型比對與
  已知限制**一律以 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md) 為權威**，本文件只描述
  它在本頁上的呈現。
- 非範圍：不提供 Web API。費用相關的非範圍見該 PRD。

> **現況提醒**：本腳手架目前有**三種**作業會記入本頁：
> 「AI 日誌分析」（日誌檢視頁，使用者主動觸發）、
> 「AI 例外分析」（0.9.68 起，系統例外紀錄明細窗，使用者主動觸發；初始分析與每一輪追問各記一筆）與
> 「系統健康檢測」（0.9.24 起，系統健康監控頁每次載入自動發出一句 hello）。
> 可用「作業」篩選器分開檢視。
>
> ⚠️ 健康檢測的**次數**會遠多於日誌分析（每開一次 `/system-health` 就一筆），
> 但每筆只有幾十個 token、費用約 NT$0.005。未套用作業篩選時，
> 「篩選範圍合計」會把它一併計入。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 |
| --- | --- | --- |
| `/token-usage` | 系統管理 → AI 管理 → id=64「Token 用量」（0.9.113 前在「統計與分析」） | **管理員專屬**（`CheckIsAdmin()`；權限鍵不上架角色矩陣） |

## 三、畫面

### 3.1 篩選與動作

- 篩選：帳號、起始日、結束日、全部作業、全部型別、全部模型
- 動作：查詢、重新整理、匯出 CSV、**下載全部頁籤的 PDF 報表**、**只匯出目前頁籤的 PDF**（0.9.51 起）、
  清空全部；另有「清除此日之前 ＋ 批次清除」
- 破壞性動作一律 `ConfirmDialog.AskDestructiveAsync` 二次確認

### 3.1.1 AI 用量上限（0.9.109 起）

說明文字下方、工具列上方一行：全系統今日、本月已用 vs 上限（達 80%、100% 加標示），與每人每日、每月上限的設定值。上限在系統參數「AI 用量」調整，見 [AI 用量配額 PRD](AI用量配額-prd.md)。

### 3.2 合計卡（0.9.53 起改為卡片；此前為單行橫幅）

標題列「篩選範圍合計　—　套用條件：…」下方四張卡：

| 卡 | 主數字 | 卡內副行 |
|---|---|---|
| 輸入（送出／prompt）| `InputCount` | 其中快取 `CachedInputCount` |
| 輸出（接收／completion）| `OutputCount` | 其中推理 `ReasoningCount` |
| 合計 | `TotalCount` | 「輸入 ＋ 輸出；子集不重複計入」 |
| 花費 | `NT$ CostTwd` | `US$ CostUsd`；未定價時改顯示橘色「⚠ 其中 N 筆未定價未計入」 |

⚠️ **為什麼是四張不是六張**：快取是**輸入的折扣子集**、推理**計入輸出**，
兩者都只回報「其中多少」，不再加進合計，否則會重複計算。
因此它們**寫在對應的卡片內**，而不是各自一張對等的卡 ——
六張長得一樣的卡會讓人很自然地相加，用版面層級表達從屬關係比只靠「其中」兩個字可靠。

數字以 K／M 精簡呈現（`TokenUsageFormat.Compact`）。

⚠️ **這排的條件含日期，下方的近 N 天卡不含**（那排會把日期換成自己的窗）。
兩行「套用條件」指的不是同一個範圍，所以文字刻意不一樣；
兩者共用 `BuildFilterText(includeDates)` 組字串，不會各自漂移。

⚠️ 這排是 `<article>`、**不可點**，沒有游標與 hover；下排是 `<button>`、可點套用區間。
兩排共用 `.token-usage-stat-card` 這組基底樣式，只有互動效果不同。

費用（0.9.17 起）：未定價的列不計入。
⚠️ 美金與台幣**各自為逐列加總**，區間橫跨匯率調整時兩者彼此推不出來 —— 那是正確行為。
費用的計算規則見 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md)。

### 3.2.1 「最近 N 天」摘要卡（0.9.50 起）

合計卡下方三張卡：**近 1 天（今天）／近 7 天／近 30 天**，各顯示 NT$、US$、呼叫次數與合計 token。

- 「最近 N 天」＝**含今天往前數 N 日**（近 1 天就是今天；近 7 天是 `today-6` 到 `today`）。
  這個定義只寫在 `TokenUsageRanges` 一處，卡片標題、卡片查詢與趨勢預設區間共用它。
- **點卡片＝把該區間套進起訖日再重查**，其餘篩選維持不動；選中的區間會標示 `is-active`。
- 卡片**沿用目前的帳號／作業／型別／模型篩選，只把日期換成自己的窗** ——
  選了某個模型，卡片回答的就是「這個模型近 1／7／30 天花了多少」。
  卡片上方那行字會列出目前套用的非日期條件，避免把篩選後的小數字誤讀成全站總額。
- 未定價沿用既有語意：不計入金額，另外顯示「⚠ 其中 N 筆未定價未計入」。

⚠️ 三張卡**只查一次資料庫**：近 1／7／30 天彼此是巢狀區間，所以取一次「近 30 天」的逐日資料
（`GetDailyAsync`），三張卡再從同一份日列切片加總（`CardOf`）。改成各呼叫一次
`GetSummaryAsync` 會是 24 次查詢（該方法內部有 8 個 `SumAsync`／`CountAsync`）。
逐日加總再相加與一次 SUM 全部資料列，在 `double` 最末幾位可能不同，顯示精度下無差異。

### 3.3 七個統計頁籤

**趨勢圖**（0.9.55 起，預設頁籤）＋ **每日趨勢**（0.9.50 起）＋ 依使用者／依作業／依模型／依型別
（四者共用 `TokenUsageGroupTable` 元件）＋ 明細。
頁籤標題帶筆數。明細逐筆顯示時間／使用者／作業／型別／模型／輸入／輸出／推理／快取／合計／費用，
失敗的列在「合計」欄顯示紅色「失敗」標籤（滑過看原因）。

費用欄（0.9.17 起）顯示台幣、可排序，Tooltip 帶美金原價、當時匯率與實際套用的費率鍵；
算不出費用時顯示橘色「未定價」。四張分組統計表也各加一欄台幣費用（Tooltip 顯示美金）。

⚠️ 明細表加入費用欄後共 11 個資料欄，**費用欄在 1280px 視窗下需橫向捲動才看得到**；
且**不可**用 `Fixed` 釘住（會讓整頁掛掉）。兩者的完整說明見
[LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md) 第三節。

**趨勢圖**（0.9.55 起，排第一個、預設頁籤）：上下兩張行內 SVG 折線圖 —— 上面是費用 NT$，
下面是合計 token，共用日期軸，各自一個從 0 起算的 Y 軸。

- **不做雙 Y 軸**：兩者單位不同（錢 vs token），雙軸圖上兩條線的交叉與相對高低沒有意義，只會誤導。
- **資料直接由畫面上的 `dailyRows` 分桶而來，不另查資料庫** —— 區間、篩選、補零與 180 天上限
  都與「每日趨勢」頁籤相同，兩個頁籤看到的必然是同一段資料。
- **時間顆粒依區間長度自動決定**（`TokenUsageTrendChart.MaxDailyPoints`＝92）：
  不超過 92 天一天一點；93～180 天改**按週加總、週一起算**。區間頭尾不滿一週的部分也是一個點，
  滑鼠提示寫出它實際涵蓋的起訖日，不假裝成完整的一週。
- 滑鼠移到點上，以 SVG `<title>` 顯示期間、NT$（US$）、合計 token、呼叫次數；有未定價的筆數時另外註明。
- Y 軸刻度取 1／2／5×10ⁿ 的整齊間距（約 4 格），上限一定是間距的整數倍；
  全部為 0 時退回 0～1，不除以零。X 軸日期最多印 8 個。
- **仍然不引入任何圖表函式庫，也不加 JS**。分桶、刻度與座標換算都在 `TokenUsageTrendChart`
  （`CustomerHelpAgent.Web/Ai/`），**畫面與 PDF 共用這一份**，兩邊畫出來的點與刻度必然一致。
  座標字串一律 `InvariantCulture`（與 `BarWidth` 同一個雷：逗號小數點會讓整條線靜默消失）。
- 色彩只用 `var(--app-accent)`（費用）與 `var(--app-accent-dim)`（token），與每日趨勢的兩根長條同一組。
- ⚠️ SVG 的 `<text>` 寫在 Razor 的 C# 區塊（`@foreach`）中會被當成 Razor 的 `<text>` 指令標籤而編譯失敗，
  必須外包一層 `<g>`。

**每日趨勢**（0.9.50 起）：每一列是一天 —— 日期、兩根長條（費用 NT$ 一根、合計 token 一根）、
右側的 NT$／合計／呼叫次數。

- **純 CSS 長條，專案刻意不引入任何圖表函式庫**；長度由行內 `style="width:…"` 給
  （`BarWidth`，⚠️ 必須 `InvariantCulture`：CSS 百分比不接受逗號小數點），
  `.razor.css` 只管顏色、高度與圓角，色彩一律 `var(--app-*)`。
- 兩根長條**各自以區間內的最大值歸一化，彼此不可互相比較**（單位不同：錢 vs token）。
  畫面上有一行字明講這件事。
- 區間**跟隨篩選**；起訖日都沒填時退回近 30 天。**上限 180 天**，超過只顯示最後 180 天並標註 ——
  純 CSS 清單沒有分頁，不設限會讓頁面長到不可用。
- **沒有資料的日期會補零**（`FillMissingDays`）。缺口不補的話，長條圖會把「那天沒花錢」
  畫成「那天不存在」，相鄰兩根柱子看起來就成了連續的兩天。

### 3.4 明細窗

點「詳細」開啟，分五區：基本資訊、輸入 (Input)、輸出 (Output)、其他、**費用**，
最後是**原始明細（供應商回傳）**——把檔案裡的 usage JSON 平攤成 `prompt_tokens_details.cached_tokens`
這種點路徑逐列顯示。開窗當下才讀檔。

費用區（0.9.17 起）沿用同一支 `FlattenJson` 把費率快照平攤顯示，快照就在資料列上，
不必再讀檔。這一區是「帳目可重現」的實際入口：有快照與 token 數就能手算驗證 ——
欄位與驗證方式見 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md) §4.6。

基本資訊區最後一列是「**AI 對話紀錄**」（0.9.72 起）：這一筆的 `CallId` 在 AI 對話紀錄裡還查得到時，
顯示對話圖示按鈕，按下導向 `/ai-call-logs?callId=…` 並自動開啟同一次呼叫；查不到時顯示
「（0.9.72 之前的紀錄沒有對話可看）」（`CallId` 為 null）或「（對話紀錄已清除，或當時未啟用記錄）」。
AI 對話紀錄預設只保留 90 天，比本表短；取消與非預期錯誤的呼叫只記在那一邊，所以兩頁筆數可能不同。

⚠️ 明細窗的**尺寸與內容樣式都寫在 `Components/Commons/OverlayStyles.razor` 的全域 `<style>`**：
AntDesign 的 Modal 渲染在元件 DOM 之外，scoped CSS（連 `::deep`）打不到。
該檔 `<style>` 內的 `@media` 必須寫成 `@@media`，否則 Razor 編譯失敗。

### 3.5 PDF 匯出的兩種範圍（0.9.51 起）

工具列有兩顆 PDF 鈕：**下載全部頁籤**（`picture_as_pdf`）與**只匯出目前頁籤**（`description`）。
範圍由 `TokenUsageReportScope` 表示（`All`／`Trend`（0.9.55 起）／`Daily`／`Account`／`Operation`／`Model`／
`CallKind`／`Detail`），畫面以 `activeTabKey` 對應過去。

⚠️ **標題、條件區與合計不受範圍影響，一律輸出。**
一份看不出是在什麼條件下產生的統計沒有意義。條件區印的是
`產生時間`／`操作者`／`查詢區間`／`篩選條件`（帳號／作業／型別／模型，沒設印「（不限）」）。

**原則：PDF 內容＝畫面當下看到的內容。**

- 趨勢直接送畫面算好的 `dailyRows`（**已補零、已套 180 天上限**），**PDF 不重新查資料庫**，
  所以報表的格數與日期範圍與畫面必然一致。
- 四張分組表送畫面上的 `groupedByXxx`，排序與畫面相同。
- 只有明細例外（畫面是分頁的）：重查一次全量再取前 `MaxDetailRows`＝**200** 筆。
  ⚠️ **明細只在範圍是 `All` 或 `Detail` 時才查** —— 匯出「依模型」不該為了一份用不到的明細去掃全表。

檔名：整份維持 `CustomerHelpAgent.Web-llm-usage-{時間}.pdf`（**舊檔名不變**）；
單頁籤加上頁籤代碼 `CustomerHelpAgent.Web-llm-usage-model-{時間}.pdf`。

⚠️ **中文換行（0.9.52 起）**：MigraDoc 不會替中文換行，所有進 PDF 的文字
（含表格儲存格）一律經 `CjkLineBreak.AddTo` 切段。0.9.52 之前，合計下方那段 97 字的「註」
會排成 30.79cm 寬、**最後約 8 個字掉出紙外**。完整的機制與禁則見
[速查表 §6.11](../architecture/開發慣例與限制速查.md)。

⚠️ 合計行的「標籤」與「數字」之間夾 `U+00A0` 不斷行空格，兩者不會被拆到不同行；
群組之間的全形空格才是斷行點。

**趨勢在 PDF 裡的呈現**：日期、費用長條、次數、合計 token、費用(TWD)、費用(USD)。

**趨勢圖在 PDF 裡的呈現（0.9.55 起）**：與畫面相同的上下兩張折線圖，點與刻度來自同一個
`TokenUsageTrendChart`，配色沿用報表自己那組（不用 theme.css）。

- ⚠️ MigraDoc 沒有圖表，也不能在排版流程中直接拿 `XGraphics` 畫圖。做法是先用 PDFsharp 把兩張圖
  畫進一份**單頁 PDF**（`RenderTrendChart`），再以 `base64:` 圖片來源 `AddImage` 嵌回報表 ——
  嵌進去的是向量（Form XObject），放大不會糊，也不必落地暫存檔。
- 圖高約 11cm，放不下時整張換頁；「趨勢圖」標題與說明設 `KeepWithNext`，跟著圖走。
- 區塊順序與頁籤一致：趨勢圖、每日趨勢、四張分組表、明細。

- ⚠️ 長條**不能**用「巢狀表格＋依比例算欄寬」做 —— **MigraDoc 不支援巢狀表格**。
  改用固定分段：`BarSegments`＝30 個等寬窄欄，依比例把前 k 格上網底，表頭用 `MergeRight` 合併。
- ⚠️ **只畫費用一條**。token 合計以數字欄位呈現 —— 兩者單位不同（錢 vs token），
  共用一個尺規只會誤導，與畫面上「兩條各自歸一化」是同一個考量。
- ⚠️ 分母可能是 **0**（整段期間沒花到錢或全部未定價），一律先擋掉再除。
- 有未定價時於表下補一行說明：那些日子的長條會短於實際用量。

## 四、內部系統運作

### 4.1 記錄管線

```
LLM 呼叫點（發出 HTTP 的那一層）
   → ITokenUsageRecorder.RecordAsync(TokenUsageEntry)
   → TokenUsageLogService → IAiUsageCostCalculator（費用＋單價／匯率快照）
                          → SQLite（可聚合的數值欄位）
                          → TokenUsageRawStore → 檔案系統（原始 usage JSON）
```

**⚠️ 記錄點必須放在發出 HTTP 的那一層，不可放畫面層** —— 原始 usage、實際模型名稱與耗時，
回到畫面之前就已經丟失了。目前的呼叫點是 `AiChatCompletionClient.CompleteAsync`（0.9.68 起由 AI 日誌分析與
AI 例外分析共用）與 `AiHealthProbe.ProbeAsync`。

**不需要佇列與背景寫入器**（與系統例外紀錄不同）：LLM 呼叫是使用者主動觸發、一次數秒到數分鐘，
多一次幾毫秒的資料庫寫入可忽略。

### 4.2 新增 LLM 呼叫點的擴充方式

1. 注入 `ITokenUsageRecorder`
2. 在 `TokenUsageOperations`／`TokenUsageCallKinds` 登記常數
3. 拿到 API 回應後呼叫一次 `RecordAsync`

該次呼叫就會自動出現在本頁與所有統計頁籤上，不需要改頁面；費用也會自動算好，
呼叫端不必知道計算器的存在。0.9.109 起有費用的紀錄也會計入 AI 用量上限並觸發 80%／100% 通知；
但**送出前的上限檢查在 `AiChatCompletionClient`**，自己發 HTTP 的新呼叫點要先呼叫 `IAiQuotaService.CheckAsync`，否則不會被擋。

⚠️ 但**要記得在 `AiPricingSettings.Models` 補上該模型的費率**，否則那些呼叫會全部
記成「未定價」（有筆數、沒金額）。見 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md)。

### 4.3 記錄哪些呼叫

**全部記錄，成功與失敗都記**：

| 情境 | 有用量？ | 說明 |
| --- | --- | --- |
| 成功 | 有 | — |
| **回應成功但內容為空** | **有** | **付了錢卻沒拿到東西**，最值得被看見 |
| 逾時／傳輸失敗／上游錯誤 | 無 | 上游可能已計費，至少留下次數 |
| 設定不完整 | — | **不記**，根本沒發出請求 |
| 使用者取消 | — | **不記**，與例外紀錄不收 `OperationCanceledException` 同一原則 |
| 非預期錯誤（上面以外的例外） | — | **不記**（寫錯誤日誌） |
| 被 AI 用量上限擋下（0.9.109 起） | — | **不記**，根本沒發出請求（另寫稽核 `Ai.QuotaBlocked`） |

取消與非預期錯誤的請求若已送出，仍會記在 [AI 對話紀錄](AI對話紀錄-prd.md)，所以兩頁筆數可能不同。

### 4.4 原始 usage JSON

- 路徑：`SystemSettings.ExternalFileSystem.TokenUsagePath`（預設 `C:\temp\CustomerHelpAgent\TokenUsage`）
- 檔名：`{yyyyMM}/{GUID}.json`（每次呼叫獨立一筆，不合併，所以用 GUID 而非雜湊）
- 生命週期集中在 `TokenUsageRawStore`；刪單筆／批次清除／清空全部三條路徑都經過它
- **價值**：供應商專有欄位（實測 Azure 會回 `latency_checkpoint`、`cache_write_tokens`）
  沒有對應的資料表欄位，但原始 JSON 完整留住了，日後新增欄位也不會漏接

### 4.5 ⚠️ 安全紅線

**只存 `usage` 子物件，絕不存提示詞與模型回應內文。**

AI 日誌分析的**提示詞就是整份日誌內容**（AI 例外分析則是例外明細與堆疊），而日誌內容有一部分
來自使用者輸入。本表保存期以年計、又會被統計與匯出，存進來等於繞過日誌檢視頁的管理員限制，也會破掉
「稽核紀錄不得寫入日誌內容或 AI 輸出」這條既有紅線。

0.9.72 起完整內文**只准**存進管理員專屬、會自動過期的 [AI 對話紀錄](AI對話紀錄-prd.md)；
本表只多一欄 `CallId`（索引）與它對應，明細窗的「AI 對話紀錄」列可直接跳過去看。

`AiChatResponseParser.ExtractUsageJson` 只取 `usage` 那一段，**不可**改成回傳整個回應 body。
由 `AiLogAnalysisServiceTests.AnalyzeAsync_ShouldRecordOnlyUsageJson_NotTheWholeResponseBody` 守門。

### 4.6 順帶修正

0.9.14 之前，PDF 匯出會用同一份分析結果**再寫一筆帶相同用量數字的 `AuditLog`**，
若拿 `AuditLog` 做成本加總會**重複計算**。記錄點改在 service 內之後，
只會記真正發出 HTTP 的那一次。

### 4.7 兩個編碼／樣式的坑（0.9.14 實測踩到）

1. ⚠️ **`new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text)` 不會輸出 BOM。**
   那個旗標只影響 `GetPreamble()` 與 `StreamWriter`。少了 BOM，Excel 開啟 CSV 會整片亂碼，
   而編譯、測試、程式碼審閱全都看不出來。正確寫法是把 `GetPreamble()` 接在 `GetBytes()` 前面。
2. ⚠️ **Blazor 的 scoped CSS 以元件為界。**`TokenUsageGroupTable` 是獨立元件，
   套不到 `TokenUsageView.razor.css` 裡的 `.token-usage-table-wrap`，所以四個統計頁籤
   會把整頁撐寬。修法是給該元件自己的 `TokenUsageGroupTable.razor.css`。

### 4.8 自動清除與稽核

- 排程作業「Token 用量紀錄清理」（`TokenUsageLogRetentionJob`，0.9.96 起，預設每天 03:00）刪除超過
  `LogRetentionSettings:TokenUsageLogDays`（預設 365，`0`＝不清除；可在「系統參數」頁「資料保留」覆寫）天的資料列與原始檔；
  刪到資料時寫 `TokenUsage.AutoPurge`（detail `rows=…; days=…; trigger=…`）。0.9.96 之前不會自動清除。
- 本頁的刪除、清除此日之前、清空全部與匯出（CSV、PDF）各寫一筆稽核：`TokenUsage.Delete`／`Purge`／`ClearAll`／`Export`
  （目標 `TokenUsage`，匯出的 detail 記格式、範圍與筆數），不記用量內容。

## 五、權限與安全

- **管理員專屬**：`CheckIsAdmin()` 守門，`角色_Token用量` **不列入**
  `RolePermissionService.GetRoleListPermissionAllName()`，由 `AdminOnlyPermissionTests` 守住。
- 只記 `Account` 與 `UserId`，**不記姓名／Email**，與全站日誌規範一致。
- ⚠️ 記錄相關的日誌訊息**不得含 `token` 字樣**（`LoggingConventionTests` 的禁用清單），
  這也是欄位叫 `InputCount` 而非 `InputTokens` 的原因。

## 六、驗收與測試

| 測試 | 驗什麼 |
| --- | --- |
| `TokenUsageLogServiceTests` | 寫檔＋寫列、失敗也記、刪列同時刪檔、清空、`PurgeBeforeAsync`、四個維度的分組統計、**合計不重複計算快取與推理**、結束日涵蓋當天整天、`RecordAsync` 絕不拋出；費用與快照寫入、未定價留空、**計算器拋例外時該列仍寫入**、費用加總與未定價計數、依費用排序 |
| `TokenUsageLogServiceTests`（0.9.50 新增）| `GetDailyAsync`：依當地日期分組（跨午夜的兩筆分屬兩列，同時驗證 `GroupBy(x => x.OccurredAt.Date)` 在 SQLite 譯得出 SQL）、逐日費用加總與未定價計數、套用篩選、回傳依日期遞增 |
| `TokenUsageRangesTests`（0.9.50 新增）| 「最近 N 天」＝**含今天往前數 N 日**：近 1 天為今天、近 7／30 天的起日與天數、傳入帶時分秒也要得到乾淨日期 |
| `AiUsageCostCalculatorTests`、`AiPricingSettingsTests` | 費用估算的計算與設定守門，見 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md) 第八節 |
| `AiLogAnalysisServiceTests` | 成功／空回應／上游錯誤三種分支都有記錄；設定不完整不記；**只存 usage JSON、不含回應 body** |
| `TokenUsageReportPdfBuilderTests` | PDF 簽章、無資料、明細上限 200 筆、依時長計費與失敗列都排得出來 |
| `TokenUsageReportPdfBuilderTests`（0.9.51 新增）| 趨勢區塊真的有排進去（比沒有趨勢時大）、趨勢空陣列不炸、⭐ **全部費用為 0 時不可除以零**、六種範圍都產得出且都小於整份、⭐ **單頁籤仍含條件區**、每個範圍的標題後綴不重複 |
| `TokenUsageReportPdfBuilderTests`（0.9.52 新增）| ⭐ **長中文篩選值真的有換行**（頁數比短值多）。搭配 `CjkLineBreakTests` 的「切段後接回去與原文一字不差」一起看 |
| 守門 | `DataAccessServiceLifetimeTests`、`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`ApiIntegrationTests`（`TokenUsagePath` 導向暫存目錄） |

手動驗收（0.9.14 實跑結果）：

- 統計與分析 →「Token 用量」可開，`toll` 圖示正常，五個頁籤數字一致，無 console 錯誤。
  （現況：0.9.113 起選單路徑為「系統管理 → AI 管理 →『Token 用量』」，頁籤共七個，見 §二、§3.3。）
- 以真實 Azure OpenAI 呼叫驗證：記到 `gpt-5.6-sol-2026-07-09`、輸入 8,122／輸出 1,967／
  合計 10,089、推理 439，使用者 `support`。
- 檔案內容經檢查**只有 usage 結構**，不含 `choices`／`message`／`content`。
- 明細窗四區與原始明細（九個點路徑欄位）皆正確。
- 匯出 CSV：檔頭三個位元組為 `EF BB BF`（UTF-8 BOM），Excel 開啟繁中不亂碼。
- 下載 PDF：115KB，內嵌 **Noto Sans TC** 子集（`FontFile2` + `Identity-H` + `CIDFontType2`），
  抽出文字比對**零缺字**，中文不是空白方塊。
- 清除此日之前 2026-09-01：7 列→4 列，原始明細檔 6→3，只刪指定日之前的。
- 刪單筆：資料列與對應的原始明細檔一併消失。
- 清空全部：0 列，`TokenUsagePath` 目錄淨空（連年月子目錄一起清掉）。
- 非管理員（暫時把 `IsAdmin` 設為 0 驗證）：側邊欄不顯示本頁，直接輸入網址顯示「你沒有權限存取此頁面」。
- 1366px／1600px／400px 三種寬度頁面皆無水平捲軸（明細表在窄螢幕於容器內自行捲動）；全站 console 零錯誤。

## 七、相關程式與文件

- `CustomerHelpAgent.AccessDatas/Models/TokenUsageLog.cs`、`Migrations/*_AddTokenUsageLog.cs`
- `CustomerHelpAgent.Models/Systems/TokenUsageModels.cs`、`AdapterModel/TokenUsageLogAdapterModel.cs`
- `CustomerHelpAgent.Business/Services/DataAccess/ITokenUsageRecorder.cs`、`TokenUsageLogService.cs`
- `CustomerHelpAgent.Business/Services/Other/TokenUsageRawStore.cs`
- `CustomerHelpAgent.Web/Ai/AiChatCompletionClient.cs`（記錄點，0.9.68 起）、`AiChatResponseParser.ExtractUsageJson`
- `CustomerHelpAgent.Web/Ai/TokenUsageReportPdfBuilder.cs`、`Ai/TokenUsageTrendChart.cs`、`Diagnostics/TokenUsageFormat.cs`
- `CustomerHelpAgent.Web/Scheduling/Jobs/TokenUsageLogRetentionJob.cs`（自動清除）
- `CustomerHelpAgent.Web/Components/Pages/Analytics/TokenUsagePage.razor`、
  `Components/Views/Analytics/TokenUsageView.razor`、`TokenUsageGroupTable.razor(.css)`
- 費用估算的程式清單見 [LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md) 第九節
- 交叉連結：[LLM 呼叫費用估算 PRD](LLM呼叫費用估算-prd.md)、[AI 日誌分析](../features/AI日誌分析.md)、
  [系統例外紀錄 PRD](系統例外紀錄-prd.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)
