# LLM 呼叫費用估算 PRD

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.17
- 最後核對日期：2026/10/23

## 一、目標與範圍

讓每一次 LLM API 呼叫除了 token 用量之外，還看得到**這次呼叫花了多少錢**（美金與台幣），
並能在「Token 用量」頁回答「**這個篩選範圍總共花了多少**」。

這是一份跨功能的權威文件：費用估算橫跨設定檔、計算器、資料表、頁面與匯出五個環節，
本身沒有選單入口，能力表現在「Token 用量」頁上。其他 PRD 連結至本文件。

- **範圍**：單價與匯率設定（`appsettings.json`）、模型比對規則、四種計費單位的算式、
  費用與單價快照的儲存、頁面與 CSV／PDF 上的呈現。
- **非範圍**：不自動抓匯率（手動維護）；不提供費用相關的 Web API；
  不支援 cache-write 費率（供應商 `usage` 沒有這項數字）；不回補歷史資料。

> ⚠️ **這是「估算」不是帳單。** 單價由人工從供應商定價頁抄錄、匯率手動維護、
> 長脈絡門檻是本系統自訂的估值。數字用來**觀察趨勢與相對佔比**，
> 對帳請以供應商實際帳單為準。文件標題用「估算」就是要讓這件事在第一頁就說清楚。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 費用在哪裡看得到 |
| --- | --- | --- | --- |
| `/token-usage` | 系統管理 → AI 管理 → id=64「Token 用量」 | **管理員專屬**（`CheckIsAdmin()`）| 合計卡「花費」（0.9.53 前為合計橫幅）、近 1／7／30 天摘要卡、每日趨勢與趨勢圖、四張分組統計表、明細表、明細窗、CSV／PDF |

費用能力沒有自己的頁面、路由或權限鍵，完全寄生在「Token 用量」頁的既有權限之下。
維護單價與匯率的入口是 `appsettings.json`，不是 UI。

## 三、畫面與欄位

| 位置 | 呈現 | 精度 |
| --- | --- | --- |
| 合計卡「花費」（0.9.53 起；之前是合計橫幅） | 主數字 `NT$ A`、副行 `US$ B`；有未定價時追加橘色「⚠ 其中 N 筆未定價未計入」 | TWD 2 位／USD 4 位 |
| 四張分組統計表 | 一欄「費用」顯示台幣，Tooltip 顯示美金；該組有未定價時附註「（N 未計）」 | TWD 2 位／USD 4 位 |
| 明細表 | 一欄「費用」顯示台幣、**可排序**；Tooltip 帶美金原價、當時匯率、實際套用的費率鍵 | TWD 4 位／USD 6 位 |
| 明細窗「費用」區 | 台幣／美金金額、當時匯率、計價依據、是否長脈絡費率、**套用的費率快照** | 同明細表 |
| CSV | `費用USD`／`費用TWD`／`匯率`／`計價依據`／`長脈絡` 五欄 | `F6`／`F4` |
| PDF | 合計段落加總花費；分組表與明細表各加一欄台幣費用 | 同畫面 |

單筆呼叫常常小於 0.01 元，所以明細層級刻意用高精度（TWD 4 位、USD 6 位），
位數不夠會全部顯示成 0；合計層級金額大，改用一般精度。

**未定價**的列顯示橘色「未定價」而不是 0 —— 「找不到費率」與「有費率但這次用量是 0」
在畫面上意義完全不同，記成 0 會讓漏帳看起來像免費。

⚠️ Tooltip 與明細窗**必須標示「前綴比對」**（`IsPrefixMatched`）。費率若是從別的模型名稱
推導來的，那是唯一能讓人發現「套錯兄弟模型費率」的地方。

⚠️ 合計卡的美金與台幣**各自為逐列加總**，不是互相換算。篩選區間橫跨匯率調整時
`SUM(CostTwd) ≠ SUM(CostUsd) × 任何單一匯率` —— 那是正確行為，頁面說明有寫。

### 已知的呈現限制

明細表加入費用欄後共 11 個資料欄（固定寬度合計 1200px）＋ 釘住的操作欄，
在 1280px 視窗下**費用欄需要橫向捲動才看得到**；四張分組統計表只有 8 欄，不受影響。
這張表在加費用欄之前就已經會橫向捲動（見 `TokenUsageView.razor.css` 的 `.token-usage-table-wrap`）。
不捲動也看得到的金額在合計卡、近 N 天摘要卡與四張分組統計表。

⚠️ **不要用 `Fixed="ColumnFixPlacement.Right"` 把費用欄釘住。** 0.9.17 實測過：
AntDesign 的 `ColumnBase.CalcFixedStyle()` 會去讀相鄰釘住欄的寬度，而隔壁的 `ActionColumn`
沒有設 `Width`，會丟 `ArgumentNullException` **讓整頁掛掉**。要釘就得連 `ActionColumn`
的寬度一起定（全專案 Fixed 欄都沒設過寬度，零先例；0.9.17 時 7 個，0.9.114 核對時 14 個），並回頭更新
[開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §6.2 的檢視清單。
程式碼裡該欄正上方有一段註解記著這件事，請勿「順手」加回去。

## 四、內部系統運作

### 4.1 計算管線

```
LLM 呼叫點 → ITokenUsageRecorder.RecordAsync(TokenUsageEntry)
           → TokenUsageLogService
               → IAiUsageCostCalculator.Calculate(entry)   ← 本文件的範圍
               → SQLite（用量欄 + 費用欄 + 單價／匯率快照）
               → TokenUsageRawStore → 檔案系統（原始 usage JSON）
```

計算器接在 `TokenUsageLogService.RecordAsync` **內部**，所以**呼叫端一行都不用改** ——
`AiChatCompletionClient`（0.9.68 起自 `AiLogAnalysisService` 抽出，AI 日誌分析與 AI 例外分析共用）與 `AiHealthProbe` 的記錄分支自動跟著有費用，日後新增的呼叫點也是。

⚠️ 計算器的呼叫**另包一層 `try/catch`**。外層那個雖然也會吞例外，但它會在 `AddAsync`
之前就中止 —— 計算器有 bug 會讓每一列用量都靜默消失，那比丟掉費用嚴重得多。
**用量比費用重要。**

⚠️ `IOptionsMonitor<AiPricingSettings>.CurrentValue` **每次呼叫只讀一次**並在整個計算中
沿用同一份。Blazor Server 的 DI scope 是 SignalR circuit，可以活好幾小時，設定隨時
可能熱更新；中途重讀會產生單價與匯率對不起來的快照。

### 4.2 設定結構

`appsettings.json` 的 `AiPricingSettings` 區段（完整鍵表見
[日誌與設定檔說明 §4.9](../operations/日誌與設定檔說明.md)）：

```jsonc
"AiPricingSettings": {
  "UsdToTwd": 31.5,
  "Models": {
    "gpt-6-astra": {
      "LongContextThresholdTokens": 272000,
      "Rates":            { "TextInputPerMillion": 10.0, "TextCachedInputPerMillion": 1.0, "TextOutputPerMillion": 50.0 },
      "LongContextRates": { "TextInputPerMillion": 20.0, "TextCachedInputPerMillion": 2.0, "TextOutputPerMillion": 75.0 }
    },
    "gpt-6-sol": {
      "LongContextThresholdTokens": 272000,
      "Rates":            { "TextInputPerMillion": 2.0, "TextCachedInputPerMillion": 0.2, "TextOutputPerMillion": 10.0 },
      "LongContextRates": { "TextInputPerMillion": 4.0, "TextCachedInputPerMillion": 0.4, "TextOutputPerMillion": 15.0 }
    },
    "gpt-6-luna": {
      "LongContextThresholdTokens": 272000,
      "Rates":            { "TextInputPerMillion": 0.1, "TextCachedInputPerMillion": 0.01, "TextOutputPerMillion": 0.5 },
      "LongContextRates": { "TextInputPerMillion": 0.2, "TextCachedInputPerMillion": 0.02, "TextOutputPerMillion": 0.75 }
    },
    "gpt-transcribe": { "Rates": { "AudioPerMinute": 0.0045 } }
  }
}
```

一組費率（`AiModelRates`）有**八個計費單位**，除 `AudioPerMinute` 外都是「每 1,000,000 單位」美金：

| 鍵 | 計費單位 |
| --- | --- |
| `TextInputPerMillion` / `TextCachedInputPerMillion` / `TextOutputPerMillion` | 文字 token |
| `ImageInputPerMillion` / `ImageCachedInputPerMillion` / `ImageOutputPerMillion` | 圖片 token |
| `AudioPerMinute` | 音訊**每分鐘**（不是每 1M） |
| `SpeechPerMillionCharacters` | 語音合成每 1,000,000 **字元** |

**省略某個鍵的語意刻意分成兩種**：

- 兩個 cached 費率省略 → **快取部分退回以對應的 input 費率計算（不打折）**，不是免費。
  寧可高估也不要靜默少算。
- 其餘費率省略 → 該計費單位不計費。
- 整組費率全省略或全為 0 → 視為未設定，該次呼叫記為「未定價」。

範本帶入十三個模型：`gpt-6-astra`／`gpt-6.1-sol`／`gpt-6-sol`／`gpt-6-luna`／`gpt-5.6-sol`／`gpt-5.6-terra`／`gpt-5.6-luna`（含長脈絡分級）、
`gpt-image-2.5-flare`／`gpt-image-2.5-sunburst`（文字＋圖片雙費率）、`gpt-transcribe`／`gpt-4o-transcribe-diarize`
（每分鐘，後者另有文字費率）、`tts-1`（每字元）、`text-embedding-3-large`（只有 input）。

### 4.3 模型比對規則

1. 模型名稱 `Trim()` 後**完全比對**（`OrdinalIgnoreCase`）。
2. 否則**受限前綴比對**：前綴命中後，剩餘後綴必須符合 `^-\d[\d-]*$`
   （連字號接數字，其後只有數字與連字號），取符合條件的**最長**鍵；
   長度相同時取字典序小的（決定性，帳目不可隨字典列舉順序而變）。
3. 都沒中 → 未定價。

| 模型名稱 | 結果 |
| --- | --- |
| `gpt-4o-2024-08-06` | ✔ 套用 `gpt-4o` |
| `gpt-3.5-turbo-0125` | ✔ 套用 `gpt-3.5-turbo` |
| `gpt-4o-mini` | ✘ 未定價 |
| `gpt-4.1` | ✘ 未定價（不會套用 `gpt-4`） |

### 4.4 用量拆解（子集晶格）

⚠️ **快取是輸入的折扣子集、推理已含在輸出內、圖片是同模態的子集。**
每一項只能被算一次，重複計算就是假帳。先夾正再拆解，因為上游偶爾會回出
「快取比輸入還多」這種數字，硬算會產生負數金額。

```
input         = max(0, InputCount ?? 0)
cached        = clamp(CachedInputCount ?? 0,      0, input)
imageIn       = clamp(ImageInputCount ?? 0,       0, input)
imageCachedIn = clamp(ImageCachedInputCount ?? 0, 0, min(imageIn, cached))

imageFreshIn  = imageIn - imageCachedIn
textCachedIn  = cached  - imageCachedIn
textFreshIn   = max(0, input - cached - imageFreshIn)

output  = max(0, OutputCount ?? 0)
imageOut = clamp(ImageOutputCount ?? 0, 0, output)
textOut  = output - imageOut
```

四項輸入拆解相加**必等於 `input`**，是一條可測的不變量。
**`ReasoningCount` 完全不參與計算** —— 它已經含在 `OutputCount` 裡。

### 4.5 費率選擇與算式

```
longContext = LongContextRates is not null
           && LongContextThresholdTokens > 0
           && input > LongContextThresholdTokens      // 嚴格大於，以扣快取「之前」的輸入數判斷

usd = textFreshIn   / 1e6 × 文字輸入費率
    + textCachedIn  / 1e6 × 文字快取費率（未設定則退回文字輸入費率）
    + textOut       / 1e6 × 文字輸出費率
    + imageFreshIn  / 1e6 × 圖片輸入費率
    + imageCachedIn / 1e6 × 圖片快取費率（未設定則退回圖片輸入費率）
    + imageOut      / 1e6 × 圖片輸出費率
    + seconds / 60        × 每分鐘費率
    + characters    / 1e6 × 每字元費率

twd = usd × UsdToTwd
```

**全程不做四捨五入**，只在顯示與匯出時格式化。

### 4.6 快照欄位

`TokenUsageLog` 的六個費用欄（詳見
[資料模型與資料庫](../architecture/資料模型與資料庫.md)）：

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| `CostUsd` / `CostTwd` | `double?`（SQLite `REAL`）| **`null` 就是「未定價」**；有費率但用量為 0 時存 `0` |
| `CostExchangeRate` | `double?` | 計算當下的匯率快照 |
| `CostPriceKey` | `string?` | 實際套用的費率鍵；與 `Model` 不同代表來自前綴比對 |
| `CostLongContext` | `bool` | 是否套用長脈絡費率 |
| `CostRateSnapshot` | `string?` | 生效費率組的精簡 JSON（只含非零項）|

**帳目可重現性不靠儲存的金額，而靠「單價快照 ＋ token 數」** —— 兩者都存了，
任何時候都能重算驗證。明細窗的「費用」區就是這個驗證的入口。

### 4.7 「未定價」的三種成因

1. **找不到費率設定** —— 模型名稱沒有完全比對也沒有合法前綴比對命中。
2. **匯率未設定** —— `UsdToTwd <= 0`，整個功能等於關閉（比照 `AiSettings.ApiKey` 留空即停用）。
3. **費率組是空的** —— 選中的那組八個費率全為 null 或 0。這是設定檔被寫壞、
   或 `IOptionsMonitor` 熱更新讀到半寫入 JSON 時的防線：寧可顯示「未定價」，
   也不要把一整批呼叫以 0 元寫成永久快照。

## 五、關鍵設計決定

### 5.1 費用在記錄當下算好並存進資料列，不是查詢時即時換算

日後調整單價或匯率**不會改變已經記錄的帳**；「篩選範圍的總花費」也變成一次 SQL `SUM`，
不必把資料撈進記憶體算。代價是欄位變多、且既有資料無法回補。

`CostTwd` 也是**逐列儲存**，不由美金總額乘匯率換算 —— 匯率會被調整，
篩選區間橫跨調整時只有逐列相加才是正確的台幣帳。

### 5.2 費用欄位用 `double`（SQLite `REAL`），不用 `decimal`

本功能的核心是在 SQL 端彙總，而 SQLite 把 `decimal` 存成 `TEXT`，無法可靠 `SUM`
（0.9.14 預留的 `EstimatedCost` 正是 `decimal?`，已於 0.9.17 移除）。
單筆金額量級在 1e-6，`double` 有 15–16 位有效數字，百萬列相對誤差仍在 1e-10 以下。
「money 要用 decimal」的通則在這裡不適用，因為可重現性由快照負責（見 4.6）。

### 5.3 圖片 token 定義為 `InputCount`／`OutputCount` 的**子集**，不是額外加項

供應商把圖片 token 算在 `prompt_tokens` 裡面。目前沒有寫入端，怎麼定都「對」；
但第一個從 `prompt_tokens_details.image_tokens` 填值的人一定會照抄供應商語意 ——
若當初定成加項，那一刻起每筆圖片呼叫都會**重複計費**，而且費用已經是寫死的快照，
回頭沒得修。定成子集則沿用既有的 `CachedInputCount`／`ReasoningCount` 慣例，
且在今天（全 null）退化成與純文字計費完全相同。

### 5.4 前綴比對限定日期／版本後綴

純前綴比對會讓 `gpt-4o-mini` 命中 `gpt-4o`（價差約 30 倍）、`gpt-4.1` 命中 `gpt-4`
（價差約 15 倍），而且**完全靜默**，錯誤金額還會被寫成永久快照。
**寧可看得到「沒算到」，也不要默默算錯** —— 要計價就補一筆設定，一行的事。

### 5.5 算不出來回 `null`（未定價），不是回 0

「找不到費率」與「有費率但這次用量是 0」在畫面上意義完全不同；
記成 0 會讓漏帳看起來像免費。合計卡另外顯示未定價筆數，讓漏帳的規模是可見的。

### 5.6 設定 POCO 放 `CustomerHelpAgent.Models` 而非 `CustomerHelpAgent.Web/Configuration`

計算發生在 Business 層的 `TokenUsageLogService`，而 Business **不相依 Web** ——
理由與 `SystemSettings` 相同。`AiSettings` 留在 Web 層不動。

## 六、權限與安全

- 費用能力沒有獨立權限鍵，寄生在「Token 用量」頁的**管理員專屬**守門之下。
- `CostRateSnapshot` 是 `TokenUsageLog`「只留可聚合的數值欄位」原則的**刻意例外**。
  它是**設定衍生資料**，不含任何使用者輸入或模型輸出，不踩
  「Token 用量絕不儲存提示詞與模型回應內文」那條紅線（完整內文 0.9.72 起只存在管理員專屬的
  [AI 對話紀錄](AI對話紀錄-prd.md)）。
  也**不能**改寫進 `RawUsageFile` 那個檔：那個檔可能是 `null`、而且會被
  「清除此日之前」獨立刪掉，帳就沒了。
- 計算器是純算術、無 I/O，已列入 `LoggingConventionTests` 的 logger 豁免清單。
- ⚠️ 新程式的日誌訊息一律用英文、佔位符不得含 `token` 字樣（`LoggingConventionTests` 禁用清單）。

## 七、錯誤與邊界

| 情境 | 行為 |
| --- | --- |
| 計算器拋例外 | 吞掉並記 `LogWarning`，**該列用量照常寫入**、費用留空 |
| 上游回出 `Cached > Input` 等髒資料 | 夾正後計算，不產生負數金額 |
| 模型名稱空白 | 未定價 |
| 設定檔熱更新讀到半寫入 JSON | 全零費率組視為未設定 → 未定價（見 4.7） |

### 已知限制（需要人為維護或日後補做）

1. **圖片 token 數與字元數目前沒有任何寫入端會填。** 系統的 LLM 呼叫點（`AiChatCompletionClient`、`AiHealthProbe`）只發 Chat 請求。
   欄位與費率先備齊，等日後接上圖片／TTS／轉錄 API 時就能直接填值。
2. **長脈絡門檻為 `272000` 輸入 token。** 官方規則是嚴格超過門檻後，整筆請求使用
   長脈絡費率；填 `0` 或省略 `LongContextRates` 即停用該模型的分級。
3. **不支援 cache-write 費率。** 供應商回傳的 `usage` 沒有 cache-write token 數，
   無法可靠判斷，硬猜只會算錯。
4. **匯率手動維護。** 沒有自動抓匯率的機制，也刻意不做（新增外部相依、需處理失敗與快取、
   離線環境會壞）。
5. **0.9.17 之前的紀錄一律維持「未定價」，不回補。** 當時沒有單價可快照，
   用今天的價補昨天的帳會違反快照的立意。
6. ⚠️ **Azure 部署名稱的坑。** `AiSettings.Model` 對 Azure 而言填的是**部署名稱**（任意字串），
   而 `TokenUsageLog.Model` 是「優先取回應的 `model`，空的才退回設定值」。成功的呼叫通常
   拿得到真實模型 id（前綴比對可用），但 **HTTP 非 2xx／逾時／傳輸失敗這三種分支根本沒有
   回應 body，必然退回部署名稱**，一律變成「未定價」。這不是 bug，但未定價筆數會比預期高。
   解法不需改程式 —— 直接把部署名稱當成 `Models` 的鍵再加一筆即可。
7. **費率數字由人工抄錄，不會自動更新。** 來源 <https://developers.openai.com/api/docs/pricing>，
   擷取日期 2026/09/17（`gpt-6-astra` 為 2026/09/22 補錄；`gpt-5.6-sol`、`gpt-5.6-terra` 於 2026/09/23 核對，`gpt-6-sol`、`gpt-6-luna` 同日補錄；2026/09/23 依 [AI 模型計費更新指南](../operations/AI模型計費更新指南.md) 全面核對十二個模型，並補錄 `gpt-image-2.5-sunburst`；`gpt-6.1-sol` 於 2026/09/30 補錄），請定期與實際帳單核對。`gpt-5.6-sol` 優惠價官方標示至少持續至 2026/11/21。範本數字由 `AiPricingSettingsTests` 釘住，
   改數字時測試會提醒同步更新文件。更新步驟見
   [AI 模型計費更新指南](../operations/AI模型計費更新指南.md)。
8. **明細表的費用欄需要橫向捲動才看得到**（見第三節「已知的呈現限制」）。

## 八、驗收與測試

| 測試 | 驗什麼 |
| --- | --- |
| `AiUsageCostCalculatorTests` | 子集晶格（快取／推理／圖片都不重複計，四項拆解相加等於輸入總數）、**受限前綴比對拒絕 `gpt-4o-mini` 與 `gpt-4.1`**、最長前綴優先且與設定順序無關、長脈絡門檻上下界、四種計費單位並存、快取費率缺漏退回原價、匯率未設定與全零費率回 `null`、髒資料不產生負數、費率快照序列化、`AiModelRates` 屬性數守門 |
| `AiPricingSettingsTests` | **遞迴**未知鍵守門（打錯費率鍵名會靜默變 0）、範本十三個模型的費率數字釘住、模型鍵唯一且全小寫、匯率為正、用真實 `appsettings.json` 走一次完整比對路徑 |
| `TokenUsageLogServiceTests` | 費用與快照寫入、未定價留空、**計算器拋例外時該列仍寫入**、費用加總與未定價計數、分組費用小計、依費用排序 |
| `TokenUsageReportPdfBuilderTests` | 全部有費用、以及**全部未定價**兩種情境都能產出 PDF |

⚠️ `AiModelRates` 的屬性數有測試釘住。**新增第九種計費單位時必須同時改四處**，
漏改會讓新單位靜默不計費：

1. `AiModelRates` 加費率屬性
2. `AiUsageCostCalculator.Compute` 加算式、`HasAnyRate` 加判斷
3. `AiUsageCostCalculator.BuildRateSnapshot` 加序列化項目
4. [日誌與設定檔說明 §4.9.2](../operations/日誌與設定檔說明.md) 的費率表與該守門測試的數字

若新單位還需要新的用量數字（例如圖片 token 數），另需加 `TokenUsageLog`／`TokenUsageEntry`
／`TokenUsageLogAdapterModel` 三個欄位與一份 migration。

### 手動驗收（0.9.17 實跑結果）

以真實 Azure OpenAI 呼叫驗證，模型 `gpt-5.6-sol-2026-07-09`：

- **費用手算比對**：輸入 7,184 × \$4/1M ＋ 輸出 2,664 × \$20/1M = **\$0.082016**，
  與系統記錄完全一致；推理 1,011 顆**沒有被重複計費**。
- **前綴比對生效**：`CostPriceKey` 記為 `gpt-5.6-sol`，明細窗顯示
  「以前綴比對自 `gpt-5.6-sol-2026-07-09`」。
- 台幣 0.082016 × 31.5 = **2.583504**，明細表顯示 `2.5835`。
- **快照語意**：把 `UsdToTwd` 改成 40（不重啟、走 `IOptionsMonitor` 熱更新）再查詢，
  已記錄的帳仍是 NT$ 2.58 —— 若是查詢時即時換算會變成 3.28。
- 0.9.17 之前的舊資料維持「未定價」，合計橫幅提示「其中 1 筆未定價未計入」。
- 匯出 CSV（2 列）與 PDF（121,146 bytes）皆成功，無字型或欄寬錯誤。
- migration 實際套用於含資料的資料庫：`EstimatedCost` 已移除、十個新欄位到位、
  `OccurredAt` 索引保留、既有資料列費用為 NULL。

⚠️ 套用 migration 時 EF 會警告 `PRAGMA foreign_keys = 0` **無法在交易內執行** ——
SQLite 的整表重建中途被中斷會停在部分套用的狀態。資料量小所以風險窗口極短，
但**正式環境套用前務必先備份 `BackendDB.db`**。

## 九、相關程式與文件

- `CustomerHelpAgent.Models/Systems/AiPricingSettings.cs` — 設定 POCO 與 `AiUsageCost`
- `CustomerHelpAgent.Business/Services/Other/IAiUsageCostCalculator.cs`、`AiUsageCostCalculator.cs`
  （`Resolve`／`Compute`／`BuildRateSnapshot` 皆為 `static` 純函式，測試可直接呼叫）
- `CustomerHelpAgent.Business/Services/DataAccess/TokenUsageLogService.cs` — 接線點（`RecordAsync`）
- `CustomerHelpAgent.AccessDatas/Models/TokenUsageLog.cs`、`Migrations/*_AddTokenUsageCost.cs`
- `CustomerHelpAgent.Web/Diagnostics/TokenUsageFormat.cs` — 金額與匯率的顯示格式
- `CustomerHelpAgent.Web/Components/Views/Analytics/TokenUsageView.razor(.cs)`、`TokenUsageGroupTable.razor`
- `CustomerHelpAgent.Web/Ai/TokenUsageReportPdfBuilder.cs` — PDF 的費用欄
- `CustomerHelpAgent.Web/appsettings.json` — `AiPricingSettings` 區段
- 交叉連結：[Token 用量 PRD](Token用量-prd.md)、
  [日誌與設定檔說明 §4.9](../operations/日誌與設定檔說明.md)、
  [開發慣例與限制速查 §6.7](../architecture/開發慣例與限制速查.md)、
  [資料模型與資料庫](../architecture/資料模型與資料庫.md)、
  changelog：LLM 呼叫費用計算（0.9.17）
