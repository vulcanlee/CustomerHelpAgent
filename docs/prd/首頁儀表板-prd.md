# 首頁儀表板 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.106
- 最後核對日期：2026/10/23

## 一、目標與範圍

登入後首頁 `/App` 原本是靜態的系統介紹；改成「精簡歡迎列＋快速入口＋小工具區」，讓每個人一眼看到與自己有關的狀態，並提供衍生系統擴充小工具的框架（路線圖 D-14）。

使用者決定（2026/10/04）：

| 題目 | 決定 |
|---|---|
| 版面 | 保留精簡的歡迎列與快速入口，下方是小工具區 |
| 內建小工具 | 「我的通知」「排程作業狀態」（管理員）「我的帳號」；不做專案狀態統計 |

- **範圍**：小工具的登記與依權限過濾、每個小工具獨立的錯誤邊界、三個內建小工具。
- **非範圍**：使用者自訂排版（拖拉、隱藏、大小）、小工具設定、圖表元件（YAGNI：目前沒有實際需求，有需要時再加）。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|---|---|---|---|
| `/App`（`HomeAuthed.razor` → `HomeWelcomeView`） | `MainLayout` | 頁面鍵「首頁」 | 所有登入者 |

| 小工具 | 代號 | 順序 | 誰看得到 |
|---|---|---|---|
| 我的通知 | `notifications` | 10 | 所有人 |
| 我的帳號 | `account` | 20 | 所有人 |
| 排程作業 | `scheduled-jobs` | 30 | 管理員（`AdminOnly`） |

## 三、畫面與欄位

- **歡迎列**：品牌圖示（72px）、`Welcome`、系統名稱與說明（`ISystemIdentity`）、「系統版本 … · 執行環境 …」。0.9.105 之前的六張「系統能力」卡片與底部版本區塊移除。
- **快速入口**：專案項目／分類清單／團隊清單，依頁面權限過濾（不變）。
- **儀表板**（`DashboardGrid`）：自動排列的卡片（最小寬 18rem），每張有圖示與標題。
  - **我的通知**：未讀數、最新 5 則（未讀粗體、時間、站內連結點了標為已讀並前往）；沒有通知顯示「沒有通知。」；說明全部通知在右上角鈴鐺。
    在鈴鐺標為已讀、刪除或「清除已讀」時立即同步（0.9.122／0.9.125 起）；別的行程（IIS 重疊回收、多台主機）發的通知與鈴鐺一樣每 60 秒輪詢補上（0.9.122 起，之前要重新整理）。
  - **我的帳號**：上一次登入（時間＋「帳號密碼」／「Google 登入」＋來源 IP，0.9.123 起；沒有 IP 的舊紀錄不顯示）、密碼到期（7 天內加「快到期」）、兩步驟驗證「已啟用／未啟用」；沒有本機密碼的人不顯示後兩項；連到個人資料。
  - **排程作業**：總開關關閉時提醒；有異常的作業與原因（上次執行失敗、上次執行被中斷、逾期未執行、已停用）；都沒有時「正常」；連到排程作業頁。
  - 小工具出錯時那張卡片顯示「這個區塊暫時無法顯示，其他區塊不受影響（錯誤追蹤碼：…）」。

## 四、內部系統運作

- **登記**：`DashboardWidgetDescriptor(Id, Title, Order, PermissionKey?, AdminOnly, ComponentType, Icon)`；`services.AddDashboardWidget<TComponent>(…)` 以 singleton 登記，`AddDashboard()` 登記三個內建小工具與 `DashboardWidgetCatalog`（代號重複時啟動失敗）。
- **過濾**：`DashboardWidgetCatalog.VisibleTo(isAdmin, hasPagePermission)` —— 管理員專屬的只給管理員、有權限鍵的要有該頁權限（管理員短路），依 `Order`、`Id` 排序。`HomeWelcomeView` 傳入 `AuthenticationStateHelper.CheckAccessPage`。
  ⚠️ 只有看得到的小工具會被放進 `DashboardGrid`，沒權限的元件根本不建立、不查資料。
- **渲染**：`DashboardGrid` 對每個小工具用 `DynamicComponent`，各自包在 `LoggingErrorBoundary`（寫錯誤日誌、附上追蹤碼）。
- **資料**：小工具只用以 `IDbContextFactory` 為基礎的服務（`NotificationQueryService`、`ProfileService`）或 singleton（`ScheduledJobOverviewService`）—— 同一個連線的共用 DbContext 會與頁面的登入檢查並行衝突。
  - 我的通知：`GetUnreadCountAsync`、`GetLatestAsync(5)`；訂閱 `INotificationSignal`（`NotificationQueryService` 的標為已讀、刪除真的改到資料時才 `Publish`）；另以 `PeriodicTimer`（`NotificationBell.PollInterval` 60 秒、注入 `TimeProvider`）輪詢，讀取失敗只記 Warning、不壞首頁。⚠️ `Dispose` 時取消訂閱並停掉計時器。
  - 我的帳號：`ProfileService.GetAsync`（密碼到期、兩步驟驗證）與 `GetPreviousLoginAsync`（`AuditLog` 以精確 `ActorUserId` 取 `Login.Success`／`Login.Sso.Success` 的倒數第二筆 —— 最近一筆通常就是這次登入）。
  - 排程作業：`ScheduledJobOverviewService.GetOverviewAsync` → `ScheduledJobAttention.From`（與排程作業頁、系統健康監控同一份總覽）。

### 新增一個小工具

1. 在 `Components/Views/Dashboard/` 寫一個元件（`.razor`）。資料一律經以 `IDbContextFactory` 為基礎的服務；需要即時更新就訂閱訊號並在 `Dispose` 取消。
2. 在 `Web/Dashboard/DashboardWidgets.cs` 的 `AddDashboard()` 加一行 `services.AddDashboardWidget<你的元件>("代號", "標題", 順序, "classic Material Icons 名稱", permissionKey: MagicObjectHelper.角色_…, adminOnly: …)`。
3. 樣式用 `DashboardGrid.razor.css` 既有的 `dashboard-*` 類別（顏色一律取 `theme.css` 色票）。
4. 補測試（比照 `DashboardTests`）並更新 `Datas/Help/app.md` 的「儀表板區塊」表。

## 五、權限與安全

- 每個小工具只顯示目前使用者自己的資料（以 `CurrentUserService.CurrentUser.Id` 查詢）。
- 小工具的可見性用登記的權限鍵判斷；資料本身的授權仍由各服務負責，過濾不是安全邊界。
- 通知連結只導向站內網址（`ReturnUrlGuard`，與鈴鐺相同）。

## 六、錯誤與邊界

- 小工具讀取失敗：只有那張卡片顯示錯誤與追蹤碼，錯誤寫進日誌與系統例外紀錄；其他卡片與頁面照常。
- 只登入過一次：「上一次登入」顯示「（沒有紀錄）」。
- 排程總開關關閉：「排程總開關已關閉，所有作業都不會依排程執行。」。
- 沒有任何看得到的小工具：整個「儀表板」區段不顯示。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/Components/DashboardTests.cs`：過濾（管理員、權限鍵、管理員專屬）與排序、代號重複、內建三個小工具的登記、⭐ 沒權限的不建立且壞掉的只影響自己的卡片（bUnit）、我的通知（未讀數、最新 5 則、⭐ 訊號一到就更新、⭐ Dispose 取消訂閱；`Notifications_ShouldPollForChangesFromOtherProcesses_AndStopPollingOnDispose` 以 `TriggerableTimeProvider` 觸發輪詢，0.9.122）、我的帳號（上一次登入不是這次、Google 標示、來源 IP、兩步驟驗證、不會到期）、上一次登入的查詢（跳過最近一筆、忽略失敗與別人）、排程作業的異常規則。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Dashboard/DashboardWidgets.cs`（登記、過濾、`AddDashboard`）、`ScheduledJobAttention.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Dashboard/DashboardGrid.razor(.css)`、`MyNotificationsWidget.razor(.cs)`、`MyAccountWidget.razor`、`ScheduledJobsWidget.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Commons/HomeWelcomeView.razor(.cs/.css)`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ProfileService.cs`（`GetPreviousLoginAsync`）
- 說明頁：`Datas/Help/app.md`
- 交叉連結：[首頁與導覽](首頁與導覽-prd.md)、[站內通知與公告](站內通知與公告-prd.md)、[個人資料](個人資料-prd.md)、[排程作業](排程作業-prd.md)
