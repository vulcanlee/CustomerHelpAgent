# 日誌等級設定 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.4.29
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供管理員在**執行期**調整 NLog 最低輸出等級的頁面（`/log-level-setting`），排查問題時可臨時調到 Debug／Trace，查完再調回來，全程不需要重新啟動，也不需要異動任何設定檔。

在此之前，唯一的辦法是編輯 `nlog.config` 的 `<logger name="*"> minlevel` —— 那是一個會進版控、且可能被誤留在 Debug 的檔案異動。

- 範圍：兩張狀態卡片、行為說明橫幅、六級單選、套用與還原兩個動作、稽核紀錄。
- 非範圍：寫回設定檔、個別 logger 的細部調整、框架日誌（Microsoft／EntityFrameworkCore）的等級調整、排程自動還原。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/log-level-setting` | 系統管理 › 監控與診斷 › 日誌等級設定 | 已登入且為管理員（`IsAdmin`） | 系統管理員／維運 |

權限沿用同子功能表的管理員專屬機制：`MagicObjectHelper.角色_日誌等級設定` 有定義並登記於 `SidebarMenuService.MenuPermissionMap`（id 63），但**刻意未列入** `RolePermissionService.GetRoleListPermissionAllName()`。`CustomerHelpAgent.Tests/AdminOnlyPermissionTests.cs` 會擋下日後「順手補齊」的修改。

## 三、畫面與欄位

**兩張卡片**

| 卡片 | 內容 | 副標 |
| --- | --- | --- |
| 目前生效等級 | 實際生效的最低等級 | 此等級以上的日誌才會寫入檔案 |
| 系統預設等級 | `nlog.config` 檔案裡的值 | 來自 nlog.config 的 `<logger name="*">` minlevel |

未套用執行期設定時兩者相同；套用後「目前生效等級」會與「系統預設等級」分歧。

**行為說明橫幅**（黃色）說明四件事：只在執行期有效、套用後可存活 nlog.config 重載、只影響應用程式日誌、調低會大幅增加日誌量。

**六級單選**（NLog 命名，與日誌檔內實際字串及日誌檢視頁的篩選下拉一致）：

| 等級 | 說明 |
| --- | --- |
| TRACE | 最詳細，含框架層級細節 |
| DEBUG | 含查詢條件、換頁等排錯資訊 |
| INFO | 含使用者操作軌跡，日常建議值 |
| WARN | 僅警告以上 |
| ERROR | 僅錯誤以上 |
| FATAL | 僅嚴重錯誤 |

**兩個動作**：「套用」（選擇未變更時停用）與「還原為系統預設等級」（未套用過時停用）。調到 TRACE／DEBUG 時會先跳確認框，其餘等級直接套用。

> **降級狀態**：當 `logLevelState.IsAvailable == false`（讀不到 `nlog.config`，或其中沒有
> `<logger name="*">` 規則）時，畫面改顯示紅色錯誤區塊並停用所有調整動作，
> 提示「無法讀取 NLog 設定，日誌等級無法在執行期調整」。

## 四、內部系統運作

1. `LogLevelSettingView.OnInitializedAsync`：`Check` → `CheckIsAdmin`；非管理員設定 `RoleMessage`、寫一筆 `Permission.Denied` 稽核（目標 `/log-level-setting`）並中止，**權限通過前不讀取任何設定資訊**。
2. `LogLevelRuntimeState`（Singleton）於**應用程式啟動時**（`Program.cs`，`app.Build()` 之後）呼叫 `Initialize()`：捕捉當下 `*` 規則的 minlevel 作為系統預設等級，並訂閱 `LogManager.ConfigurationChanged`。
3. 套用時修改 `*` 規則的等級範圍並呼叫 `LogManager.ReconfigExistingLoggers()`。
4. 變更寫入 `AuditLog`（action 為 `LogLevel.Apply` / `LogLevel.Restore`，detail 含前後等級）。

## 五、限制與已知取捨

- **只影響 `<logger name="*">` 規則**。`nlog.config` 前面幾條 `Microsoft.*`／`System.*` 規則設了 `final="true"`，因此：
  - **調低**到 DEBUG／TRACE 只會讓應用程式日誌變多，框架日誌被前面的規則攔掉 —— 這是本功能安全的關鍵。此性質由 `LogLevelRuntimeStateTests.LoweringLevel_AgainstRealConfig_ShouldOnlyAffectApplicationLoggers` **直接對真實的 nlog.config** 驗證。
  - **調高**到 ERROR／FATAL **無法靜音** `Microsoft.Hosting.Lifetime`（Info）與 Microsoft／EF 的 Warn。這個不對稱是刻意保留的。
- **「強制排除」規則的三個條件缺一不可**：涵蓋所有等級、`final="true"`、**且沒有 target**。少了任何一項，Microsoft 的 Debug 訊息就會因不符合上面 Warn 規則的等級範圍而繼續往下比對，最後被萬用規則收走。實作測試時曾因合成設定漏掉「沒有 target」這項而誤判。
- **`ReconfigExistingLoggers()` 是必要的**：不呼叫的話，已建立的 Logger 會沿用快取的等級過濾器，設定等於沒生效 —— 而執行中的應用程式，每一個 Logger 都是已建立的。
- **不寫回任何設定檔**：重新啟動就會回到系統預設等級。這是刻意的，避免有人把 Debug 誤留在版控裡。
- **`appsettings.json` 的 `Logging:LogLevel` 是死設定**，改它不會有任何效果 —— `builder.Host.UseNLog()` 預設 `RemoveLoggerFactoryFilter: true`，NLog 會接管全部過濾。這一節很容易誤導人，特此記錄。
- **`LogLevelRuntimeState` 是本專案第一個可變的 Singleton**（既有的 `SystemStartupState` 是不可變的），因此鎖的責任完全在它自己：`LoggingRules` 是普通的 `IList`，改動發生在 Blazor circuit 執行緒上，而其他執行緒同時正在寫日誌。
- 多個管理員同時操作時最後套用者勝出；每次進頁都重讀真實狀態，不快取。

## 六、與 autoReload 的互動

`nlog.config` 設了 `autoReload="true"`，NLog 重載時會**整個換掉 `LogManager.Configuration`**。`LogLevelRuntimeState` 訂閱 `ConfigurationChanged` 並依序處理：

1. 先把新設定裡 `*` 規則的值讀進「系統預設等級」（此時反映檔案最新內容）。
2. 補回 `BasePath`／`LogFilenamePrefix` 兩個 NLog 變數。
3. 若有執行期覆寫，重新套用。

第 2 步修的是**既有缺陷**：那兩個變數由 `Program.cs` 在啟動時注入，重載時會被清空，導致 `fileName="${var:BasePath}/..."` 的路徑變成空字串、**日誌從此寫到磁碟根目錄**而非設定的目錄。0.4.29 之前只要有人在執行中存檔 `nlog.config` 就會踩到。

因為第 3 步的存在，**套用執行期設定後，直接改 `nlog.config` 的 minlevel 不會立即生效**（會被覆寫蓋過）——要回到檔案的值請按「還原為系統預設等級」。畫面上的說明橫幅已載明此行為。

## 七、驗收與測試

對應測試檔 `CustomerHelpAgent.Tests/LogLevelRuntimeStateTests.cs`（10 支）：涵蓋等級升降、還原預設、
`IsAvailable` 判定，以及 `LoweringLevel_AgainstRealConfig_ShouldOnlyAffectApplicationLoggers`
（調低等級只影響應用程式 logger，不會連帶打開框架的雜訊）。

> 該測試檔標記 `[CollectionDefinition(..., DisableParallelization = true)]`——
> 它會動到真實的 `nlog.config`，不能與其他測試平行執行。

---

## 八、相關文件

- 同一子功能表：`docs/prd/日誌檢視-prd.md`、`docs/prd/資料庫用量-prd.md`
- 日誌檔位置與 nlog.config 設定：`docs/operations/日誌與設定檔說明.md`
- 選單與權限機制：`docs/prd/首頁與導覽-prd.md`

> 返回 [prd 索引](README.md)
