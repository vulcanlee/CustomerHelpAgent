# 首頁與導覽 PRD

- 文件版本：1.26
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：既有腳手架核心功能（「關於」對話窗為 0.4.24 新增）
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供系統的兩個進入點與整體導覽骨架：未登入者的品牌啟動畫面（`/`）、登入後的系統介紹首頁（`/App`），以及依權限過濾的側邊功能選單。

- 範圍：啟動頁／介紹頁兩個路由、側邊選單（`Menu.json`）之載入、宣告式權限過濾、收合／展開與圖示呈現，以及右上角使用者選單（含「關於」系統資訊對話窗）與頂欄的「操作說明」按鈕（0.9.66 起；0.9.67 對齊 ReviewSkills.AI）。
- 非範圍：各業務頁面（專案、使用者、角色、分類、團隊）之內容；登入／登出流程本身；權限鍵的授予（屬角色管理）。動作級授權與團隊資料權控見「紀錄分類與團隊權控 PRD」。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/` | 非選單（啟動頁） | 無（`EmptyLayout`，任何人） | 尚未完成驗證者 |
| `/App` | 選單 id=1「首頁」 | 頁面鍵「首頁」（管理員豁免） | 已登入使用者 |
| 側邊選單 | — | 各項目依 `MenuPermissionMap` 對應之權限鍵過濾 | 已登入使用者 |

## 三、畫面與欄位

- 啟動頁（`/` → `Home.razor` → `SplashView`）：品牌圖示（`wwwroot/images/brand-logo.png`，於圓角容器內以 `object-fit: cover` 滿版呈現）、標題（取自 `SystemSettings:SystemInformation:SystemName`）、說明文字（0.9.2 起取自 `SystemSettings:SystemInformation:SystemDescription`，先前為寫死字串）與「系統載入中」狀態列；採 `EmptyLayout`，不含側邊選單。驗證通過即導向 `/App`，未登入則導向 `/Auths/Login`（0.9.39 起；帳號停用等其他失敗仍導向 `/Auths/Logout`）。此頁與 `Error`、`NotFound`、登入相關頁是全系統僅有的匿名頁面（0.9.41 起其餘頁面一律需登入，未登入者直接輸入網址會在 HTTP 層被導去登入頁、看不到任何畫面，見 [開發慣例與限制速查 §5.1](../architecture/開發慣例與限制速查.md)）。
- 首頁（`/App` → `HomeAuthed.razor` → `HomeWelcomeView`，**0.9.9 起**；0.9.106 起改為儀表板）：登入後的第一個畫面，套用主版面與側邊選單。三個區塊由上而下：

  | 區塊 | 內容 | 來源 |
  | --- | --- | --- |
  | 歡迎列 | 品牌圖片（72px）、`Welcome` 標籤、系統名稱、系統說明、「系統版本 … · 執行環境 …」 | `wwwroot/images/brand-logo.png`（以 `@Assets[...]` 取 fingerprint URL）＋ `ISystemIdentity` ＋ `IWebHostEnvironment.EnvironmentName` |
  | 快速入口 | 專案項目／分類清單／團隊清單，**依使用者權限過濾**，全數無權限時整區不顯示 | `AllQuickLinks` ＋ `CheckAccessPage(權限鍵)` |
  | 儀表板 | 依權限過濾的小工具卡片（我的通知、我的帳號、排程作業〔管理員〕），各自有錯誤邊界 | `DashboardWidgetCatalog.VisibleTo` ＋ `DashboardGrid`，見 [首頁儀表板 PRD](首頁儀表板-prd.md) |

  0.9.105 之前另有六張寫死的「系統能力」卡片與底部「系統資訊」區塊，0.9.106 移除（版本與環境移到歡迎列）。

  > ⚠️ 快速入口與小工具的圖示必須是 **classic Material Icons** 名稱。用 Material Symbols 專有名稱會渲染失敗 ——
  > 除了破圖方塊，也可能被拆成數個子字的圖示（`shield_person` 會畫出「盾」與「人」兩個圖示並撐破容器），
  > 側邊欄是靠 `NavMenu.GetMaterialIconKind` 把 `shield_person` 映射成 `security` 才正常。本頁直接用 `security`。

  > 專案清單維持在 `/projects`（`ProjectPage.razor` → `ProjectViewView`）。0.9.9 之前 `/App` 與 `/projects` 渲染同一個檢視，使用者登入後第一眼看到的是資料表格；改版後兩個路由各司其職。
- 側邊選單（`NavMenu.razor` + `SidebarMenuNode`）：
  - 左上品牌文字取自 `SystemSettings:SystemInformation:SystemName`（**0.9.13 起**；先前是硬編的 `CustomerHelpAgent.Web`）。
    過長時以刪節號收尾，完整名稱放在 `title` 屬性。副標「管理後台功能清單」仍為設計文案，刻意不參數化。
    品牌文字左側原有的裝飾圖示（`dashboard_customize`）**0.9.19 起已移除** ——
    它純裝飾卻長得像按鈕，展開時緊貼漢堡鈕造成誤導；現在左上只有漢堡鈕與品牌文字。
  - 依 `Menu.json` 階層渲染，支援展開與「收合」兩種型態（收合時以圖示 flyout 呈現）。
  - 初始狀態（0.9.112 起）：寬螢幕第一次開啟為展開，之後沿用該瀏覽器上次的選擇（`wwwroot/js/nav-state.js`，localStorage `app.sidebarCollapsed`）；
    寬度 ≤ 640px 一律從收合開始，且不寫入偏好。`MainLayout` 在登入檢查完成、版面渲染前讀取，不會先閃另一種狀態；讀不到（腳本未載入、隱私模式）時為展開。
    目前頁面所屬的群組在選單第一次渲染後（`NavMenu.OnAfterRender`）才套用 `OpenKeys` 展開 —— 0.9.113 修正：之前在同一次渲染設定，AntDesign 的 SubMenu 尚未註冊，一開始就展開時群組不會自動打開。
  - 每項含 `name`、`icon`（Material 圖示）、`url` 或子選單 `subMenu`。

  > ⚠️ **圖示大小只能從 `MaterialIcon` 的 `Size` 參數改**：該元件輸出行內的
  > `style="font-size:…"`，CSS 的 `font-size` 一律被壓過（實際都是 24px）。
  > 0.9.16 已把 `NavMenu.razor.css`／`MainLayout.razor.css` 與兩個按鈕元件裡
  > 那些無效的 `font-size` 清掉，**不要再往那裡加**。
  >
  > 0.9.15 起另有**光學尺寸補正**（`Components/Layout/MaterialIconOpticalSize.cs`）：
  > 各字面的墨跡在同一個 em 方框內佔的高度不同，字級一樣不代表看起來一樣大。
  > 以 100px 字級實測，`home` 只有 72、`work` 80、`admin_panel_settings` 84，
  > 所以「首頁」的房子補到 27px 才與鄰居視覺齊平。
  > 要新增補正請先量墨跡高度，不要憑感覺調，也不要把矮的字面一律放大 ——
  > `storage`（68）更矮但視覺密度夠，補了反而突兀。
  - 無任何可用項目時顯示「尚無可用選單」。
  - 現有結構（由上而下，0.9.21 起為三層）：首頁、專案管理（專案項目）、資料定義（分類清單／團隊清單）、
    系統管理〔帳號與權限（使用者管理／角色管理／稽核紀錄）、監控與診斷（系統健康監控／日誌檢視／日誌等級設定／系統例外紀錄／資料庫用量）、
    AI 管理（AI 提示詞／AI 對話紀錄／Token 用量）、系統設定與維運（系統參數／公告管理／排程作業／系統備份）〕、登出。
    0.9.113 起「系統管理」底下只放子群組、不放單一頁面（之前系統參數、系統備份、公告管理、AI 提示詞直接掛在系統管理下，「統計與分析」也混了 AI 與排程）。
  - 三層結構的收合模式（0.9.21 起）：收合時同樣維持階層 —— 群組型子項渲染成一列可懸停的觸發鈕（圖示 ＋ 名稱 ＋ `chevron_right`），滑過去才往右彈出屬於它自己的第二層 flyout，不把孫節點一次攤平在同一層。群組列會以前綴比對標示目前頁面所屬的群組。
    ⚠️ 該區塊只輸出帶 `url` 的節點，新增群組型節點（無 `url`）時必須確認它有自己的巢狀 flyout，否則整個 flyout 會空白。
    ⚠️ flyout 的顯示條件用 `:has(:focus-visible)`，**不可改回 `:focus-within`** —— 後者會讓滑鼠點完子項後焦點黏住、flyout 收不起來。詳見 [開發慣例與限制速查 §5](../architecture/開發慣例與限制速查.md)。
- 右上角使用者選單（`MainLayout.razor`）：顯示姓名縮寫（0.9.102 起取代人像圖示）、目前使用者名稱與「管理員」標記，展開後含「個人資料」（0.9.102 起）「變更密碼」（0.9.101 起開頁面，對話窗已移除）「關於」「登出」四項（0.9.63 起移除「設定 API 密碼」）。
  姓名與縮寫在 `CurrentUserService.Changed` 時更新（每個頁面的登入檢查後、個人資料存檔後）。
- 頂欄頁名（`MainLayout.ResolvePageTitle`）：先找選單；不在選單的登入後頁面用 `HelpTopics.json` 的頁名（0.9.102 起，之前顯示「系統首頁」）。
- **登出需二次確認**（0.9.29 起）：三個登出入口（右上角使用者選單、側邊欄展開、側邊欄收合）
  按下後都先跳確認窗「確認登出／確定要登出嗎？」，選「登出」才真的登出，選「取消」留在原頁。
  行為收斂在 `Components/Commons/LogoutConfirm.cs`，由 `CustomerHelpAgent.Tests/LogoutConfirmationConventionTests.cs` 守門。
  ⚠️ 系統的**強制登出**（session 失效、查無使用者、角色遺失…）刻意**不確認** ——
  那是系統行為不是使用者意圖。詳見 [登入與帳號流程 PRD](登入與帳號流程-prd.md)。
  - ⚠️ **版面層級**（0.4.44 修正）：`.top-row` 帶 `z-index` 即建立 stacking context，選單自己的 `z-index: 20` 只在其內部有效，整個頂部列是以 `.top-row` 的數值參與根層級堆疊。該值必須高於 AntDesign 表格的固定欄／sticky 標頭（2–4），否則「關於」「登出」會被有固定「操作」欄的頁面蓋住。詳見 [開發慣例與限制速查 §6.2](../architecture/開發慣例與限制速查.md)。
- **頂欄「操作說明」按鈕**（0.9.66 起，0.9.67 對齊 ReviewSkills.AI；`Components/Commons/PageHelpDialog`）：頁名右側一顆 28px 透明圓形 `help_outline` 圖示鈕
  （滑鼠提示「這個畫面怎麼用？」），開啟近滿版的「〈頁名〉・操作說明」對話窗 —— 左側說明目錄（全部／一～七），右側內容。
  - 點「全部」顯示前言（頁名、簡介、提醒框、「一分鐘看懂這一頁」）與全部章節；點單一章節只顯示該章節（篩選，不是捲動），切換時內容捲回頂端。每次開啟都回到「全部」。
  - 「六、相關頁面」以卡片呈現，點名稱會先關窗再導頁；**使用者無權進入的頁面不顯示**。
  - **字體大小**（0.9.121 起）：標題列右側「字體 小／中／大」，內文與左側目錄（字級與寬度）一起縮放（0.9.124 起內文約 14／16／20px，預設「中」；0.9.121～0.9.123 為 13／14／17px）。
    選擇記在這個瀏覽器（localStorage `app.helpFontSize`），所有頁面的說明窗共用；換電腦、換瀏覽器或讀不到時回到「中」。
  - 目前路由沒有登記說明時不顯示按鈕；切換頁面時對話窗自動關閉。窄視窗（≤640px）頁名與按鈕整組隱藏（與 ReviewSkills.AI 一致）；若開著窗縮小視窗，對話窗改為滿版、章節導覽改為橫向一排。
  - 涵蓋登入後的 23 個頁面（0.9.135；20 個選單頁＋`/ChangePassword`、`/Profile`、`/TwoFactorSetup`；0.9.66 起始時為 14 個）；內容與規則見 [開發慣例與限制速查 §6.14](../architecture/開發慣例與限制速查.md)。
- 「關於」對話窗（`MainLayout.razor` 之 `about-modal`）：以 AntDesign `Modal`（寬 520、無 Footer）呈現六列唯讀系統資訊。

  | 項目 | 來源 |
  | --- | --- |
  | 系統名稱 | `SystemSettings.SystemInformation.SystemName` |
  | 系統描述 | `SystemSettings.SystemInformation.SystemDescription` |
  | 系統版本 | `SystemSettings.SystemInformation.SystemVersion`（唯一版本來源） |
  | 執行環境 | `IWebHostEnvironment.EnvironmentName` |
  | 啟動時間 | `SystemStartupState.StartedAt`（`yyyy/MM/dd HH:mm:ss`） |
  | 已運作時間 | `DateTimeOffset.Now - StartedAt`（`dd.hh:mm:ss`） |

## 四、內部系統運作

1. `SidebarMenuService.LoadAuthorizedMenuItemsAsync` 讀取選單並過濾：
   - `ReadMenuItemsFromDisk` 由 `MagicObjectHelper.Menu結構定義`（`Datas/Menu.json`）反序列化，經 `ICacheService` 快取（key `sidebar:menu:raw`）。
   - `ApplyPermissionStructure` 依每項唯一 `id` 從 `MenuPermissionMap`（id→權限鍵）填入 `PermissionName`；找不到對應時退回以 `Name` 為權限名。
   - `FilterAuthorizedMenuItems` 遞迴過濾：項目自身權限（`Name` 或 `PermissionName` 任一）通過，或其子項尚有可見項目時保留。
2. 權限判定唯一來源為 `AuthenticationStateHelper.CheckAccessPage(name)`：比對 `CurrentUser.RoleList`（由 `IPermissionChecker.GetEffectivePermissionKeysAsync` 供給的 RBAC 有效權限鍵集合）；管理員短路一律通過。
3. `Menu.json` 以 `id` 對應權限鍵，重排選單順序不會錯位（已移除舊「位置索引三處同步」耦合）。
4. 頁面操作說明：`PageHelpService` 讀 `Datas/HelpTopics.json` 與 `Datas/Help/*.md`（經 `ICacheService` 快取），以**精確比對**（非頂欄標題的前綴比對）找出目前路由的說明；
   `PageHelpMarkdownParser` 以行首 `## ` 切章節，`HelpMarkdownRenderer`（`DisableHtml` ＋ `UsePipeTables`）轉 HTML。
   相關頁面以 `MainLayout` 傳入的已授權選單過濾，與側邊欄同源。
5. 「關於」對話窗由 `MainLayout.OnAboutClick` 於**點擊當下**組出資料列：注入 `IOptions<SystemSettings>`、`IWebHostEnvironment` 與 Singleton `SystemStartupState`。已運作時間必須在開啟當下計算並存成欄位，否則 Blazor Server 不會自動刷新而顯示過期值。

## 五、權限與安全

- 頁面權限採宣告式三件組：`Menu.json`（每項唯一 `id`）＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數。
- id→權限鍵對應（節錄）：1→`角色_首頁`「首頁」、2→`角色_專案管理`「專案管理功能」、21→`角色_專案項目`「專案項目」、3→`角色_系統管理`「系統管理功能」（管理員專屬，不在矩陣）、34→`角色_帳號與權限`「帳號與權限」（同左）、6→`角色_監控與診斷`「監控與診斷功能」（同左）、7→`角色_AI管理`「AI 管理功能」（同左）、8→`角色_系統設定與維運`「系統設定與維運功能」（同左）、65→`角色_系統健康監控`「系統健康監控」（同左）、61→`角色_日誌檢視`「日誌檢視」（同左）、63→`角色_日誌等級設定`「日誌等級設定」（同左）、33→`角色_系統例外紀錄`「系統例外紀錄」（同左）、62→`角色_資料庫用量`「資料庫用量」（同左）、64→`角色_Token用量`「Token 用量」（同左）、31→`角色_使用者管理`「使用者管理」（同左）、32→`角色_角色管理`「角色管理」（同左）、35→`角色_稽核紀錄`「稽核紀錄」（同左）、39→`角色_AI提示詞`「AI 提示詞」（同左）、66→`角色_AI對話紀錄`「AI 對話紀錄」（同左）、36→`角色_系統參數`「系統參數」（同左）、38→`角色_公告管理`「公告管理」（同左）、67→`角色_排程作業`「排程作業」（同左）、37→`角色_系統備份`「系統備份」（同左）、5→`角色_資料定義`「資料定義管理功能」、51→`角色_分類清單`「分類清單」、52→`角色_團隊清單`「團隊清單」、4→`角色_登出`「登出」。
- **例外：系統管理整支（3、其下四個子群組 34／6／7／8 與所有頁面）的權限鍵都是管理員專屬**。
  這些鍵刻意未列入 `RolePermissionService.GetRoleListPermissionAllName()`，因此不會種出 `Permission` 資料列、
  角色矩陣不顯示、任何角色都無法被授予，只有 `CheckAccessPage` 的管理員短路能通過
  （`MyUserView`／`RoleViewView` 另以 `CheckIsAdmin()` 守門）。由 `AdminOnlyPermissionTests` 守住，**請勿補上**。
  詳見 [日誌檢視 PRD](日誌檢視-prd.md)、[使用者管理 PRD](使用者管理-prd.md)、[角色管理 PRD](角色管理-prd.md)。
- 選單過濾僅隱藏無權項目，並非授權邊界；實際資料存取由 API 端 `[HasPermission]` 與團隊權控把關（見「紀錄分類與團隊權控 PRD」）。
- 管理員（`IsAdmin`）於 `CheckAccessPage` 短路，選單全可見。
- 「操作說明」按鈕不另做權限判斷：能進入該頁的人就看得到該頁說明；說明內的「相關頁面」依已授權選單過濾（管理員短路自然成立）。說明內容是隨原始碼進版控的可信檔案，仍以 `DisableHtml()` 防止夾帶 raw HTML。
- 右上角使用者選單與「關於」對話窗不做權限過濾：任何已登入者皆可開啟；內容僅為系統識別資訊，不含連線字串、金鑰或其他機敏設定。

## 六、錯誤與邊界

- 找不到／無法解析 `Menu.json`：記錄警告或錯誤並回傳空清單，選單顯示「尚無可用選單」，不致中斷頁面。
- 未登入者進入 `/App` 等受保護頁：0.9.41 起在 HTTP 層（`[Authorize]`）即被導去 `/Auths/Login`；`AuthenticationStateHelper.Check` 的未驗證分支同樣導向 `/Auths/Login`（0.9.39 起，不清 Cookie）。帳號停用、缺角色、工作階段失效者於此導向 `/Auths/Logout`，需改密碼者導向 `/ChangePassword`。
- 使用者無任一項目權限：選單為空，僅顯示提示文字。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/MenuIconTests.cs::MenuJson_AllIcons_ShouldBeNonEmptyAndAllowed`：`Menu.json` 每項圖示非空且屬允許集合。
- `CustomerHelpAgent.Tests/MenuPermissionConsistencyTests.cs::MenuJsonIds_ShouldExactlyMatch_MenuPermissionMapKeys`：`Menu.json` 的 id 集合與 `MenuPermissionMap` 的 key 完全相等。
- `CustomerHelpAgent.Tests/MenuPermissionConsistencyTests.cs::Views_CheckAccessPageKey_ShouldMatch_MenuPermissionMap`：`ViewToMenuId` 已登錄 `HomeWelcomeView.razor.cs` → 選單 id 1，驗證它檢查的權限鍵確為 `角色_首頁`。
- 手動驗收：以不同角色登入，確認選單僅顯示具權限之項目；管理員可見全部；重排 `Menu.json` 順序不影響權限對應。
- 手動驗收（首頁）：登入後落在 `/App`，可見品牌圖、系統名稱、系統說明、版本（須與 `appsettings.json` 的 `SystemVersion` 一致）、快速入口與儀表板（一般使用者兩張卡片、管理員三張，圖示皆為單一圖示）。儀表板的測試見 [首頁儀表板 PRD](首頁儀表板-prd.md)。
- 手動驗收（權限）：以**未授予「首頁」權限**的角色登入，直接輸入 `/App` 應顯示「你沒有權限存取此頁面」且不渲染介紹內容，側邊欄亦無「首頁」；以**未授予分類／團隊清單**的角色登入，快速入口只剩「專案項目」。
- 手動驗收（RWD）：視窗縮至 400px 寬，歡迎列改直向、儀表板卡片改單欄、快速入口滿版，頁面不出現水平捲軸。
- 手動驗收（關於）：點右上角使用者名稱 →「關於」，對話窗顯示六列資訊，系統版本須與 `appsettings.json` 之 `SystemVersion` 一致；關閉後再次開啟，「已運作時間」應有增加。
- `CustomerHelpAgent.Tests/PageHelpCatalogTests.cs`：每個 `@page` 都登記說明或明列排除理由、索引與檔案雙向一致、七段章節、頁名＝索引＝選單、「一分鐘看懂」六標籤、第三段四欄表頭、常見問題（≥3 題且以「還是解決不了怎麼辦？」結尾）、相關頁可解析、UTF-8 BOM、csproj 複製規則；`PageHelpParsingTests.cs`、`HelpMarkdownRendererTests.cs` 覆蓋解析、比對、權限過濾與管線；`PageHelpFontSizesTests.cs`（0.9.121 起）覆蓋字體大小的存值往返、讀不到或無效回「中」、選項依小／中／大排列。
- 手動驗收（操作說明）：在各頁按頁名旁的「?」，滑鼠提示「這個畫面怎麼用？」、標題為「〈頁名〉・操作說明」；切換章節（內容捲回頂端）、Esc 關窗；視窗縮至 640px 以下時頁名與按鈕不顯示；以非管理員登入，相關頁面不出現管理頁；開窗後點相關頁卡片會關窗並導頁。
- 權限判定來源之測試見 `PermissionCheckerTests.cs`（詳「紀錄分類與團隊權控 PRD」）。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Home.razor`（`/` landing）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/HomeAuthed.razor`（`/App` 系統介紹首頁）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Commons/HomeWelcomeView.razor`／`.razor.cs`／`.razor.css`（歡迎列、快速入口、儀表板）；`Components/Views/Dashboard/*`、`Web/Dashboard/*`（小工具）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Projects/ProjectPage.razor`（`/projects` 專案清單）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Commons/SplashView.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/Menu.json`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap`）、`:68`（載入與過濾）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/NavMenu.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/MainLayout.razor`（使用者選單與「關於」對話窗）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/MainLayout.razor.cs`（`OnAboutClick`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Commons/PageHelpDialog.razor`／`.razor.cs`／`.razor.css`、`PageHelpMarkdownParser.cs`、`HelpMarkdownRenderer.cs`（操作說明）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/PageHelpService.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/HelpTopics.json`、`Datas/Help/*.md`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Health/SystemStartupState.cs`（啟動時間來源）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/AuthenticationStateHelper.cs`（`CheckAccessPage`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs`（角色權限鍵常數）
- 交叉連結：[紀錄分類與團隊權控 PRD](紀錄分類與團隊權控-prd.md)、[認證授權與權限機制](../security/認證授權與權限機制.md)
