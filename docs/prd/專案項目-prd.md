# 專案項目 PRD

- 文件版本：1.10
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供「專案項目（Project）」的建立、查詢、修改、刪除與附件管理能力，同時作為新增其他領域 CRUD 模組時的參考樣板。

- 範圍：清單查詢（關鍵字搜尋、分類／團隊過濾、排序、分頁）、單筆維護（含表單驗證）、多檔附件上傳／下載／刪除、動作級授權與團隊可見範圍控管。
- 非範圍：專案間的相依關係／甘特圖、工時統計、跨專案報表、附件線上預覽；本模組本身不含 AI 功能（全站的 AI 能力見 [AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md) 與 [Token 用量](Token用量-prd.md)）。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/projects`（`ProjectPage.razor`，掛 `MainLayout`） |
| REST API | `api/Project`、`api/v1/Project`（JWT Bearer） |
| 選單路徑 | 專案管理（`Menu.json` id=2）→ 專案項目（id=21） |
| 權限鍵 | 資源鍵「專案項目」（`MagicObjectHelper.角色_專案項目`），動作 `view`／`create`／`edit`／`delete`；管理員短路 |
| 主要使用者 | 具「專案項目」對應動作權限的登入者；管理員可見全部 |
| 操作說明 | 頂欄頁名旁的「操作說明」按鈕（0.9.66 起，`PageHelpDialog`）；內容為 `Datas/Help/projects.md`，於 `Datas/HelpTopics.json` 登記 `/projects` |

> 頁面進入檢查用 `CheckAccessPage(角色_專案項目)`（通過條件：管理員、裸鍵「專案項目」或 `專案項目:view`），工具列與操作鈕再以 `CheckAccessAction(角色_專案項目, 動作)` 個別控管。群組鍵「專案管理功能」（`角色_專案管理`）只用於側邊選單的群組節點（`MenuPermissionMap[2]`）。
> 頁面被擋下時顯示「你沒有權限存取此頁面」，並寫一筆 `Permission.Denied` 稽核（目標 `Page`／`/projects`，0.9.78 起）。

## 三、畫面與欄位

- 工具列：新增（需 `專案項目:create`）、重新整理、匯出 Excel（0.9.107 起，見[通用匯出](通用匯出-prd.md)）；右側為分類過濾（多選）、團隊過濾（多選）、關鍵字輸入、清空搜尋（有輸入時才出現）、搜尋。分類／團隊過濾一變更就回到第 1 頁重新查詢；兩者的選項都只列啟用中的項目（分類另受團隊可見性過濾）。
- 清單欄位（`ProjectViewView.razor`）：標題、描述、開始日期、結束日期、狀態、優先級、完成百分比、負責人、分類、團隊、建立時間、更新時間、操作（修改需 `專案項目:edit`、刪除需 `專案項目:delete`，無權限時不顯示按鈕）；標題預設遞增排序。0.9.30 起「狀態」以 `StatusPill` 徽章呈現（已完成＝綠、進行中＝梅、暫緩＝琥珀、未開始＝灰，其他值如「等待」為中性色）。
- 可排序欄位（`ProjectService.cs`）：Title、StartDate、EndDate、Status、Priority、CompletionPercentage、Owner、CreatedAt、UpdatedAt；未指定或不認得的排序一律退回 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 搜尋比對欄位（`ProjectService.cs`）：Title、Description、Status、Priority、Owner（關鍵字先去除前後空白）；「顯示已刪除」清單比對相同欄位（0.9.136 起；之前漏了 Status、Priority），固定依刪除時間由新到舊。
- 分頁：`RemoteDataSource`，預設每頁 `MagicObjectHelper.PageSize`（8 筆）。
- 編輯表單（`form-modal` 大量資料輸入對話窗，見 [對話窗 UI 設計規範](../architecture/對話窗%20UI%20設計規範.md)），分四區（`Project` 實體 / `ProjectCreateUpdateDto`）：
  - 基本資料：標題（必填）、負責人（必填；0.9.107 起 Web API 的 `owner` 也必填，不帶回 400）、描述（獨占整行）。
  - 排程與進度：開始日期、結束日期、狀態（必填，`StatusOptions`：未開始／進行中／已完成／暫緩／等待）、優先級（必填，`PriorityOptions`：低／中／高）、完成百分比（0-100）。
  - 分類與團隊：分類（多值標籤）、團隊（多值標籤，不設定＝公開；選項為啟用中的團隊，非管理員只列自己範圍內的〔含下屬部門〕加上這筆原本就有的，0.9.105 起）。
  - 專案附件：見下方「附件」。
- 新增時的預設值：狀態「未開始」、優先級「中」、完成百分比 0；**團隊預填目前使用者範圍裡最上層的團隊**（0.9.105 起範圍含下屬部門，全帶會讓專案對所有下屬公開；已停用、不在選項內的略過）。
- **指派與修改的範圍檢查**（0.9.105 起，`RecordTeamScope`）：非管理員新加的團隊必須在自己範圍內、有團隊的專案不可改成公開；修改前檢查既有專案在範圍內（之前修改不檢查）。Web API 套用同一套規則，範圍外 404、指派範圍外 400。
- 儲存流程（`SaveAsync`）依序為：表單驗證 → 修改模式下若沒有任何變更（含待上傳／待移除附件），提示「沒有任何變更，未進行儲存。」並關窗 →
  儲存確認「確定要儲存這筆記錄嗎？」（「儲存」／「再檢查」）→ 團隊確認（見下）→ 前置檢查 `BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` → 寫入；任一步失敗都不關窗。
- 取消／✕／ESC：有未儲存變更時先詢問是否放棄（`FormDirtyTracker`，附件異動也算變更）；點遮罩不會關窗。
- 刪除：先跑 `BeforeDeleteCheckAsync`，再以 `ConfirmDialog.AskSoftDeleteRecordAsync` 二次確認「確定要刪除這筆紀錄嗎？刪除後可在工具列的「顯示已刪除」中還原。」，並檢查結果。
- **刪除為軟刪除**（0.9.94 起）：刪除只標記 `IsDeleted`／`DeletedAt`／`DeletedBy`，所有查詢經全域過濾器自動排除；工具列「顯示已刪除」（需刪除權限）列出已刪除的專案，可「還原」或「永久刪除」。專案沒有唯一欄位（畫面路徑允許同名），還原不做唯一性檢查。刪除與還原都會換新版本號（正在編輯的人存檔會得到衝突訊息）；刪除、還原、永久刪除都在伺服器端檢查團隊範圍。稽核：`*.Delete`（軟刪除）、`*.Restore`、`*.Purge`。Web API 的 `DELETE` 也是軟刪除，之後對它的 GET／PUT／DELETE 回 404；API 不提供還原與永久刪除。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的專案，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `Project.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。附件資料列隨 Cascade 刪除，實體檔在提交成功後經 `ProjectFileStore` 刪除；刪不掉只計數（孤兒檔），不算失敗。見 [排程作業 PRD](排程作業-prd.md)。
- 附件：軟刪除期間**保留**實體檔但無法下載（`GetFileDownloadAsync` 查不到父專案就回 null —— 不能交給團隊檢查，`IsTeamAccessible(null)` 的語意是「公開」）；永久刪除時**先提交資料庫、成功後才刪實體檔**（0.9.93 之前是先刪檔）；編輯時移除附件也改為存檔成功後才刪檔（0.9.97 起，之前是先刪檔）。刪除實體檔一律經 `ProjectFileStore`（0.9.97 起），路徑解析後跑出 `ProjectFilePath` 根目錄就拒絕（之前只有下載有檢查）。軟刪除期間工具列的分類／團隊過濾隱藏。
- **分類下拉的可選項目（0.4.40 起）**＝「目前使用者可見的分類」∪「本筆專案已貼、但已限定其他團隊的分類」，
  後者顯示為「分類名稱（已限定其他團隊）」（`ProjectViewView.BuildModalCategoryOptions` / `CategoryOptionLabel`）。
  若不列出後者，AntDesign 的多選 `Select` 會把它視為未知值，使用者一存檔就被靜默清掉。
  可見性規則見 [分類清單 PRD](分類清單-prd.md) 第八節。
- **儲存前團隊確認（0.4.40 起）**：驗證通過後，若「團隊」為空，以 `TeamBindingConfirm.AskAsync` 提出警告
  「此專案未指定團隊，將對所有使用者公開可見。確定要這樣儲存嗎？」，
  確認鈕「仍要儲存」、取消鈕「回去編輯」；取消時 Modal 保持開啟、表單內容不消失。
- 附件：「專案附件」區一次可多選，單檔上限 1GB（超過即提示並略過）；待上傳清單可移除，已上傳檔案可點檔名下載（`/api/project-files/{id}/download`，Cookie 驗證，新分頁開啟）或標記移除。待上傳與待移除的檔案都在按「儲存」後才真正寫入／刪除。
- **附件副檔名白名單（0.4.35 起）**：不在白名單內的檔案由 `UploadFileTypePolicy.IsAllowed` 直接拒收；
  儲存的 `ContentType` 一律由 `UploadFileTypePolicy.ResolveContentType` **依副檔名決定**，
  不採用呼叫端提供的值（避免偽裝）。白名單可經 `SystemSettings.Upload.AllowedExtensions` 覆寫，
  留空則採內建預設（不含 `.html`／`.svg`／`.exe` 等可執行或可內嵌腳本的型別）。
  詳見 [檔案上傳機制](../features/檔案上傳機制.md)。

## 四、內部系統運作

- 資料流：`ProjectPage.razor` → `ProjectViewView`（`.razor.cs`）→ `ProjectService` → `BackendDBContext.Project`。REST API 走 `ProjectController` → `ProjectRepository`（與 UI 的 Service 為兩條路徑，皆回 `ApiResult`）。
- 讀取：清單 `GetAsync(DataRequest)` 使用 `AsNoTracking`；單筆 `GetAsync(int)` 以 `Include(x => x.Files)` 帶附件。
- 編輯前處理：開啟修改視窗時以 `ProjectService.GetAsync(id)` 重新取得資料（含附件），再 `Clone()` 一份作為編輯對象（非重用清單物件），並清空待上傳／待移除清單（`ProjectViewView.razor.cs`）。
- DbContext 生命週期：每個方法以 `IDbContextFactory<BackendDBContext>` 建立獨立 context、用完即棄（0.4.36 起，不再需要清追蹤）；附件的寫入與移除沿用 `AddAsync`／`UpdateAsync` 的同一個 context。
- 附件 Adapter：UI 以 `ProjectUploadFileInput`（FileName/ContentType/FileSize/Content）傳入；Service 依主表 `CreatedAt` 年／月建立目錄，檔名以 GUID 產生，落地後寫入 `ProjectFile`；刪除主表為軟刪除、實體檔保留，永久刪除時先提交資料庫、成功後才經 `ProjectFileStore` 刪實體檔（見上方「附件」）。細節見 [檔案上傳機制](../features/檔案上傳機制.md)。
- 稽核（0.9.78 起，LOG-14）：成功後寫入，代碼定義於 `AuditActions`，可在「稽核紀錄」頁查詢。

  | 事件 | 動作代碼 | 寫入點 | 內容 |
  |------|----------|--------|------|
  | 新增／修改／刪除（軟刪除）專案 | `Project.Create`／`Project.Update`／`Project.Delete` | 畫面：`ProjectService`（操作者取自 `CurrentUserService`）；API：`ProjectController` | `title=標題`；API 刪除不帶內容 |
  | 還原／永久刪除專案（0.9.94 起）| `Project.Restore`／`Project.Purge` | `ProjectService.RestoreAsync`／`PurgeAsync` | `title=標題`；永久刪除另含 `files=附件數` |
  | 自動永久刪除（0.9.97 起）| `Project.AutoPurge` | 排程作業「已刪除資料清理」 | 每次一筆彙總（見 §三）|
  | 匯出 Excel（0.9.107 起）| `Project.Export` | `ProjectViewView`（`BusinessExports.WriteAuditAsync`）| 見[通用匯出](通用匯出-prd.md) |
  | 上傳附件 | `Project.FileUpload` | `ProjectService.AddAsync`／`UpdateAsync` | `count=筆數`，沒有上傳就不寫 |
  | 移除附件 | `Project.FileDelete` | `ProjectService.UpdateAsync` | `count=筆數`，沒有移除就不寫 |
  | 下載附件（0.4.31 起）| `Project.FileDownload` | `ProjectFileController` | 目標 `ProjectFile`、`file=原始檔名` |
- Migration：模型異動需在 `CustomerHelpAgent.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- 動作級授權：`ProjectController` 各端點標註 `[HasPermission(角色_專案項目, 動作)]`（`ProjectController.cs`）；無權限回 403 且維持 `ApiResult` 結構；管理員短路。
- UI 與 API 共用同一 RBAC 權威（`IPermissionChecker`）。
- 團隊可見範圍：非管理員清單以 `RecordTeamScope.Apply`（內部用 `TagStringHelper.BuildTeamAccessPredicate`）只看到公開（無團隊）或與自身團隊交集的專案；單筆／附件下載以 `RecordTeamScope.CanAccess`（內部用 `TagStringHelper.IsTeamAccessible`）守門，越界回空模型或 `null`（`ProjectService.cs`）。
- Web API（`ProjectController` → `ProjectRepository`）0.9.105 起套用同一套 `RecordTeamScope` 規則（之前**不做**列級過濾）：範圍外 404、指派範圍外 400，詳見 [紀錄分類與團隊權控 PRD](紀錄分類與團隊權控-prd.md)。
- Web API 的刪除（`ProjectRepository.DeleteAsync`）與畫面路徑一樣是軟刪除（0.9.94 起），附件保留；0.9.93 之前 API 的硬刪除不會刪實體檔，會留下孤兒檔。
- 附件下載端點 `ProjectFileController` 需 `專案項目:view`；查無紀錄、團隊越界、實體檔不存在、路徑逃脫一律回 404（不讓外部從狀態碼推斷哪些 Id 存在）。

## 六、錯誤與邊界

- 標題重複（僅 Web API 路徑）：`Create` 回 409、`Update` 回 409（`ProjectRepository.ExistsByNameAsync`）。畫面路徑（`ProjectService`）**不**檢查標題重複，允許同名專案。
- 路由 ID 與 payload ID 不一致：`Update` 回 400。
- 結束日期早於開始日期、狀態／優先級不合法、完成百分比超出 0-100、未設定附件根目錄：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 回失敗訊息（如「結束日期不可早於開始日期。」）。
- 附件超過 1GB：前端即時提示並略過，後端再次驗證（「檔案 X 超過 1GB 限制」）。
- 附件副檔名不在白名單：後端直接拒收（`UploadFileTypePolicy`），回「檔案 X 的類型不在允許清單中。」。
- 附件落地失敗：已寫出的實體檔會刪除，回「專案附件儲存失敗」。
- 0.9.94 起刪除為軟刪除，不再有「仍有關聯資料（FK 衝突）」的情況，`ProjectController.Delete` 攔截 FK 錯誤回 400 的分支已移除。
- 例外：Service try/catch 記 `Error` 並回「新增／修改／刪除專案失敗。」；畫面上未預期的例外由 `FormModalFlow`／刪除流程的 try/catch 攔下，顯示通用錯誤訊息，不會拆掉 Blazor circuit。
- **並行衝突**（0.9.93 起，樂觀並行）：開啟編輯後若別人先存檔或刪除了同一筆，存檔時回「這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。」，
  Modal 維持開啟、輸入不會遺失。API 的 `PUT` 必須帶 GET 取得的 `ConcurrencyStamp`：沒帶回 400、與資料庫不符回 409。
  屬使用者情境（LOG-11）：只記 `Information`，不進「系統例外紀錄」。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/ProjectServiceTeamAccessTests.cs`：管理員可見全部（3 筆）、非管理員僅見公開＋交集團隊、無團隊者僅見公開、團隊過濾、單筆越界守門回空模型。
- `CustomerHelpAgent.Tests/ProjectServiceTeamAccessTests.cs`（附件下載，5 支）：
  `GetFileDownloadAsync_Admin_ShouldReturnStreamWithOriginalName`、`_UnknownId_ShouldReturnNull`、
  `_NonAdminOutsideTeam_ShouldReturnNull`、`_WhenPhysicalFileMissing_ShouldReturnNull`、
  **`_WhenRelativePathEscapesRoot_ShouldReturnNull`**（路徑逃脫防護，屬安全不變量）。
- `CustomerHelpAgent.Tests/ProjectServiceTeamAccessTests.cs`（移除附件）：`UpdateAsync_RemovingAnAttachmentWhosePathEscapesTheRoot_ShouldNotDeleteTheOutsideFile`、`UpdateAsync_WhenRemovingAnAttachmentFailsToSave_ShouldKeepThePhysicalFile`（0.9.97 起經 `ProjectFileStore`、存檔成功後才刪檔）。
- `CustomerHelpAgent.Tests/ProjectServiceTeamAccessTests.cs`（排序退回）：`GetAsync_WithUnknownSortField_ShouldFallBackToDefaultOrder`、`GetAsync_WithNullSortDescending_ShouldFallBackToDefaultOrder`（0.4.46）。
- `CustomerHelpAgent.Tests/UploadFileTypePolicyTests.cs`：副檔名白名單與 ContentType 對應，
  含 `DefaultAllowedExtensions_ShouldNotContainScriptableTypes`。
- `CustomerHelpAgent.Tests/DataAccessServiceLifetimeTests.cs`：`ProjectService` 不得注入 `BackendDBContext`（須走 `IDbContextFactory`）。
- `CustomerHelpAgent.Tests/PermissionCheckerTests.cs`、`RbacBackfillServiceTests.cs`：動作級授權鍵與 RBAC 回填涵蓋「專案項目」。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/Projects/ProjectPage.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Views/Projects/ProjectViewView.razor`、`ProjectViewView.razor.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ProjectService.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Repositories/ProjectRepository.cs`（Web API 路徑）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/ProjectController.cs`、`ProjectFileController.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/Project.cs`、`ProjectFile.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Models/AdapterModel/ProjectAdapterModel.cs`（`StatusOptions`／`PriorityOptions`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/AuditActions.cs`（`Project.*` 稽核代碼）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Datas/Help/projects.md`（頁面操作說明）
- 交叉連結：[Web API 設計慣例](../architecture/Web%20API%20設計慣例.md)、[檔案上傳機制](../features/檔案上傳機制.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)

> 附件下載端點為 `ProjectFileController`（`api/project-files` 與 `api/v1/project-files`，`GET {id}/download`）。此端點**只收 Cookie 驗證**，與其他走 JWT 的 Web API 不同 —— 呼叫端是畫面上的一般連結，由瀏覽器直接導覽。0.4.31 之前這個端點並不存在，附件下載一律 404。
