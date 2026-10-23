# AI 模型計費更新指南

- 文件版本：1.0
- 文件狀態：維護中
- 現行系統版本：1.0.0
- 首次實作版本：0.9.56
- 最後核對日期：2026/10/23

OpenAI 會不定期推出新模型、調整價格或淘汰舊模型，而 `appsettings.json` 的 `AiPricingSettings`
是**人工抄錄、不會自動更新**的。本文件是更新它的**唯一操作流程**：人可以照著做，也可以直接對
AI 助手說「照 `docs/operations/AI模型計費更新指南.md` 更新計費」。

⚠️ **為什麼一定要跟上**：費用在呼叫**當下**算好並存成快照，事後不回補
（見 [LLM 呼叫費用估算 PRD](../prd/LLM呼叫費用估算-prd.md) §5.1）。少一筆模型費率，
那段期間的呼叫就永遠是「未定價」；抄錯一個數字，錯的金額就永遠寫在帳上。

本文件只講**流程**。各設定鍵的語意、單位與比對規則以
[日誌與設定檔說明 §4.9](日誌與設定檔說明.md) 為權威，這裡不重寫。

---

## 1. 什麼時候要做

- **定期**：建議每月一次。
- **OpenAI 發表新模型或宣布調價時**。
- **系統要切換 `AiSettings.Model` 之前**：新模型沒有費率，切過去的第一筆呼叫就開始漏帳。
  注意範本的 `AiSettings.Model` 是空字串，實際值在部署環境（`appsettings.Production.json`
  或環境變數）裡，要看**實際部署的值**。
- `/token-usage` 出現大量「未定價」時（先排除 [§4.9.4 Azure 部署名稱](日誌與設定檔說明.md)
  這個已知成因）。

## 2. 資料來源與查詢順序

依下列順序查，**三個來源各有用途，不可互相取代**：

| 順序 | 網址 | 用途 |
|---|---|---|
| 1 | <https://developers.openai.com/api/docs/models/all> | **盤點**：有哪些模型、哪些被標為 **Deprecated**。⚠️ 這一頁**沒有價格** |
| 2 | `https://developers.openai.com/api/docs/models/<模型名稱>`（例如 `.../models/gpt-6-sol`）| **抄價格**：輸入／快取輸入／輸出單價，以及長脈絡門檻與長脈絡單價 |
| 3 | <https://developers.openai.com/api/docs/pricing> | **交叉核對**：與第 2 步抄到的數字比對 |

規則：

- **只抄 Standard（標準）價格**。Batch／Flex／Priority 等層級，以及寫入快取（cache-write）的
  費用都**不支援** —— 供應商回傳的 `usage` 分辨不出一次呼叫用的是哪一層
  （見 PRD §7 已知限制 #3 與 2026-09-22 changelog）。
- 價格單位一律是**美金**。除 `AudioPerMinute`（每分鐘）與 `SpeechPerMillionCharacters`（每 1M 字元）
  外，都是**每 1M token**。
- ⚠️ **第 2 步與第 3 步的數字對不上時，停下來問人**，不要自己挑一個。
- 使用 Azure OpenAI 時，實際價格以 Azure 帳單為準；OpenAI 官方價只是估算基準。

## 3. 盤點表

先把 `appsettings.json` 現有的每個鍵與 `models/all` 逐一比對，產出下表。
**改任何東西之前，先把這張表給人確認。**

| 模型鍵 | 系列 | models/all 狀態 | 目前 Rates（入／快取／出）| 官方 Rates | 長脈絡（門檻／入／快取／出）| 判定 |
|---|---|---|---|---|---|---|
| `gpt-6-sol` | GPT-6 | 現行 | 2.0／0.2／10.0 | 2.0／0.2／10.0 | 272000／4.0／0.4／15.0 | 價格不變 |
| `gpt-6-nova`（範例）| GPT-6 | 現行 | — | … | … | 新模型候選 |
| … | | | | | | |

「判定」只用這四種：**價格不變**、**調價**（情況 A）、**新模型候選**（情況 B）、
**Deprecated**（情況 C）。

**收錄原則**（哪些新模型要加）：

- 費率表裡**已有的系列**出了新版（例如 GPT-6 多一個型號、`gpt-image` 出新版）→ 列為候選。
- **`AiSettings.Model` 實際在用的模型 → 一定要有費率**，不論系列。
- 其他系列（例如 Realtime、HD 版 TTS）→ 在表下另列一行「未收錄的其他模型」，**讓人決定**，不自動加。

## 4. 情況 A：既有模型調價

1. 修改 `src/CustomerHelpAgent/CustomerHelpAgent.Web/appsettings.json` 中該模型的 `Rates`
   （以及有分級時的 `LongContextRates`、`LongContextThresholdTokens`）。
   - 欄位名稱與單位見 [§4.9.2](日誌與設定檔說明.md)。⚠️ 打錯鍵名會被
     `AppSettingsSection_ShouldOnlyContainKnownKeys` 擋下；**不要**為了讓測試過而刪掉那個鍵。
   - 兩個 cached 費率若官方沒有列出，**省略**而不是填 0（省略＝以原價計；填 0＝免費）。
2. 同步 `src/CustomerHelpAgent/CustomerHelpAgent.Tests/AiPricingSettingsTests.cs` 中**固定數字**的三支測試，
   數字要與 JSON 完全一致：
   - `AppSettings_ShouldPinFlagshipTextRates`：文字模型的 `Rates`（入／快取／出），一個模型一列 `InlineData`
   - `AppSettings_ShouldPinLongContextRates`：長脈絡門檻與 `LongContextRates`，一個模型一列 `InlineData`
   - `AppSettings_ShouldPinNonTextBillingUnits`：圖片、轉錄、語音、嵌入模型。⚠️ 這支**不是** `InlineData`，
     是逐行 `Assert.Equal(…, models["<鍵>"].Rates.<欄位>)`，照同一個模型既有的寫法增減
3. 更新擷取日期（兩處，文字相同）：
   - [日誌與設定檔說明 §4.9.5](日誌與設定檔說明.md)「擷取日期」那一行
   - [LLM 呼叫費用估算 PRD](../prd/LLM呼叫費用估算-prd.md) §7 已知限制 #7
4. 若 §4.9 的 jsonc 範例裡正好有這個模型，範例的數字也要一起改。

## 5. 情況 B：新增模型

1. 在 `appsettings.json` 的 `Models` 加一個鍵：
   - ⚠️ **全小寫、不帶日期後綴**（`gpt-6-nova`，不是 `gpt-6-nova-2026-10-01`）。
     帶日期的鍵在供應商換版後就永遠比對不到，理由見
     [速查表 §6.10](../architecture/開發慣例與限制速查.md)。
     `AppSettings_ModelKeys_ShouldBeTrimmedLowercaseAndUnique` 會擋大寫與重複。
   - 帶日期的回應名稱（例如 `gpt-6-nova-2026-10-01`）會經由受限前綴比對自動套到 `gpt-6-nova`；
     但 `-mini`、`-pro` 這類**型號後綴不會**，必須各自有一個鍵
     （[§4.9.3](日誌與設定檔說明.md)）。
   - ⚠️ **絕不要從 `/token-usage` 的「模型」欄抄名稱** —— 那裡顯示的是回應的帶日期名稱。
   - 放的位置：同系列由高階到低階，與現有排列一致。
   - ⚠️ `RealAppSettings_ShouldResolveModelsAsDocumented` 斷言 `gpt-5.6-sol-mini` 與
     `my-azure-deployment` **比對不到**。若真的要新增這兩個鍵，那支測試要改用別的反例，
     不可直接刪掉 —— 它守的是「型號後綴不會被父模型吃掉」。
2. 在第 4 節提到的測試補上對應的斷言（文字模型加前兩支的 `InlineData`，非文字模型在第三支加 `Assert` 行）。
3. 模型總數與模型清單寫在多份文件裡，要跟著改。**不要只信下面的清單，先 grep**
   （以目前的數字為例）：`grep -rn -E "十三個模型|13 個模型" docs/`，並找出列舉模型名稱的段落。
   2026/09/30 時共五處：
   - [日誌與設定檔說明 §4.9.5](日誌與設定檔說明.md)：「範本內十三個模型的費率」
   - [LLM 呼叫費用估算 PRD](../prd/LLM呼叫費用估算-prd.md) §4.2：「範本帶入十三個模型：…」**這一段逐一列出模型名稱**，新模型要加進去
   - 同一份 PRD §8：「範本十三個模型的費率數字釘住」
   - [腳手架開發指引](../guides/腳手架開發指引.md)：`AiPricingSettings:Models` 那一列的「13 個模型」，以及文件後段「含 13 個模型的費率」
4. 更新第 4 節第 3 步的擷取日期，寫明「某模型於某日補錄」。
5. 若是新的**旗艦**系列，判斷 §4.9 的 jsonc 範例是否要換成新模型。

## 6. 情況 C：模型被標為 Deprecated

**先留著，確定沒在用才刪。**

1. 到 `/token-usage`，起訖日設成近 30 天，看「依模型」頁籤裡這個模型（含它帶日期的名稱）
   還有沒有呼叫。也確認 `AiSettings.Model` 的實際部署值不是它。
2. **還有呼叫 → 保留**，在 changelog 記一筆「已被標為 Deprecated，仍在使用，暫不移除」。
3. **近 30 天沒有呼叫 → 移除**：刪掉 `appsettings.json` 的鍵、刪掉測試裡對應的 `InlineData`，
   並依第 5 節第 3 步把模型總數減一。
   - 若 `RealAppSettings_ShouldResolveModelsAsDocumented` 用到了這個鍵，要換成另一個仍存在的模型。

移除的影響：

- **舊紀錄不受影響** —— 費用是快照，早就存在資料列上。
- 若其實還有地方在呼叫，**之後的呼叫會變成「未定價」**，而且不回補。這就是為什麼要先看用量。

## 7. 情況 D：計算方式改變

⚠️ **這類變更一律先停下來與人討論**，不要自行改程式。

- **長脈絡門檻改了**（目前 `272000`，輸入 token **嚴格大於**才套用）：
  改該模型的 `LongContextThresholdTokens`；`AppSettings_ShouldPinLongContextRates` 裡寫死了
  `272000`，要一併修改。並更新 §4.9.5 與 PRD §7 #2 的門檻說明。
- **某模型取消或新增長脈絡分級**：省略 `LongContextRates`（或門檻填 `0`）即停用分級。
- **出現新的計費單位**（例如一種全新的 token 類別）：這要改程式，必須同時改四處，
  見 [速查表 §6.7「新增一種計費單位要同時改四處」](../architecture/開發慣例與限制速查.md)。
  `AiUsageCostCalculatorTests` 釘住了 `AiModelRates` 的屬性數（目前是 `8`），漏改會被擋下。
- **計價規則本身改了**（例如快取不再是輸入的子集）：影響
  [速查表 §6.7 的費用紅線](../architecture/開發慣例與限制速查.md)，必須先討論。

## 8. 選擇性：匯率 `UsdToTwd`

匯率與 OpenAI 頁面無關，**不必每次都改**；需要時再做。

- 目前值：`31.5`（`AiPricingSettings.UsdToTwd`）。
- 可參考臺灣銀行牌告匯率 <https://rate.bot.com.tw/xrt> 的美金即期賣出價，或依公司財務規定。
- 改了**只影響之後的紀錄**；舊紀錄的台幣金額是用當時的匯率算的，不會變。
  區間跨越匯率調整時，美金與台幣的合計彼此換算不回去，這是預期行為。
- ⚠️ **不要設成 0 或負數** —— 那代表「未設定」，之後的每一筆呼叫都會變成未定價。

## 9. 收尾檢查表

- [ ] 盤點表已經過人確認
- [ ] `appsettings.json` 與 `AiPricingSettingsTests` 的 `InlineData` 數字一致
- [ ] `dotnet test` 全部通過（特別是 `AiPricingSettingsTests`、`AiUsageCostCalculatorTests`）
- [ ] 擷取日期已更新（§4.9.5、PRD §7 #7）；有增減模型時模型總數與清單也已更新（見第 5 節第 3 步）
- [ ] `/system-health` 的「AI 計費表」檢查項仍是正常（它會檢查匯率大於 0，以及目前的
      `AiSettings.Model` 找得到費率）
- [ ] 新增一篇 `docs/changelog/YYYY-MM-DD-<主題>.md`，並在 `docs/changelog/README.md` 與
      `docs/README.md` 加一條
- [ ] `appsettings.json` 的 `SystemVersion` Patch +1
- [ ] 修改過的文件表頭「現行系統版本」「最後核對日期」已更新
- [ ] `pwsh scripts/Test-DocsEncoding.ps1` 通過（`docs/` 下 `.md` 必須 UTF-8 含 BOM）
- [ ] **不自動 commit**，由人確認後再提交

即使盤點結果**完全沒有變動**，也要更新擷取日期，記錄「某日已核對」。

## 10. 給 AI 助手的執行守則

1. **先盤點、後修改**：抓完三個來源後，先輸出第 3 節的盤點表，**等人確認**才動檔案。
2. **數字對不上就停**：模型頁與 pricing 頁不一致、頁面寫法模稜兩可（例如沒寫門檻是否嚴格大於）、
   或單位看不懂時，列出原文請人判斷。
3. **不自動新增其他系列的模型**，不自動刪除 Deprecated 模型 —— 刪除前必須有第 6 節的用量確認。
4. **絕不從 `/token-usage` 抄帶日期的模型名**當成設定鍵。
5. **遇到情況 D 不改程式**，先說明影響再討論。
6. 發現本文件的步驟有漏洞或過時，一併修正本文件。

---

相關文件：

- [日誌與設定檔說明 §4.9](日誌與設定檔說明.md)：`AiPricingSettings` 各鍵的權威說明
- [LLM 呼叫費用估算 PRD](../prd/LLM呼叫費用估算-prd.md)：計算規則、快照、已知限制
- [開發慣例與限制速查 §6.7、§6.10](../architecture/開發慣例與限制速查.md)：費用紅線、模型名稱不帶日期
  新增 GPT-6 Sol 與 Luna 定價

> 返回 [operations 索引](README.md)
