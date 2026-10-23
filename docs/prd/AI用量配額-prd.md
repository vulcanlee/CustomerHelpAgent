# AI 用量配額 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.109
- 最後核對日期：2026/10/23

## 一、目標與範圍

改動前「Token 用量」只記帳、不限制，任何人都可以無限次呼叫 AI。0.9.109 起可以設定全系統與每人的每日、每月新台幣上限，達到上限時不再送出 AI 呼叫，並在 80%、100% 時通知（路線圖 F-18）。

使用者決定（2026/10/04）：

| 題目 | 決定 |
|---|---|
| 上限的對象 | 全系統總額＋每人上限 |
| 單位 | 新台幣金額（以「Token 用量」記錄的台幣費用加總） |
| 期間 | 每日＋每月都設 |
| 健康檢測 | 記帳但不擋 |
| 通知 | 每人上限通知本人；全系統上限通知全體管理員 |

- **非範圍**：依角色或部門的上限、預先保留額度（送出前估算費用）、管理員豁免、讓使用者自行查詢剩餘額度的頁面。

## 二、使用者與入口

| 角色 | 入口 | 看到什麼 |
|---|---|---|
| 管理員 | 「系統參數」→ 分類「AI 用量」 | 四個上限（0＝不限制），修改立即生效 |
| 管理員 | 「Token 用量」上方「AI 用量上限」 | 全系統今日、本月已用 vs 上限；每人上限的設定值 |
| 使用 AI 分析的人 | 日誌檢視、系統例外紀錄的 AI 分析 | 被擋下時的原因與重置時間；鈴鐺收到 80%／100% 通知 |
| 管理員 | 鈴鐺、信箱 | 全系統 80%／100% 通知；全系統達 100% 另寄信 |

## 三、畫面與欄位

- 系統參數「AI 用量」：全系統每日上限、全系統每月上限、每人每日上限、每人每月上限（單位「元（新台幣）」，0–100000000，0＝不限制）。
- Token 用量頁：「AI 用量上限　—　在「系統參數」的「AI 用量」調整；未定價的呼叫以 0 計」；每一項「全系統每日：今日 NT$ 252／NT$ 300」，達 80% 顯示「已達 80%」、達上限顯示「已達上限」；另列每人每日、每月上限。
- 被擋下的訊息：「已達每人每日 AI 用量上限（NT$ 150，今日已用 NT$ 189），2026-10-05 00:00 重置。需要提高上限請洽系統管理員（系統參數「AI 用量」）。」
- 通知：標題「AI 用量已達上限的 80%：每人每日」或「AI 用量已達上限：全系統每月」；內容「今日已用 NT$ 126／上限 NT$ 150。達到上限後你的（所有人的）AI 分析會暫停，… 重置。」；全系統的通知連到 `/token-usage`。

## 四、內部系統運作

- **設定**：`AiQuotaSettings`（`CustomerHelpAgent.Models/Systems`；`GlobalDailyTwd`、`GlobalMonthlyTwd`、`PerUserDailyTwd`、`PerUserMonthlyTwd`，int、`[Range(0, 100000000)]`），以 `IOptionsMonitor` 讀取，是系統參數。
- **計算**：`AiQuotaService`（Business，`IDbContextFactory`）。期間以伺服器本地時間的今天 00:00～明天 00:00、本月 1 日 00:00～下月 1 日 00:00（`TimeProvider`）；`TokenUsageLog.OccurredAt` 存的就是本地時間。加總 `CostTwd`，未定價（空值）以 0 計。每人加總吃新索引 (`UserId`, `OccurredAt`)（migration `AddTokenUsageUserIndex`）。只計算有設定的上限，四個都是 0 時不查資料庫。
- **攔截**：`AiChatCompletionClient.CompleteAsync` 在設定檢查之後、送出之前呼叫 `CheckAsync(目前使用者)`。已達到（含等於）任一上限 → 不送出、不記用量、回 `AiAnalysisFailureReason.QuotaExceeded`、寫稽核 `Ai.QuotaBlocked`（目標 `AiQuota#User:Daily`，detail `operation=…; limit=…; used=…`）、記警告日誌。同時達到多個時回報**最晚才重置**的那一個。讀不到用量時寫錯誤日誌並放行。
  AI 日誌分析、AI 例外分析（每一輪追問）都經這個 client；系統健康監控的 AI 連線檢查（`AiHealthProbe`）不經它，所以不會被擋。
- **提醒**：`TokenUsageLogService.RecordAsync` 寫入一筆有台幣費用的用量後呼叫 `NotifyIfReachedAsync(使用者)`（健康檢測也是）。每個有設定的上限：達 100% 發 100% 的通知，否則達 80% 發 80% 的通知；去重鍵 `AiQuota:{global|user:{Id}}:{daily:yyyy-MM-dd|monthly:yyyy-MM}:{80|100}`，同一期間同一門檻只通知一次（`NotificationSender` 依去重鍵略過**現存通知裡**已有同鍵的人；通知被刪除後會再發，見 §六）。每人 → 本人；全系統 → 全體管理員，達 100% 同時寄信。通知分類 `AiQuota`。
- **並行**：同時送出的呼叫都通過檢查後才記帳，已用金額可能小幅超過上限（已接受）；下一次呼叫就會被擋。

## 五、權限與安全

- 上限只有管理員能改（系統參數頁）；修改寫 `SystemParameter.Update` 稽核。
- 管理員本身也受每人上限限制（沒有豁免）；要緊急解除就把上限調高或設 0，立即生效。
- 被擋下的呼叫沒有送出任何內容給 AI 服務。

## 六、錯誤與邊界

| 情況 | 行為 |
|---|---|
| 上限 0 | 不限制 |
| 已用剛好等於上限 | 視為已達上限，擋下 |
| 模型未定價 | 費用以 0 計，不會讓人被擋；Token 用量頁照樣提示「未定價」 |
| 沒有登入者的呼叫（使用者 Id 為 0） | 只看全系統上限 |
| 跨午夜、月初 | 依伺服器本地時間重置 |
| 讀不到用量（資料庫錯誤） | 放行並寫錯誤日誌 |
| 通知失敗 | 不影響用量記錄（`NotifyIfReachedAsync` 不丟例外） |
| 收件人刪掉了那則通知（鈴鐺 ×、「清除已讀」，或已讀滿 `NotificationSettings:ReadRetentionDays` 天被排程清除） | 0.9.136 起不會再收到：去重看 `NotificationDispatch`，不看現存的通知（0.9.125～0.9.135 會在同一期間再收到一次，全系統 100% 還會重寄信） |

## 七、驗收與測試

- `AiQuotaTests`：今天從本地午夜起算、本月從 1 日起算；四個上限各自在達到時擋下；0＝不限、未定價以 0 計；每人只算本人、全系統算所有人；同時達到時回報最晚重置的；調整立即生效；讀不到資料庫時放行；80%／100% 的對象、去重鍵與寄信；⭐ 經真正的通知服務，同一門檻同一期間只通知一次、隔天重新計算；⭐ 已達上限時 client 不送出、不記用量、寫稽核；沒有達到時照常送出；只有有費用的呼叫才評估提醒（健康檢測也計入）。

## 八、相關程式與文件

- `CustomerHelpAgent.Models/Systems/AiQuotaSettings.cs`、`CustomerHelpAgent.Business/Services/Other/AiQuotaService.cs`
- `CustomerHelpAgent.Web/Ai/AiChatCompletionClient.cs`（攔截）、`CustomerHelpAgent.Business/Services/DataAccess/TokenUsageLogService.cs`（提醒）
- `CustomerHelpAgent.Web/Configuration/Parameters/SystemParameterCatalog.cs`（分類「AI 用量」）、`Components/Views/Analytics/TokenUsageView.razor(.cs/.css)`
- migration `AddTokenUsageUserIndex`
- 交叉連結：[Token 用量](Token用量-prd.md)、[系統參數](系統參數-prd.md)、[站內通知與公告](站內通知與公告-prd.md)、[日誌與設定檔說明](../operations/日誌與設定檔說明.md)
