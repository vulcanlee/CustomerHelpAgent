# 角色管理 PRD

- 文件版本：1.9
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供管理員維護角色（`RoleView`）與其**動作粒度權限矩陣**的能力，並設定角色預設團隊。角色權限以權限鍵集合表示，透過 RBAC 雙寫落地為 `RolePermissionMap`，成為 UI 與 API 動作級授權（`[HasPermission]`／`IPermissionChecker`）的單一權威來源。

- **範圍**：`/roleviews` 角色維護頁、`RoleViewService` CRUD、`RolePermissionService` 權限矩陣序列化、`RbacWriteService.SyncRolePermissionsAsync` 雙寫、稽核寫入。
- **非範圍**：使用者與角色的指派見 [使用者管理](使用者管理-prd.md)；登入與帳號安全見 [登入與帳號流程](登入與帳號流程-prd.md)；紀錄層級的團隊可視性見 [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/roleviews` | 系統管理 → 帳號與權限 → 角色管理（`Menu.json` id=32；0.9.113 起）| 僅管理員（`AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

- `RoleViewView` 初始化先 `Check`，非管理員顯示「你沒有權限存取此頁面」並停止載入。

> **0.4.33 起**：`角色管理` 權限鍵刻意**不列入角色矩陣**（比照「統計與分析」群組，0.9.113 起改名「監控與診斷」）。
> 此前該鍵可被勾選卻永遠無效（頁面以 `CheckIsAdmin` 守門），屬於「死權限」。
> 由 `CustomerHelpAgent.Tests/AdminOnlyPermissionTests.cs` 守門，請勿補上。

- 角色本身以 Blazor 頁面、管理員身分閘控（無 `RoleView` API 控制器）；此處編輯出來的權限鍵，才是各業務 API 控制器 `[HasPermission("頁面", "動作")]` 的授權依據。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 名稱、建立時間、更新時間，可排序；工具列含新增、重新整理、「顯示已刪除」切換（0.9.95 起）、搜尋、清空搜尋（搜尋比對名稱）。
- **維護表單**（Modal）：
  - 名稱（必填，唯一）。
  - 預設團隊（多選團隊名稱；不設定表示僅能看到無團隊的公開紀錄）。
  - 「需要兩步驟驗證」（0.9.104 起，`RoleView.RequireTwoFactor`）：有這個角色（主要或額外）的人必須開啟兩步驟驗證，還沒設定的人下一次換頁被帶到設定頁；稽核 detail 帶 `requireTwoFactor=`。見[兩步驟驗證](兩步驟驗證-prd.md)。
  - **動作粒度權限矩陣**（角色項目）：依 `RolePermissionService` 的群組結構呈現。每個群組（母項，如「專案管理功能」「資料定義管理功能」；系統管理及其子群組不上架矩陣）有一個群組核取方塊；群組下每個頁面節點提供「（全部）」核取方塊，以及四個動作核取方塊：檢視、新增、編輯、刪除（`view/create/edit/delete`）。⚠️ 0.9.37 之前還有第五個「匯出」，但全系統沒有任何地方檢查 `export`，屬「勾了等於沒勾」的死權限，已下架並由 `AdminOnlyPermissionTests` 守門；要重新上架必須**先**有會檢查它的程式。
- **矩陣互動語意**：勾「（全部）」等同該頁裸鍵、代表全部動作，並停用個別動作核取方塊（舊制相容）；勾任一動作或頁面會自動點亮所屬群組；取消群組會連帶清掉其下所有頁面權限。

## 四、內部系統運作

View（`RoleViewView`）→ `RoleViewService` → `BackendDBContext`：

- **矩陣 ↔ 權限鍵**（`RolePermissionService`）：`GetPermissionInput` 將勾選狀態轉為權限鍵清單——群組名、裸頁面鍵（＝全動作），或 `PermissionKey.For(頁面, 動作)`（如「專案項目:edit」）；`SetPermissionInput` 反向回填矩陣。清單序列化為 `RoleView.TabViewJson`。
- **新增／修改**（`AddAsync`／`UpdateAsync`）：以 `GetPermissionInputToJson` 產生 `TabViewJson` 存檔；再 `ParsePermissionKeys` 解析並呼叫 `RbacWriteService.SyncRolePermissionsAsync` 雙寫至 `RolePermissionMap`；寫 `Role.Create`／`Role.Update` 稽核（含權限鍵數）。
- **RBAC 雙寫**（`RbacWriteService.SyncRolePermissionsAsync`）：`EnsurePermissionsAsync` 對缺漏的權限鍵自動補建 `Permission` 列，再對 `RolePermissionMap` 差異化增刪，使角色權限與矩陣一致。
- **刪除**（`DeleteAsync`，0.9.95 起為軟刪除）：追蹤載入 → 禁止刪除「預設角色」→ 仍有**未刪除**使用者（含停用者）以它為主要角色就擋下，訊息列出人數與前 5 個帳號（使用者決定；刪了的話那些人每次換頁都會被登出）→ `MarkDeleted` 存檔 → **存檔後再數一次**，期間若有人被設成這個主要角色就還原並擋下。事前檢查不可省略：只靠事後複查會先寫入再還原，版本號被換掉，正在編輯這個角色的人存檔時會被誤判為衝突。`RolePermissionMap` 與額外角色的 `UserRole` 保留。
- **已刪除清單／還原／永久刪除**（0.9.95 起）：還原時與有效角色同名（完全比對）就擋下。永久刪除只能對已刪除的角色；任何使用者（**含已刪除的**，`IgnoreQueryFilters` 計算）仍以它為主要角色就擋下並列出帳號（`MyUser.RoleViewId` 是 Restrict 外鍵）。刪除與還原都會換新版本號；登入、權限判斷、下拉選單都經全域過濾器而看不到已刪除的資料。稽核：`Role.Delete`（軟刪除）、`Role.Restore`、`Role.Purge`。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的角色，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `Role.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。「預設角色」永遠不刪；任何使用者（含尚未到期的已刪除使用者）仍以它為主要角色時留到之後再刪，同一輪中使用者先於角色處理。見 [排程作業 PRD](排程作業-prd.md)。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時由 `RolePermissionService` 建立權限目錄（`Permission`，含 `GroupName`／`SortOrder`），並依各角色 `TabViewJson` 補寫 `RolePermissionMap`，冪等。
- **權限判定**（`PermissionChecker`）：使用者角色取自 `UserRole`（多角色）並容錯併入 legacy `RoleViewId`；join `RolePermissionMap`／**`RoleView`（0.9.95 起，排除已刪除的角色）**／`Permission` 得有效權限鍵集合；管理員短路回 true；擁有裸頁面鍵者視為具該頁全部動作。
- **前置檢查**：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查角色名稱唯一性。
- **預設角色**：`Get預設新建帳號角色Async` 以名稱「預設角色」查詢，供新使用者預帶。0.9.95 起不可刪除；`DefaultRoleViewSeeder` 以 `IgnoreQueryFilters` 查找並優先取未刪除的列，找到已刪除的就還原（不會重建第二個帶全部權限的預設角色）。

> ℹ️ 0.9.136 起 `RoleViewService.AddAsync`／`UpdateAsync` 以工廠建立的 context 開一個交易：存 `RoleView`（含 `TabViewJson`）→
> `RbacWriteService.SyncRolePermissionsAsync(context, …)` 用**同一個 context** 寫 `RolePermissionMap` → 提交；權限對應寫入失敗時整筆回復。
> 之前兩者各用自己的 context 存檔，可能只成功一半。`RbacWriteService` 不帶 context 的多載仍用 DI 注入的 scoped context（給測試與其他呼叫端）。
- **「預設角色」不可改名**（0.9.136 起）：`BeforeUpdateCheckAsync` 與 `UpdateAsync` 都擋，訊息「「預設角色」是系統內建的角色，不可改名。」，不換版本號；權限與其他欄位照常可改。原因：系統以名稱找它，改名後啟動時會重建一個帶全部權限的新「預設角色」。

## 五、權限與安全

- RBAC 表（`Permission`／`RolePermissionMap`／`UserRole`）為 UI 與 API 共用的**單一權威**；登入後 `AuthenticationStateHelper` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync` 載入有效權限鍵（多角色聯集）。
- 動作級授權：API 控制器以 `[HasPermission(頁面, 動作)]` 判權，無權限回 `ApiResult` 403（`ForbiddenResult`）；未登入回 401；管理員短路一律通過。UI 以 `RequirePermission` 元件（0.9.110 起，規則在 `PermissionRules.CanAccessAction`，與 `AuthenticationStateHelper.CheckAccessAction` 相同）依動作顯示按鈕，沒有權限時不渲染、或渲染 `Fallback`（0.9.120 起，例如不可點的狀態標籤）；專案項目、分類清單、團隊清單三頁皆有。⚠️ 頁面進入權由 `CheckAccessPage` 判定，通過條件為「管理員 ∨ 裸頁面鍵 ∨ `頁面鍵:view`」（0.9.37 起）——因此只勾「檢視」的唯讀角色**進得去頁面但按不到任何寫入鈕**。
- 角色本頁僅管理員可進入；不輸出任何機密欄位。

## 六、錯誤與邊界

- 角色名稱重複：前置檢查回「角色名稱已存在，無法新增／修改。」
- 修改對象不存在：回「找不到要修改的角色資料。」
- **0.9.93～0.9.94 的缺陷（0.9.95 修正）**：`RoleViewAdapterModel.Clone()` 手寫逐欄複製、漏了 `ConcurrencyStamp`，從畫面修改任何角色都回並行衝突訊息。
- `TabViewJson` 解析失敗：`OtherDependencyData` 以空權限初始化矩陣（不致命）。
- 未設任何權限：該角色無有效權限鍵，成員（非管理員）將無對應頁面／動作。
- 矩陣使用未在目錄中的權限鍵時，雙寫會自動補建 `Permission` 列。
- **並行衝突**（0.9.93 起，樂觀並行）：開啟編輯後若別人先存檔或刪除了同一筆，存檔時回「這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。」，
  Modal 維持開啟、輸入不會遺失。屬使用者情境（LOG-11）：只記 `Information`，不進「系統例外紀錄」。
  0.9.92 之前這一頁的存檔不看結果，任何失敗都顯示「修改成功」；0.9.93 起失敗會顯示原因並保持 Modal 開啟。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/RbacWriteServiceTests.cs`：`SyncRolePermissionsAsync_ShouldAddAndRemoveToMatchKeys`、`ShouldCreateMissingPermissionRows`。
- `CustomerHelpAgent.Tests/RbacBackfillServiceTests.cs`：建立權限目錄、由 `TabViewJson` 連結角色權限、冪等。
- `CustomerHelpAgent.Tests/PermissionCheckerTests.cs`：管理員全通過、角色具／缺鍵、裸頁面鍵授予全動作、僅 `view` 不含 `edit`、多角色聯集。
- `CustomerHelpAgent.Tests/AuditEventsTests.cs`：`Role.Create`／`Role.Delete` 稽核。
- `CustomerHelpAgent.Tests/RoleViewServiceTests.cs`（0.9.136）：權限對應寫入失敗時新增與修改整筆回復；「預設角色」不可改名且不換版本號、其他欄位照常可改。
- `CustomerHelpAgent.Tests/SoftDeleteUserRoleTests.cs`（0.9.95）：已刪除的角色不再給權限（含 legacy `RoleViewId`）、仍是主要角色時刪除被擋且不改版本號、存檔後複查、預設角色不可刪、永久刪除的明確訊息、從 Clone 出來的模型可以存檔。
- `CustomerHelpAgent.Tests/TwoFactorTests.cs`（0.9.104）：主要或額外角色勾了「需要兩步驟驗證」即必須使用、已刪除的角色不算。
- `CustomerHelpAgent.Tests/AdapterModelCloneTests.cs`（0.9.95）：`Clone()` 複製每個可寫屬性 —— 0.9.93～0.9.94 `RoleViewAdapterModel.Clone()` 漏了版本號，從畫面修改任何角色都被當成並行衝突。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Admins/RoleViewPage.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Admins/RoleViewView.razor`（權限矩陣）、`RoleViewView.razor.cs`（矩陣互動）、`:490`（動作欄定義 `PermissionActionItems`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/RoleViewService.cs`（`AddAsync`、`UpdateAsync`、`DefaultRoleRenameMessage`、回填矩陣 `OtherDependencyData`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/RolePermissionService.cs`（`SetPermissionInput` `:117`）、`:138`（`GetPermissionInput`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/RbacWriteService.cs`（`SyncRolePermissionsAsync`）、`:108`（`EnsurePermissionsAsync`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/PermissionChecker.cs`（判定）、`RbacBackfillService.cs`（權限目錄）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Filters/HasPermissionAttribute.cs`（API 403）
- `src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/PermissionKeys.cs`（`PermissionActions`／`PermissionKey`）
- RBAC 資料表：`RoleView`、`Permission`、`RolePermissionMap`、`UserRole`（`src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/`）
- 交叉連結：[使用者管理](使用者管理-prd.md)、[登入與帳號流程](登入與帳號流程-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)
