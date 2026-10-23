# 分類清單 PRD

- 文件版本：1.13
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.3.0
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供「分類（Category）」主資料的維護能力，讓具權限的管理者在 `/categories` 頁面完成分類的查詢、新增、修改、刪除。分類為獨立主資料，無外鍵關聯，`Name` 唯一（不分大小寫）；亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的分類名稱。

0.4.40 起，分類可指定**適用團隊**（`Teams`，多值），用以限定哪些團隊看得到這個分類，避免使用者的分類下拉清單塞滿用不到的項目。詳細可見性規則見第八節。

非範圍：
- 不做分類的階層／樹狀結構（純平面清單）。
- 不做與其他實體的外鍵關聯或參照完整性檢查（刪除前無被引用檢查，`BeforeDeleteCheckAsync` 直接回成功）。
- 不做匯入、批次操作（0.9.107 起有匯出 Excel，見 §三）。（0.9.94 起刪除為軟刪除，可還原，見 §三。）

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/categories`（`CategoryPage.razor`，`MainLayout`） |
| 選單路徑 | 資料定義（id=5）> 分類清單（id=51 選單項，`url=/categories`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[51] = 角色_分類清單` |
| UI 頁面權限 | 頁面鍵「分類清單」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `分類清單:view` / `分類清單:create` / `分類清單:edit` / `分類清單:delete` |
| 主要使用者 | 具「分類清單」角色權限的後台管理者；系統管理員無條件可存取 |
| 操作說明 | 頂欄頁名旁的「操作說明」按鈕（0.9.66 起，`PageHelpDialog`）；內容為 `Datas/Help/categories.md`，於 `Datas/HelpTopics.json` 登記 `/categories` |

（`Menu.json` 的 id 與 `MenuPermissionMap` 的 key 皆為 51，且 `MenuPermissionConsistencyTests` 強制兩邊 id 集合完全相等。）

## 三、畫面與欄位

單頁清單 + Modal 表單（`CategoryViewView`）：

- 工具列：新增（需 `分類清單:create`）、重新整理、匯出 Excel（0.9.107 起，見[通用匯出](通用匯出-prd.md)）；右側為關鍵字輸入、清空搜尋（有輸入時才出現）、搜尋。
- 搜尋：關鍵字比對 `Name` 或 `Description`（`Contains`）。
- 排序：可排序欄位 `Name`、`IsEnabled`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`（8 筆），`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、描述、適用團隊、啟用狀態（`StatusPill` 徽章：啟用／停用；0.9.120 起有 `分類清單:edit` 時點徽章直接切換，停用先確認，`CategoryService.SetEnabledAsync` 只改狀態並換版本號、檢查團隊範圍、稽核 `Category.Enable`／`Category.Disable`；已是目標狀態時回成功、不寫稽核；沒有修改權限或在「顯示已刪除」時為不可點的一般標籤）、更新時間、操作（修改需 `分類清單:edit`、刪除需 `分類清單:delete`，無權限時不顯示按鈕）。
- 新增／編輯表單（`form-modal` 大量資料輸入對話窗，見 [對話窗 UI 設計規範](../architecture/對話窗%20UI%20設計規範.md)；「分類資料」一區）：
  - 名稱 `Name`（必填，最長 100）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
  - 描述 `Description`（選填，最長 2000，獨占整行）
  - 適用團隊 `Teams`（多選，選填，獨占整行；選項為啟用中的團隊；不設定表示所有團隊皆可使用）
- 儲存流程（`SaveAsync`）依序為：表單驗證 → 修改模式下若沒有任何變更，提示「沒有任何變更，未進行儲存。」並關窗 →
  儲存確認「確定要儲存這筆記錄嗎？」（「儲存」／「再檢查」）→ 名稱重複檢查 → 團隊確認（見下）→ 寫入；寫入失敗會顯示原因且不關窗。
- 取消／✕／ESC：有未儲存變更時先詢問是否放棄（`FormDirtyTracker`；改了又改回原值視為無變更）；點遮罩不會關窗。
- 刪除：`ConfirmDialog.AskSoftDeleteRecordAsync` 二次確認「確定要刪除這筆紀錄嗎？刪除後可在工具列的「顯示已刪除」中還原。」，並檢查結果（0.9.93 之前不看結果，失敗也顯示「刪除成功」）。
- **刪除為軟刪除**（0.9.94 起）：刪除只標記 `IsDeleted`／`DeletedAt`／`DeletedBy`，所有查詢經全域過濾器自動排除；工具列「顯示已刪除」（需刪除權限）列出已刪除的分類，可「還原」或「永久刪除」。還原時重新檢查唯一性，衝突就擋下並說明（名稱：去空白、不分大小寫）。刪除與還原都會換新版本號（正在編輯的人存檔會得到衝突訊息）；刪除、還原、永久刪除都在伺服器端檢查團隊範圍。稽核：`*.Delete`（軟刪除）、`*.Restore`、`*.Purge`。Web API 的 `DELETE` 也是軟刪除，之後對它的 GET／PUT／DELETE 回 404；API 不提供還原與永久刪除。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的分類，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `Category.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。見 [排程作業 PRD](排程作業-prd.md)。
- 唯一索引 `IX_Category_Name` 為部分索引（`WHERE "IsDeleted" = 0`）：已刪除的名稱可以重新建立。
- 儲存前團隊確認（0.4.40 起；0.4.41 起改排在名稱重複檢查之後）：`ConfirmTeamBindingAsync` 會在兩種情況擇一提出警告，
  確認鈕為「仍要儲存」、取消鈕為「回去編輯」（取消時 Modal 保持開啟、表單內容不消失）：
  1. 完全未指定適用團隊 —— 這筆會成為所有人都看得到的公用分類。
  2. 指定的團隊與自己所屬團隊沒有交集 —— 存檔後自己就會在清單上看不到它。

## 四、內部系統運作

- UI 路徑：`CategoryViewView` →（注入）`CategoryService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`CategoryController` → `CategoryRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Category`（`Id/Name/Description/Teams/IsEnabled/CreatedAt/UpdatedAt`，另有樂觀並行 `ConcurrencyStamp` 與軟刪除 `IsDeleted/DeletedAt/DeletedBy`），DbSet 為 `context.Category`。`Teams` 以換行分隔字串儲存（`TagStringHelper`），AutoMapper 以 `ForMember` 在 `List<string>` 與字串間轉換。
- 查詢一律 `AsNoTracking()`；每個方法以 `IDbContextFactory<BackendDBContext>` 建立獨立 context、用完即棄（0.4.36 起，不再需要清追蹤）。
- 編輯前於 UI 以 `CurrentRecord = model.Clone()` 複製，避免污染清單資料；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified` 提交；刪除以 `SoftDeleteHelper.MarkDeleted` 標記（軟刪除），永久刪除才 `Remove`。
- 稽核（0.9.78 起，LOG-14）：新增、修改、刪除成功後各寫一筆 `Category.Create`／`Category.Update`／`Category.Delete`（代碼定義於 `AuditActions`，目標為 `Category`／Id），可在「稽核紀錄」頁查詢；點狀態標籤切換另寫 `Category.Enable`／`Category.Disable`（0.9.120 起），匯出寫 `Category.Export`（0.9.107 起）。
  畫面路徑由 `CategoryService` 寫入（操作者取自 `CurrentUserService`，內容 `name=分類名稱`），Web API 路徑由 `CategoryController` 寫入（API 刪除不帶名稱）；寫入失敗（含唯一索引擋下）不留稽核。
- 模型變更需在 `CustomerHelpAgent.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`分類清單:view`、`分類清單:create`、`分類清單:edit`、`分類清單:delete`。裸鍵「分類清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面、以 `CheckAccessAction(角色_分類清單, 動作)`（0.9.110 起畫面改用 `<RequirePermission>` 元件，規則相同）控制新增／修改／刪除按鈕是否顯示；API 用 JWT Bearer 並以動作鍵控制個別操作。
- 頁面被權限擋下時顯示「你沒有權限存取此頁面」，並寫一筆 `Permission.Denied` 稽核（目標 `Page`／`/categories`，0.9.78 起）。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（先以 `NameNormalizer` 去除前後空白，再 `ToLower()` 不分大小寫，修改時排除自身），重複回「分類名稱已存在，無法新增／修改。」。API 端另以 `ExistsByNameAsync` 回 409 Conflict，判定語意與 UI 路徑一致。
- 名稱正規化與唯一索引（0.4.41 起）：寫入前一律 `Trim()`（AutoMapper 的「→ Entity」映射上），資料庫另有 `IX_Category_Name` 唯一索引兜底。前置檢查與寫入不在同一個交易裡，並發時由索引擋下，訊息經 `UniqueConstraintHelper` 轉譯為「分類名稱已存在，無法儲存。」。UI 會檢查 `AddAsync` / `UpdateAsync` 的回傳值後才顯示成功。
  名稱重複屬使用者錯誤（0.9.78 起，LOG-11）：只記 `Information`、不帶例外物件，不會進「系統例外紀錄」。
  - ⚠️ 沿革：0.4.41 之前是「檢查時 Trim、寫入時不 Trim」，「技術文件 」（尾隨空白）會原樣入庫，之後「技術文件」再也比不到它，兩筆看起來一模一樣的資料同時存在。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的分類資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱必填、長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service 以 try/catch 記 `Error` 並回 `VerifyRecordResult(false, "新增／修改／刪除分類失敗。")`；API 以 `ApiServerError` 回 500。畫面上未預期的例外由 `FormModalFlow`／刪除流程的 try/catch 攔下，顯示通用錯誤訊息，不會拆掉 Blazor circuit。
- **並行衝突**（0.9.93 起，樂觀並行）：開啟編輯後若別人先存檔或刪除了同一筆，存檔時回「這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。」，
  Modal 維持開啟、輸入不會遺失。API 的 `PUT` 必須帶 GET 取得的 `ConcurrencyStamp`：沒帶回 400、與資料庫不符回 409。
  屬使用者情境（LOG-11）：只記 `Information`，不進「系統例外紀錄」。

## 七、驗收與測試

對應測試檔 `src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryServiceTests.cs`：

- `BeforeAddCheckAsync_WithUniqueName_ShouldSucceed`：唯一名稱可新增。
- `BeforeAddCheckAsync_WithDuplicateName_ShouldFail`：重複名稱被拒。
- `BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail`：大小寫不同仍視為重複。
- `BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed`：同一筆用原名可通過。
- `BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail`：名稱被他筆占用被拒。
- `AddAsync_ShouldPersistCategory`：新增後可查回並保留描述與啟用狀態。
- `AddAsync_WithUntrimmedName_ShouldPersistTrimmedName` / `AddAsync_WithFullWidthSpace_ShouldPersistTrimmedName`：寫入前正規化（含全形空白 U+3000）。
- `BeforeAddCheckAsync_AfterAddingUntrimmedName_ShouldRejectTrimmedName`：0.4.41 修正的破口重現。
- `AddAsync_WithDuplicateName_ShouldReturnFriendlyMessage`：略過前置檢查直接寫，驗證唯一索引兜底與訊息轉譯。

對應測試檔 `src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryServiceTeamVisibilityTests.cs`（0.4.40 新增）：

- `GetAsync_Admin_ShouldSeeAllCategories`：管理員看到全部。
- `GetAsync_NonAdminWithoutTeams_ShouldSeeAllCategories`：**沒有團隊的使用者看到全部**（與紀錄相反的規則）。
- `GetAsync_NonAdminWithTeams_ShouldSeeOnlyPublicOrIntersectingCategories`：只看到公用分類與有交集的分類。
- `GetAllEnabledNamesAsync_NonAdminWithTeams_ShouldFilterByTeamAndSkipDisabled`：下拉清單同時受團隊與啟用狀態過濾。
- `GetAllEnabledNamesAsync_NonAdminWithoutTeams_ShouldReturnAllEnabled`：未綁團隊者在**下拉清單**路徑也看得到全部。
- `GetById_NonAdmin_ShouldDenyCategoryOutsideTeamScope`：單筆守門回空模型。
- `BeforeAddCheckAsync_WithNameOfInvisibleCategory_ShouldStillFail`：名稱唯一性仍為全域比對。
- `AddAsync_ShouldRoundTripTeamsBetweenListAndStoredString` / `UpdateAsync_WithEmptyTeams_ShouldStoreNullAsPublicCategory`：`Teams` 的 List↔字串往返。

對應測試檔 `src/CustomerHelpAgent/CustomerHelpAgent.Tests/StatusToggleServiceTests.cs`（0.9.120 新增，與團隊、使用者共用）：

- `Category_SetEnabled_ShouldChangeStatusAndAudit`：只改狀態、換版本號並寫 `Category.Disable`。
- `Category_SetEnabled_OutsideTeamScope_ShouldBeDenied`：團隊範圍外的分類被擋下。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper；存取範圍以 `FakeRecordAccessScopeProvider` 替身指定。

## 八、分類的團隊可見性（0.4.40 起）

| 情境 | 結果 |
| --- | --- |
| 分類未指定適用團隊（`Teams` 為 null／空） | 公用分類，所有人可見 |
| 使用者為系統管理員 | 看得到全部分類 |
| 使用者未綁定任何團隊 | 看得到全部分類 |
| 使用者有團隊 | 看得到「公用分類」＋「適用團隊與自己所屬團隊有交集」的分類 |

- 「使用者的團隊」＝其**所有**所屬團隊的聯集（`UserTeam` ∪ 角色 `DefaultTeamsJson`），0.9.105 起再加上它們所有的下屬部門，由 `IEffectiveTeamResolver` 計算、經 `IRecordAccessScopeProvider` 取得。系統沒有「目前團隊／切換團隊」的概念。
- 規則集中在 `RecordTeamScope.ApplyCategory`／`CanAccessCategory`（0.9.105 起；`CategoryService.ApplyTeamVisibility`／`IsVisible` 轉呼叫它們）。修改也檢查既有分類在範圍內；非管理員新加的適用團隊必須在自己範圍內、有指定的不可改成留空（`RecordTeamScope.CheckAssignment`）。
- ⚠️ **「未綁團隊的使用者看得到全部」與紀錄（Project）的規則相反** —— 紀錄是「只看得到公開紀錄」。分類可見性是下拉清單的便利性過濾，不是安全邊界（安全邊界為 RBAC），故刻意採寬鬆規則。
- 名稱唯一性檢查**不**套團隊過濾，避免「看不到卻建得出同名分類」。
- 專案編輯畫面的分類下拉，會額外把「這筆專案已貼、但目前使用者看不到」的分類列出並加註「（已限定其他團隊）」，避免使用者一存檔就把它靜默清掉。
- Web API 路徑（`CategoryController` / `CategoryRepository`）0.9.105 起套用同一套規則（之前**不**做行級過濾）：範圍外 404、指派範圍外 400。

## 九、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Categories/CategoryPage.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Categories/CategoryViewView.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Categories/CategoryViewView.razor.cs`（頁面權限檢查）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/CategoryController.cs`（`[HasPermission]` 動作鍵）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/CategoryService.cs`（AddAsync / 前置檢查）
- `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/Category.cs`（Entity 欄位）
- `src/CustomerHelpAgent/CustomerHelpAgent.Dtos/Models/CategoryCreateUpdateDto.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Dtos/Commons/CategorySearchRequestDto.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/PermissionKeys.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Layout/SidebarMenuService.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/Menu.json`
- `src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryServiceTests.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryServiceTeamVisibilityTests.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Tests/StatusToggleServiceTests.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryTeamRepositoryUniquenessTests.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Tests/CategoryTeamUniqueIndexMigrationTests.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/NameNormalizer.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/UniqueConstraintHelper.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Commons/TeamBindingConfirm.cs`（儲存前團隊確認對話窗，與專案編輯頁共用）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/AuditActions.cs`（`Category.*` 稽核代碼：Create／Update／Enable／Disable／Delete／Restore／Purge／AutoPurge／Export）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/Help/categories.md`（頁面操作說明）
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
