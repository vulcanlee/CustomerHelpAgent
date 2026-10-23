# 個人資料 PRD

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.102
- 最後核對日期：2026/10/23

## 一、目標與範圍

讓每個登入的人看自己的帳號資料、角色與團隊、密碼狀態與最近的登入紀錄，並能自己修正姓名；右上角以姓名縮寫代替人像圖示。

- **範圍**：新頁 `/Profile`（登入即可用）、右上角使用者選單的「個人資料」、圓形縮寫、不在選單的登入後頁面的頂欄頁名。
- **非範圍**（使用者決定，2026/10/04）：修改 Email（由管理員改：它是忘記密碼的收件者，改錯就收不到重設信）、頭像上傳、修改角色與團隊、工作階段管理。
  `/ChangePassword` 保留為獨立頁。

> 歷史：0.9.63 之前 `/Profile` 是「設定 API 密碼」頁，已移除；0.9.102 起同一個路由改為個人資料頁，與 API 密碼無關。

## 二、使用者與入口

| 入口 | 位置 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/Profile` | 右上角使用者選單「個人資料」（不在左側選單）| 登入即可（`Pages/_Imports.razor` 的 `[Authorize]`；比照 `/ChangePassword` 不經選單權限）| 所有登入者 |

## 三、畫面與欄位

- **姓名與 Email**：圓形縮寫、姓名、帳號；「姓名」可改（必填、前後空白去掉）；「Email」唯讀並說明由管理員修改。「儲存」。
- **角色與團隊**：所有角色（主要 ∪ 額外，排除已刪除）與指派的團隊（直接加入 ∪ 角色預設），以小標籤顯示；0.9.105 起另列「下屬部門」（因部門樹而看得到資料、但沒有直接指派的團隊，沒有時不顯示）。
- **密碼**：上次變更、到期（`IPasswordPolicy.GetExpiresAtUtc`；不會到期時顯示「不會到期」）、「系統管理員要求變更」狀態、「變更密碼」按鈕；沒有本機密碼（Google）時只顯示說明。
- **兩步驟驗證**（0.9.104 起，有本機密碼的人才有）：「已啟用／未啟用」徽章與「設定兩步驟驗證」／「管理兩步驟驗證」按鈕，前往 `/TwoFactorSetup`（見[兩步驟驗證](兩步驟驗證-prd.md)）。
- **最近 20 筆登入紀錄**：時間（伺服器本地時間）、動作（登入、登入失敗、帳號停用中嘗試登入、連續輸錯，帳號被鎖定、Google 登入／失敗、登出；0.9.103 起另有「其他地方變更了帳號，被登出」、0.9.104 起另有「兩步驟驗證碼錯誤」）、來源 IP（0.9.123 起；0.9.117 之前的舊紀錄顯示「—」）、結果。
- **右上角**：人像圖示改為姓名縮寫（`UserInitials`：中文取第一個字；英文姓名取第一與最後一個字的字首；沒有姓名用帳號）。
- **頂欄頁名**：不在選單的登入後頁面改用操作說明登記的頁名（`MainLayout.ResolvePageTitle`），`/ChangePassword` 不再顯示「系統首頁」。

## 四、內部系統運作

- `ProfileService`（`Business/Services/DataAccess`，scoped，`IDbContextFactory`）：
  - `GetAsync(userId)`：帳號資料、角色經 `RoleView`（排除已刪除）、團隊經 `IEffectiveTeamResolver`（指派的團隊與下屬部門分開，0.9.105 起）、密碼狀態、兩步驟驗證是否開啟（0.9.104 起）。
  - `GetRecentLoginsAsync(userId)`：`AuditLog` 以 ⚠️ **精確 `ActorUserId`** 篩選 `Login.*` 與 `Logout`、新到舊、取 20 筆（不用帳號比對：帳號刪除後可能被別人重新使用），0.9.123 起一併帶出 `ClientIp`。
    Migration `AddAuditLogActorIndex` 加索引 (`ActorUserId`, `OccurredAt`)（稽核表之前沒有任何索引）。
  - `UpdateNameAsync(userId, name, stamp)`：只寫 `Name` 與更新時間；`ConcurrencyStampHelper.Apply` 比對開頁時的版本號（管理員同時修改 → 衝突訊息）、`ProtectFlags`；稽核 `User.ProfileUpdate`（detail 只寫 `field=Name`，姓名是個資）；
    成功且是目前使用者時更新 `CurrentUserService.CurrentUser.Name` 並 `NotifyChanged()`。
- `CurrentUserService.Changed`：`AuthenticationStateHelper.Check`（每個頁面初始化時）載入最新資料後觸發、個人資料存檔後觸發；`MainLayout` 聽它更新姓名與縮寫。
  ⚠️ 不能改聽換頁事件（`LocationChanged`）：它在新頁面的登入檢查之前觸發，讀到的還是舊資料。管理員改了別人的姓名，對方在下一次換頁時更新。

## 五、權限與安全

- 每個方法以目前登入者的 Id 為準，看不到也改不到別人的資料；Email、管理員、啟用、鎖定、旗標都不在這裡改。
- 登入紀錄只來自稽核表。來源 IP 是稽核表的 `ClientIp`（0.9.117 起由 `AuditLogService` 自動寫入），0.9.123 起顯示；之前的舊紀錄沒有 IP，顯示「—」；長的 IPv6 允許換行。

## 六、錯誤與邊界

- 姓名空白 →「姓名不可空白。」；版本號不符 → `ConcurrencyStampHelper.ConflictMessage`。
- 使用者已不存在（極少見）→ 頁面顯示「找不到目前登入使用者的資料。」。

## 七、驗收與測試

- `ProfileTests`：⭐ 登入紀錄只含自己的（同帳號字串不同 Id、匿名失敗、非登入動作都排除）、新到舊、上限 20、帶出來源 IP（舊紀錄為 null，0.9.123）；動作文字；改姓名只動姓名（Email、管理員、啟用、旗標、鎖定、密碼不變）、換版本號、通知右上角、稽核不含姓名；舊版本號衝突、空白被擋；改別人的不動目前使用者；角色排除已刪除、團隊含角色預設、到期時間；縮寫規則（中文、英文頭尾、單字、空白、罕用字）；頁名退回操作說明。
- `AuthenticationStateHelperTests`：登入檢查載入最新資料後觸發 `Changed`。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/DataAccess/ProfileService.cs`、`Services/Other/CurrentUserService.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/ProfilePage.razor`、`Components/Views/Profiles/ProfileView`、`Components/Layout/UserInitials.cs`、`MainLayout`
- 操作說明 `profile.md`；[登入與帳號流程](登入與帳號流程-prd.md)、[使用者管理](使用者管理-prd.md)
