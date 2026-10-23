# 紀錄分類與團隊權控 PRD

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.4.0
- 最後核對日期：2026/10/23

## 一、目標與範圍

這是一份跨功能的權威文件，定義兩件貫穿所有業務紀錄（目前為專案）的能力：

1. 紀錄的「分類 / 團隊」多值標籤欄位（分類供檢索、團隊供資料權控）。
2. 團隊資料權控（列級可見性）與動作級 RBAC 授權（API 動作級把關）。

授權判定為單一權威來源，UI 與 API 共用；其他 PRD（首頁與導覽等）連結至本文件。

- 範圍：標籤欄位的儲存與過濾、使用者「有效團隊」解析、紀錄可見範圍規則、`[HasPermission]` 動作級授權、管理員豁免。
- 非範圍：分類清單／團隊清單的 CRUD 頁面本身；角色與權限鍵的授予流程（屬角色管理）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/projects` | 專案管理子選單 | 頁面鍵，動作另需 `頁面:動作` | 一般使用者 |
| API `Project` Controller | 非選單 | `[HasPermission(頁面, 動作)]` | UI 呼叫端 / 外部（JWT） |
| `/categories`、`/teams` | 資料定義子選單 | 分類清單／團隊清單頁面鍵 | 管理者 |

分類與團隊為系統層級主檔；各紀錄以多值標籤引用其名稱。

## 三、畫面與欄位

- 紀錄編輯（如 `ProjectViewView` Modal）：
  - 「分類」多選，供檢索過濾，不影響本筆紀錄的可見性。選項本身受**分類主檔的適用團隊**過濾（0.4.40 起，見「分類清單 PRD」第八節），依**登入使用者所屬團隊**決定，與這筆紀錄自己填了哪些團隊無關。
  - 「分類」選項會額外列出「本筆紀錄已貼、但目前使用者看不到」的分類並加註「（已限定其他團隊）」，避免存檔時被靜默清掉。
  - 「團隊」多選，Placeholder「選擇團隊（不設定表示公開）」；決定該筆紀錄的可見範圍。
  - 儲存前若「團隊」為空，以 `TeamBindingConfirm.AskAsync` 提出警告（0.4.40 起）：說明此紀錄將對所有使用者公開可見，使用者可選「回去編輯」或「仍要儲存」。取消時 Modal 保持開啟。
- 清單工具列：分類過濾、團隊過濾（多選）與關鍵字搜尋。
- 表格欄位：分類（`CategoriesText`）、團隊（`TeamsText`）以文字呈現。
- 可見範圍規則（非管理員）：
  - 無團隊（null 或空）＝ 公開，任何人可見。
  - 有團隊＝ 僅當紀錄團隊與使用者「有效團隊」有交集才可見。
  - 0.9.105 起「有效團隊」含所有下屬部門：被指派到上層部門的人看得到下屬部門的紀錄；反過來，下屬看不到上層或同層部門的紀錄。
  - 管理員一律可見全部。
- 指派規則（0.9.105 起，非管理員）：新加上的團隊必須在自己的範圍內（原本就有的可以保留或拿掉）；原本有團隊的紀錄不可改成公開。表單的「團隊」選項只列範圍內的團隊（加上這筆原本就有的）；新增專案時預設帶入範圍裡最上層的團隊。

## 四、內部系統運作

1. 標籤字串（`TagStringHelper`）：多值以換行分隔並前後包夾，例 `"\n團隊A\n團隊B\n"`。此格式可用 `Contains("\n團隊A\n")` 在 SQLite 上做「精確成員」比對，避免子字串誤判（如「團隊」誤中「團隊2」）。
   - `ToStored` 去空白／去重（忽略大小寫，保留順序）；`ToList` 還原；`Wrap` 包單一名稱。
2. 使用者有效團隊（`EffectiveTeamResolver.GetEffectiveTeamNamesAsync`）：指派的團隊（`GetAssignedTeamNamesAsync`，聯集兩來源並去重）再加上它們所有的下屬部門（0.9.105 起，`ITeamTreeCache`）——
   - 直接綁定使用者的團隊（`UserTeam`）。
   - 使用者角色（`UserRole` ∪ legacy `RoleViewId`）的預設團隊（`RoleView.DefaultTeamsJson`）。
   - 反查 `GetUserIdsInTeamAsync(團隊)`（發通知、公告用）＝ 被指派了這個團隊或它任一個上層部門的人，與正向定義相同（測試互相對照）。
3. 存取範圍解析（`RecordAccessScopeProvider.GetAsync` → `RecordAccessScope(IsAdmin, Teams)`）：
   - Blazor 互動情境用已填入的 `CurrentUserService`。
   - Web API／檔案下載情境以 `RequestActorResolver` 取出使用者 Id（Cookie 在 Sid、JWT 在 NameIdentifier），載入使用者並解析有效團隊。
   - 未登入：「非管理員、無團隊」，僅能看到公開紀錄。⚠️ 已登入卻解析不到是誰（0.9.105 起）：`RecordAccessScope.None`（`Denied`），什麼都看不到 —— 0.9.104 之前退回「只看公開」，而 JWT 的 Id 不在 Sid，Web API 等於不過濾，分類更是全部看得到。
4. 查詢範圍套用：一律經 `RecordTeamScope`（0.9.105 起）—— `Apply`／`CanAccess`（紀錄）、`ApplyCategory`／`CanAccessCategory`（分類的反向規則）、`CheckAssignment`（指派）。
   Blazor 的 `ProjectService`／`CategoryService` 與 Web API 的 `ProjectRepository`／`CategoryRepository` 共用，清單、單筆、修改、刪除都套用（0.9.120 起分類清單點狀態標籤的 `CategoryService.SetEnabledAsync` 也檢查範圍）；範圍外的單筆在 API 一律回 404。
5. 分類主檔的可見性（0.4.40 起）：`Category` 本身也有 `Teams` 標籤欄位，`CategoryService.ApplyTeamVisibility` 會過濾清單、單筆與下拉來源。⚠️ 其「使用者無團隊」的規則**與紀錄相反**——紀錄是「只看得到公開紀錄」，分類是「視為不受限、看得到全部」。分類可見性只是下拉清單的便利性過濾，安全邊界仍為 RBAC。完整規則見「分類清單 PRD」第八節。
6. 動作級授權（`HasPermissionAttribute`）：解析呼叫者 userId 後委由 `IPermissionChecker.HasPermissionAsync` 判定；管理員短路，擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。

## 五、權限與安全

- 單一權威來源：`IPermissionChecker` 為 UI（`AuthenticationStateHelper.CheckAccessPage`／`CheckAccessAction`）與 API（`[HasPermission]`）共用的權限判定來源，兩端一致。
- 宣告式頁面權限：`Menu.json` 唯一 `id` ＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數（見「首頁與導覽 PRD」）。
- 動作級 RBAC：受保護 CRUD 以 `[HasPermission("頁面", "動作")]` 標註（View/Create/Edit/Delete；`Export` 已於 0.9.37 下架）；未登入回 401、無權限回 403，皆維持 `ApiResult` 格式。
- 團隊權控為資安不變量：列級可見性由伺服器端查詢述詞強制，UI 過濾僅為輔助，不可作為授權邊界。
- 0.9.105 起這條不變量**同時涵蓋 Web API**（之前只有 Blazor 路徑有，持 JWT 呼叫 `POST /api/Project/search` 可取得全部紀錄）。⚠️ 新增會讀寫紀錄的 Repository 或 Controller 時，一律經 `RecordTeamScope`。
- 0.9.105 起修改也檢查既有紀錄的範圍（之前 `ProjectService`／`CategoryService` 的 `UpdateAsync` 不檢查，知道 Id 就能改看不到的紀錄）。
- 管理員豁免：`IsAdmin` 於 `PermissionChecker`、`CheckAccessPage/Action`、`IsTeamAccessible`、查詢範圍皆短路，一律通行且可見全部。

## 六、錯誤與邊界

- 無分類／團隊：紀錄視為公開（團隊）、無分類標籤（分類）；過濾清單空表示不套用該過濾。
- 非管理員且無有效團隊：僅見公開**紀錄**；但**分類主檔**在同一情況下視為不受限，看得到全部（刻意的規則差異，見第四節第 5 點）。
- 指派範圍外的團隊：畫面顯示「只能指定你所屬範圍內的團隊（「X」不在你的範圍內）。」，API 回 400；把有團隊的紀錄改成公開：「這筆資料原本有指定團隊，不能改成不指定（公開）。」。
- 已登入卻解析不到身分：什麼都看不到，指定團隊回「無法確認你的身分，不能指定團隊。」。
- 標籤精確比對避免「團隊」誤命中「團隊2」等子字串問題。
- API 未登入回 401、越權回 403，維持 `ApiResult`，不洩漏資料。
- 子項（如附件）存取沿用父紀錄的團隊可見性判斷。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/TagStringHelperTests.cs`：`ToStored_ThenToList_ShouldRoundTrip`、`ToStored_ShouldTrimDeduplicateAndDropBlanks`、`BuildContainsAnyPredicate_ShouldMatchExactMemberOnly`、`IsTeamAccessible_*`（公開／交集／無交集／管理員）。
- `CustomerHelpAgent.Tests/EffectiveTeamResolverTests.cs`：`ShouldReturnDirectUserTeams`、`ShouldReturnRoleDefaultTeams`、`ShouldUnionAndDeduplicate`、`ShouldReturnEmptyForUnknownUser`。
- `CustomerHelpAgent.Tests/ProjectServiceTeamAccessTests.cs`：`GetAsync_Admin_ShouldSeeAllRecords`、`GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords`、`GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords`、`GetAsync_WithTeamFilter_ShouldFilterByTeam`。
- `CustomerHelpAgent.Tests/CategoryServiceTeamVisibilityTests.cs`（0.4.40）：分類主檔的可見性，特別是 `GetAsync_NonAdminWithoutTeams_ShouldSeeAllCategories` 釘住「與紀錄相反」的那條規則。
- `CustomerHelpAgent.Tests/TeamTreeTests.cs`（0.9.105）：有效團隊含下屬、⭐ 反查與正向對照、指派規則、拒絕的範圍什麼都看不到、服務層修改檢查範圍與指派、JWT 身分解析與解析不到即拒絕；`TeamTreeApiTests`：⭐ Web API（JWT）上層看得到下屬、同層看不到、範圍外 404、指派範圍外團隊 400。
- `CustomerHelpAgent.Tests/PermissionCheckerTests.cs`：`HasPermissionAsync_ForAdmin_ShouldReturnTrueForAnyKey`、`HasPermissionAsync_WhenRoleHasKey_ShouldReturnTrue`、`HasPermissionAsync_LegacyBarePageKey_ShouldGrantAnyActionOfThatPage`、`HasPermissionAsync_GranularViewOnly_ShouldNotGrantEdit`、`GetEffectivePermissionKeysAsync_WithMultipleRoles_ShouldReturnUnion`。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/TagStringHelper.cs`（標籤字串、`BuildTeamAccessPredicate:110`、`IsTeamAccessible:142`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/EffectiveTeamResolver.cs`（有效團隊解析）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Auth/RecordAccessScopeProvider.cs`（存取範圍解析）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/IRecordAccessScopeProvider.cs`（`RecordAccessScope`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Helpers/RecordTeamScope.cs`（0.9.105 起的共用可見性與指派規則）、`Services/Other/TeamTree.cs`（部門樹與快取）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ProjectService.cs`、`CategoryService.cs`、`Repositories/ProjectRepository.cs`、`CategoryRepository.cs`（查詢範圍套用）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/PermissionChecker.cs`（`HasPermissionAsync`，管理員短路 `:33`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Filters/HasPermissionAttribute.cs`（401/403 與 `ApiResult`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/ProjectController.cs`（`[HasPermission]` 動作級標註）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/AuthenticationStateHelper.cs`（`CheckAccessAction`）
- `src/CustomerHelpAgent/CustomerHelpAgent.Share/Helpers/PermissionKeys.cs`（`PermissionKey.For`／`PageOf`）
- 交叉連結：[認證授權與權限機制](../security/認證授權與權限機制.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
