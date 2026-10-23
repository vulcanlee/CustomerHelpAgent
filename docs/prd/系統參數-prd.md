# 系統參數 PRD

- 文件版本：1.7
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.98
- 最後核對日期：2026/10/23

## 一、目標與範圍

讓管理員在畫面上修改**非機密、執行期可改**的參數（系統名稱、保留天數、監控與告警門檻、流量與忘記密碼的限制），
**存檔後不需重啟即生效**，每次修改寫稽核（舊值 → 新值），而且參數只有一個定義來源。

- **範圍**：資料庫覆寫層（`SystemParameter` 表＋設定來源）、參數目錄、驗證、跨行程同步、管理頁 `/system-parameters`、
  系統名稱改為即時讀取（`ISystemIdentity`）、忘記密碼設定改為即時讀取、限流配額熱更新。
- **非範圍**：~~登入鎖定次數／時間與密碼長度（當時規劃另案加進目錄）~~（0.9.101 已加入「密碼與登入」分類）；機密、路徑、版本號、排程 cron、告警收件人等陣列與字典；
  啟動時就被讀走的值（Cookie、JWT、CORS、快取、Google 登入）。

使用者決定（2026/10/04）：資料庫**只存覆寫值**、疊在設定檔之上；第一批開放保留天數、系統名稱與簡介、監控門檻與頻率；頁面**只限管理員**；一次做完。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/system-parameters` | 系統管理 → 系統設定與維運 → 系統參數（`Menu.json` id=36）| 僅管理員（`CheckIsAdmin`；權限鍵 `角色_系統參數` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- 依「系統識別／資料保留／監控與告警／安全與流量」分表（之後加入「備份」「密碼與登入」「AI 用量」，現行共七類）：參數（名稱＋設定鍵）、目前值（＋來源標籤：系統參數／設定檔／未套用與原因、最後修改者與時間）、
  設定檔的值、說明（用途、範圍、生效時機）、操作（修改、還原為設定檔值）。
- 修改窗（小型表單，不用 `EditForm`）：依型別數字框／開關／文字框；伺服器驗證訊息顯示在窗內；縮短保留天數（含 0 → 正數）先跳危險確認。
- 頁首提示：設定檔本身有誤的區段（不能存檔）、未套用的覆寫數量、資料庫裡不認得的鍵（可移除）。
- 工具列說明文字（0.9.119 補完整）：立即生效與跨主機同步；沒改過的沿用設定檔；改過的以頁面為準，設定檔或環境變數再改也不生效，要「還原為設定檔值」；不在頁面的設定改設定檔。
  操作說明列出「只能改伺服器設定檔」的完整區段表（與 [日誌與設定檔說明 §4.15](../operations/日誌與設定檔說明.md) 同一份）。
- 稽核：`SystemParameter.Update`／`SystemParameter.Reset`，Detail `key=…; old=…(來源); new=…(來源)`（值超過 200 字截斷）。

## 四、內部系統運作

- **資料**：`SystemParameter`（主鍵 `ParameterKey`＝完整設定鍵、`Value`、`ConcurrencyStamp`、時間與修改者）。沒有列＝沿用設定檔。Migration `AddSystemParameters`，不需種子資料。
- **設定來源**：`SystemParameterConfigurationProvider`（被動）在 `Program.cs` 緊接 `CreateBuilder` 之後加入 —— 最後加入的來源優先權最高，
  所以覆寫蓋過 appsettings、環境變數與命令列。資料庫初始化之後由 `SystemParameterRuntime.LoadAsync` 載入，之後每次存檔與每分鐘（`SystemParameterRefreshWorker`）重新套用；
  內容沒變不觸發 reload。
- **目錄**：`SystemParameterCatalog.All`（0.9.98 為 23 個鍵；0.9.99 加備份 2 個、0.9.100 加站內通知 1 個、0.9.101 加密碼與登入 9 個、0.9.104 加兩步驟驗證 2 個、0.9.109 加 AI 用量 4 個、0.9.125 加已讀站內通知保留天數 1 個，共 42 個）是唯一定義：分類、名稱、說明、型別、單位、0 的意義、範圍（讀 `[Range]`，驗證器裡的範圍明列）、生效時機。
  不在目錄的鍵不會套用（安全邊界）。
- **驗證**：`SystemParameterValidator` 把「設定檔的值＋候選覆寫」組成記憶體設定，交給真的 options 管線與 DI 裡所有 `IValidateOptions<T>`。
  存檔：格式（整數、布林、文字不含控制字元與長度）→ 設定檔本身是否合法 → 候選是否合法 → 寫入（條件式 UPDATE 比對版本號）→ 立即重新套用。
  新值等於設定檔的值時改為還原。
- **載入**：每個區段依目錄順序逐一加入覆寫、每加一個驗證一次；不合法者略過並記錯誤（同一問題只記一次）、頁面標「未套用」；設定檔本身有誤的區段維持現有覆寫。
- **即時讀取**：目錄內的設定類別一律 `IOptionsMonitor<T>.CurrentValue`；系統名稱與簡介只經 `ISystemIdentity`（14 處改寫，含 singleton 的例外告警服務；
  設定被改壞時回傳上一次的正確值，不注入 `ILogger`）；忘記密碼服務與頁面改用 `IOptionsMonitor`；限流分割鍵加上配額，改了配額換新計數器。

| 分類 | 鍵 | 生效 |
|---|---|---|
| 系統識別 | `SystemName`、`SystemDescription` | 信件與 PDF 立即；已開啟的畫面換頁後 |
| 資料保留 | 例外／稽核／Token 用量／AI 對話紀錄保留天數、記錄 AI 對話內容、已刪除資料保留天數、站內通知保留天數（0.9.100）、已讀站內通知保留天數（0.9.125）、排程執行紀錄保留天數 | 下一次排程清理（頁面的清除按鈕立即；排程執行紀錄在下一次任何排程作業執行完時；記錄 AI 對話內容下一次 AI 呼叫起） |
| 監控與告警 | 四個慢操作門檻、例外告警四個數值、瀏覽器錯誤回報開關與上限 | 立即（瀏覽器錯誤回報「開啟」只對之後載入的頁面） |
| 安全與流量 | API／登入每分鐘配額、重設密碼連結有效時間與申請冷卻 | 立即（連結有效時間只影響之後寄出的） |
| 備份（0.9.99）| 備份保留份數、是否備份 AI 對話內容 | 下一次備份 |
| AI 用量（0.9.109）| 全系統每日／每月上限、每人每日／每月上限（新台幣，0＝不限制），見 [AI 用量配額](AI用量配額-prd.md) | 下一次 AI 呼叫起 |
| 密碼與登入（0.9.101）| 密碼最少字元數、須含英文字母／數字／大寫字母／符號、不可重複最近幾次、密碼有效天數、連續輸錯幾次鎖定、鎖定時間；0.9.104 加管理員必須使用兩步驟驗證、記住裝置天數 | 規則只影響之後設定的密碼；有效天數與兩步驟驗證強制立即（下一次換頁）；鎖定下一次登入嘗試起；記住裝置天數影響之後勾選的裝置，改成 0 時既有標記也不再採用 |

完整鍵表見 [日誌與設定檔說明 §4.15](../operations/日誌與設定檔說明.md)。

## 五、權限與安全

- 管理員專屬；權限鍵不進角色矩陣（`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`）。
- 機密與基礎設施鍵不在目錄（測試守門：白名單區段、屬性名稱不含 password／secret／key／connection／path／recipients、版本號不在目錄）；
  資料庫裡出現非目錄鍵也不會套用。
- 每次修改與還原寫稽核；縮短保留天數有危險確認（下一次排程會永久刪除資料）。

## 六、錯誤與邊界

- 兩位管理員同時修改同一個鍵：第二位得到並行衝突訊息（新增撞主鍵、或版本號不符）。
- 設定檔被改壞：該區段拒絕存檔並列出設定檔的錯誤；已套用的覆寫保留；`ISystemIdentity` 回傳上一次的正確名稱。
- 升級後範圍變窄：舊覆寫略過、標「未套用」、啟動不失敗。
- 讀不到資料庫：啟動時讓啟動失敗（與資料庫初始化一致）；執行中刷新失敗維持現有設定、連續失敗只記一次錯誤。
- 已知限制：已開啟的畫面（側邊欄、關於）要換頁或重新整理才顯示新名稱；`WebApplicationFactory` 之後加入的測試設定會蓋過覆寫層。

## 七、驗收與測試

- `SystemParameterTests`：目錄（鍵唯一且有說明、每個鍵綁得到設定類別、範圍與真的驗證器一致、白名單與機密排除、每個鍵寫進文件、
  目錄內設定類別不得以 `IOptions` 讀、系統名稱只經 `ISystemIdentity`）；行為（存檔立即生效且只通知一次、還原、等於設定檔值改為還原、
  不合法的值擋下且不寫入、所有驗證器都會執行、載入時略過不合法與不認得的鍵、驗證以設定檔值為基準、其他行程的修改在刷新時生效、
  並行衝突、設定檔有誤時擋下存檔並保留現有覆寫、系統名稱即時、`ISystemIdentity` 回退、縮短判斷、稽核截斷、限流分割鍵含配額）；
  `SystemParameterHostTests`（真主機中覆寫層排在環境變數之後、存檔後可見）。
- 守門同步：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`DataAccessServiceLifetimeTests`、`PageHelpCatalogTests`。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Configuration/Parameters/`（provider、目錄、編碼、驗證、載入、刷新、管理門面、註冊）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/SystemParameterService.cs`、`Services/Other/SystemIdentity.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/SystemParameter.cs`、Migration `AddSystemParameters`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Admins/SystemParameterView.razor(.cs)`、`Datas/Help/system-parameters.md`
- 規則：[開發慣例與限制速查 §6.15](../architecture/開發慣例與限制速查.md)；設定：[日誌與設定檔說明 §4.15](../operations/日誌與設定檔說明.md)
