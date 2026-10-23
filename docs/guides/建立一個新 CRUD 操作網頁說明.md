# 建立一個新 CRUD 操作網頁

- 文件版本：2.1
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/10/23

> 0.9.110 起一行指令產生並登記完整的 CRUD 模組（路線圖 G-21）。不要再複製既有檢視 —— 0.4.27 的 emoji 回歸（六個檢視共 22 個按鈕）正是複製貼上造成的。
> 慣例與踩雷點見[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（§6.27 是產生器本身）。

---

## 1. 一行指令

```powershell
pwsh ./scripts/New-CrudModule.ps1 -Name Equipment -DisplayName 設備清單
```

- 先提交或暫存手上的變更：產生器預設要求工作目錄乾淨，讓它的異動可以單獨檢視（`-Force` 略過）。
- 跑完會列出寫入的檔案、每一項登記（「已登記」或「略過（已存在）」）與 migration 結果。
- 要先看產出長什麼樣子：加 `-Preview`，只寫到 `output/crud-modules/<Name>/`，不改專案。

## 2. 參數

| 參數 | 預設 | 說明 |
|---|---|---|
| `-Name` | （必填） | 實體名稱，PascalCase，例如 `Equipment` |
| `-DisplayName` | 同 `-Name` | 顯示名稱，同時是權限鍵（`MagicObjectHelper.角色_設備清單 = "設備清單"`），不可與既有頁面重複 |
| `-Plural` | `<Name>s` | 頁面與檢視的資料夾／命名空間 |
| `-Route` | `/<plural 小寫>` | 頁面路由；說明檔名同規則（`equipments.md`） |
| `-MenuGroupId` | `5`（資料定義） | 掛在 `Menu.json` 的哪個群組；非管理員專屬只能 `2`（專案管理）或 `5`，因為角色權限矩陣只有這兩組。管理員專屬掛在「系統管理」的子群組：`34`（帳號與權限）、`6`（監控與診斷）、`7`（AI 管理）、`8`（系統設定與維運）；**不可**用 `3`（系統管理那一層只放子群組，0.9.113 起產生器會擋下） |
| `-Icon` | `description` | 選單圖示（classic Material Icons），不在 `MenuIconTests` 允許清單時自動加入 |
| `-WithTeams` | 關 | 加上「團隊」欄位與團隊範圍：沒有團隊（公開）或與授權團隊有交集才看得到；非管理員只能指定自己範圍內的團隊 |
| `-AdminOnly` | 關 | 管理員專屬：權限鍵不進角色矩陣，檢視以 `CheckIsAdmin` 判斷（配合上列子群組，例如 `-MenuGroupId 8`） |
| `-SkipMigration` | 關 | 不自動產生 migration |
| `-Preview` | 關 | 只產生檔案到 `-OutputPath`，不登記、不產生 migration |
| `-OutputPath` | `output/crud-modules` | `-Preview` 的輸出目錄（相對 repo 根目錄），檔案寫在 `<OutputPath>/<Name>/` 底下 |
| `-Force` | 關 | 略過工作目錄檢查；之前產生過的檔案保留不覆蓋（用來重跑登記） |

## 3. 產生了哪些檔案

樣板在 `scripts/crud-templates/`（行首 `#IF TEAMS`／`#IF ADMIN`／`#ELSE`／`#ENDIF` 控制條件區塊）。以 `-Name Equipment -DisplayName 設備清單` 為例：

| 檔案 | 內容 |
|---|---|
| `AccessDatas/Models/Equipment.cs` | 實體：名稱、描述、啟用（、團隊）；`IConcurrencyStamped`（樂觀並行）＋`ISoftDeletable`（刪除可還原） |
| `Models/AdapterModel/EquipmentAdapterModel.cs` | 畫面模型，`Clone()` |
| `Dtos/Models/EquipmentDto.cs`、`EquipmentCreateUpdateDto.cs`、`Dtos/Commons/EquipmentSearchRequestDto.cs` | Web API 的 DTO（PUT 必須帶 `concurrencyStamp`） |
| `Business/Repositories/EquipmentRepository.cs` | Web API 的資料存取（scoped DbContext） |
| `Business/Services/DataAccess/EquipmentService.cs` | Blazor 的資料服務（`IDbContextFactory`）：清單、已刪除清單、新增、修改（並行比對）、只改啟用狀態的 `SetEnabledAsync`（0.9.120 起：換版本號、已是目標狀態不寫稽核）、刪除、還原、永久刪除、名稱重複檢查、稽核 |
| `Web/Controllers/EquipmentController.cs` | GET／search／POST／PUT／DELETE，各自 `[HasPermission]`，回 `ApiResult`；名稱重複 409、並行衝突 409、軟刪除 |
| `Web/Components/Pages/Equipments/EquipmentPage.razor` | `@page`＋`@layout MainLayout` |
| `Web/Components/Views/Equipments/EquipmentViewView.razor(.cs)` | 權限閘門（`isAccessChecked`）、`<RequirePermission>` 依動作顯示按鈕、搜尋排序分頁、勾選多筆「刪除選取」（0.9.132 起，`BatchDelete`；`BatchDeleteConventionTests` 守門）、顯示已刪除／還原／永久刪除、匯出 Excel、`form-modal` 表單（未儲存確認、儲存確認）；「啟用狀態」欄的 `StatusPill` 有編輯權限時可點（0.9.120 起：停用先確認、啟用直接切換；沒有權限經 `<RequirePermission>` 的 `Fallback` 顯示不可點的標籤；顯示已刪除時不可點） |
| `Web/Datas/Help/equipments.md` | 操作說明初稿（固定七段，UTF-8 含 BOM） |
| `Tests/EquipmentServiceTests.cs` | 新增修改與稽核、名稱重複、並行衝突、切換啟用狀態（只改狀態、換版本號、稽核）、軟刪除／還原／永久刪除、還原撞名、搜尋（、團隊範圍） |

工具列與表格容器用 `wwwroot/theme.css` 的共用類別（`.view-toolbar`、`.view-table-wrap`），不另產生 `.razor.css`。

## 4. 自動登記了哪些地方

重跑不會重複插入（已存在就略過並列出）；找不到登記位置時停止並說明是哪一項。

| 位置 | 內容 |
|---|---|
| `BackendDBContext` | `DbSet`、名稱唯一索引（只約束未刪除的資料） |
| `AutoMapping` | 實體 ↔ 畫面模型／DTO（名稱正規化、`IgnoreSoftDeleteFields`、團隊字串轉換） |
| `ServiceCollectionExtensions` | 服務與 Repository 的 DI |
| `MagicObjectHelper` | 權限鍵常數 |
| `AuditActions` | `Equipment.Create／Update／Enable／Disable／Delete／Restore／Purge／AutoPurge／Export`（`Enable`／`Disable` 0.9.120 起） |
| `Menu.json` | 指定群組的最後一項，id＝目前最大 id＋1 |
| `SidebarMenuService.MenuPermissionMap` | 選單 id → 權限鍵 |
| `RolePermissionService`（一般）或 `AdminOnlyPermissionTests`＋`MenuPermissionConsistencyTests.AdminOnlyViews`（管理員專屬） | 角色權限矩陣；預設角色在下次啟動時自動加入新頁面 |
| `MenuPermissionConsistencyTests.ViewToMenuId`（一般） | 檢視檔與選單 id 的對照 |
| `HelpTopics.json`、`_Imports.razor` | 操作說明目錄、檢視命名空間 |
| `SoftDeletePurgeService`、`SoftDeletePurgeJob` | 排程作業「已刪除資料清理」與它的稽核 |
| `OptimisticConcurrencyTests`、`SoftDeleteUserRoleTests`、`SoftDeletePurgeTests` | 並行與軟刪除的實體清單、清理測試 |
| `DataAccessServiceLifetimeTests`、`AdapterModelCloneTests`、`ApiIntegrationTests`、`MenuIconTests` | 服務生命週期、Clone、API DI 解析、選單圖示 |

最後在隔離的暫存路徑執行 `dotnet build` 與 `dotnet ef migrations add Add<Name>`（不寫進 `C:\temp` 或正式路徑）。

## 5. 產生之後的檢查清單

1. [ ] **補上實際欄位**：實體、畫面模型、兩個 DTO、`AutoMapping`（有需要轉換時）、表單（短欄位自動兩欄，多行或多選加 `Class="form-field-full"`）、表格欄位、`ExportColumns`、服務的搜尋與排序、Repository 的搜尋與排序、測試。
2. [ ] **改了欄位就重新產生 migration**：刪掉產生器建立的 `Add<Name>` 再執行 `dotnet ef migrations add Add<Name>`（隔離路徑的做法見[開發指引](腳手架開發指引.md)），或另外加一個 migration。
3. [ ] **操作說明**：把 `Datas/Help/<路由>.md` 第二段（這個頁面在做什麼）與第四段（建議操作）改成實際的業務說明；改了按鈕或欄位要同步改第三段（[速查 §6.14](../architecture/開發慣例與限制速查.md)）。
4. [ ] **被其他資料引用時**：在服務的刪除與永久刪除加上「仍被使用就不能刪」的檢查，並在 `SoftDeletePurgeService` 的這一行加上 `isInUse`。
5. [ ] **權限**：一般頁面在「角色管理」把新頁面的動作授予需要的角色（預設角色已自動加入）。
6. [ ] `pwsh ./scripts/Invoke-QualityGate.ps1` 全綠。
7. [ ] 實機：選單出現、可新增、修改、刪除、還原、永久刪除、匯出；沒有權限的人看不到按鈕、直接開網址會被擋。
8. [ ] 文件：PRD、畫面字典、changelog、版本號 Patch +1。

## 6. 改了產生器或樣板之後

```powershell
pwsh ./scripts/Test-CrudGenerator.ps1            # 數分鐘；-KeepWorktree 保留工作目錄以便啟動網站實際操作
```

在暫存的 git worktree（以目前工作目錄為準，含未提交的變更；不動你的 index）產生三個範例模組（一般、`-WithTeams`、`-AdminOnly`），
建置、格式檢查、全部測試、文件編碼，再以 `-Force` 重跑確認沒有任何檔案再被改動。它不在品質關卡裡（太慢），**修改產生器、樣板或任何被登記的檔案的錨點之後必跑**。

⚠️ 登記是以既有程式碼為錨點插入的（例如 `services.AddScoped<CategoryRepository>();` 之後）。改動這些錨點行時，同步更新 `New-CrudModule.ps1`，`Test-CrudGenerator.ps1` 會先失敗告訴你。

## 7. 已知限制

- 範例欄位只有名稱、描述、啟用（、團隊），名稱全系統唯一（不分大小寫、只看未刪除的資料）。
- 一般頁面只能掛在「專案管理」或「資料定義」群組（角色權限矩陣只有這兩組）。
- 不產生子表、檔案上傳或外鍵關聯；需要時參考 `ProjectService`（附件）或 `TeamService`（自我參照）。
- 產生的模組不含 PRD 與畫面字典條目，請自行補上。
