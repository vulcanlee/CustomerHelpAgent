# Web API 端點目錄

- 文件版本：1.9
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.1.61
- 最後核對日期：2026/10/23

本文件彙整 `CustomerHelpAgent.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）；`BackupController` 只有 `api/backups`。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。**例外是 `ProjectFileController` 與 `BackupController`（見第三節），它們走 Cookie。**
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `CustomerHelpAgent.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。

## 二、資源 CRUD 控制器

三個資源控制器共用同一組動作樣板（以 `CategoryController` 為代表，`src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/CategoryController.cs:38`）：

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無回 `NotFound`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>` |
| 新增 | `POST {controller}` | Create | `ApiResult<TDto>`（同名回 `Conflict`，團隊另檢查代號；指派範圍外的團隊、上層部門不合法回 `BadRequest`；客戶端傳來的 `Id` 一律忽略，0.9.94 起）|
| 更新 | `PUT {controller}/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 `BadRequest`；**沒帶 `ConcurrencyStamp` 回 `BadRequest`**；團隊指派或部門樹規則不過回 `BadRequest`；版本號不符或名稱重複回 `Conflict`（409）；查無回 `NotFound`）|
| 刪除 | `DELETE {controller}/{id}` | Delete | `ApiResult`（**軟刪除**，0.9.94 起；查無或已刪除回 `NotFound`；團隊還有未刪除的下屬回 `BadRequest`）|

> **樂觀並行（0.9.93 起）**：`GET` 回傳的 DTO 帶有 `ConcurrencyStamp`。`PUT` 必須把它原樣帶回；伺服器以它比對資料庫目前的值，不同代表別人已先修改或刪除，回 409（訊息「這筆資料在你編輯期間已被其他人修改或刪除…」），用戶端應重新 `GET` 後再決定如何合併。`PUT` 成功後版本號會換新，要再次修改請重新 `GET`。`POST` 會忽略這個欄位。
>
> **軟刪除（0.9.94 起）**：`DELETE` 只把資料標記為已刪除，之後對同一筆的 `GET`／`PUT`／`DELETE` 都回 404。還原與永久刪除只能在畫面上的「顯示已刪除」操作，API 不提供。
>
> 0.9.93 之前三個 `POST` 會直接採用客戶端傳來的 `Id`，與既有資料撞號時回 500；0.9.94 起一律由資料庫配號。

各控制器對應的路由前綴與權限鍵：

| 控制器 | 路由前綴 | 權限鍵（`MagicObjectHelper`）| 檔案 |
|--------|----------|------------------------------|------|
| `CategoryController` | `api/Category`、`api/v1/Category` | `角色_分類清單` | `Controllers/CategoryController.cs` |
| `TeamController` | `api/Team`、`api/v1/Team` | `角色_團隊清單` | `Controllers/TeamController.cs` |
| `ProjectController` | `api/Project`、`api/v1/Project` | `角色_專案項目` | `Controllers/ProjectController.cs` |

## 三、專案附件下載 `ProjectFileController`

`src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/ProjectFileController.cs`，路由 `api/project-files`、`api/v1/project-files`。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 下載附件 | `GET project-files/{id}/download` | `[Authorize(CookieScheme)]` ＋ `[HasPermission(角色_專案項目, View)]` | 成功回**原生 File stream**；其餘回 `ApiResult` 404 |

與其他控制器不同的三點，都源自「呼叫端是畫面上的一般連結而非程式」：

- **驗證走 Cookie 不走 JWT**。連結由瀏覽器直接導覽（`<a href target="_blank">`），帶的是登入 Cookie。
- **路由是 kebab-case**，不套 `api/[controller]` 慣例，以維持 UI 既有網址。
- **成功時不包 `ApiResult`**，維持原生 File stream（`enableRangeProcessing: true`，支援 1GB 附件續傳）；只有錯誤才回 `ApiResult`。

查無紀錄、團隊越界（`ProjectService.GetFileDownloadAsync` 以 `IsTeamAccessible` 守門）、實體檔案不存在**一律回 404**，不以狀態碼洩漏檔案是否存在；三者差異由 Service 層的 Warning 日誌區分。成功下載寫入稽核 `Project.FileDownload`。

> **系統備份下載 `BackupController`（0.9.99 起）**：`src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/BackupController.cs`，路由 `api/backups`（**無** `api/v1` 平行路由）。`GET backups/{fileName}/download`，同樣走 `[Authorize(CookieScheme)]`、成功回原生檔案（`application/zip`，支援續傳）；不用 `[HasPermission]`，而是每次從資料庫確認是**管理員**，否則回 `ApiResult` 403；找不到備份回 `ApiResult` 404。第一段下載寫入稽核 `Backup.Download`（續傳的後續區段不重複寫）。

## 四、認證控制器 `AuthController`

`src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/AuthController.cs`，路由 `api/Auth`、`api/v1/Auth`；帳密換 JWT。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 登入 | `POST Auth/login` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（失敗回 `Unauthorized`；已啟用兩步驟驗證要在 body 帶 `TwoFactorCode`，沒帶回 401「需要兩步驟驗證碼。」且不計入失敗次數；必須使用兩步驟驗證卻還沒設定的帳號一律 401，0.9.104 起）|
| 換發 Token | `POST Auth/refresh` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（Refresh Token 無效、帳號已停用或刪除、工作階段已失效（0.9.103 起比對 `SecurityStamp`）都回 `Unauthorized`，並寫稽核 `Token.RefreshFailed`）|
| 目前使用者 | `GET Auth/me` | `[Authorize(JwtBearer)]` | `ApiResult<CurrentUserDto>`（讀 JWT Claims）|

## 五、Google 第三方登入 `ExternalAuthController`

`src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/ExternalAuthController.cs`，路由前綴 `Auths/Google`。**此為網頁 Cookie 登入導向端點，非 API**（回傳 `Challenge`／`Redirect`，不走 `ApiResult`）。詳見《[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)》。

| 動作 | 路由 | 說明 |
|------|------|------|
| 觸發 Google 驗證 | `GET Auths/Google/Login` | 未設定金鑰時導回 `/Auths/Login`|
| 驗證回呼 | `GET Auths/Google/Callback` | 查找/建立帳號；依 `ExternalLoginResult.Evaluate` 的順序：已刪除導回 `/Auths/Login?sso=deleted`、停用（待開通）導向 `/Auths/Pending`、鎖定導回 `/Auths/Login?sso=locked`；已啟用兩步驟驗證且這台裝置沒被記住時導向 `/Auths/TwoFactor`，其餘完成 Cookie 登入 |

## 六、Controller 端預設拒絕（0.4.34 起）

`Program.cs` 的 `app.MapControllers().RequireAuthorization()` 讓**未明確標註授權的 Controller 一律拒絕**。
需要匿名存取的端點必須自己標 `[AllowAnonymous]`（目前為 `AuthController` 的 login/refresh 與整個 `ExternalAuthController`）。

> 刻意**只**套在 Controller 上，不使用 `AuthorizationOptions.FallbackPolicy`。Blazor 頁面另有自己的
> 「預設需登入」機制（0.9.41 起）：`Components/Pages/_Imports.razor` 對整個 `Pages/` 標 `[Authorize]`，
> 登入頁等匿名頁面各自標 `[AllowAnonymous]` 並由 `PageAuthorizationTests` 白名單守門。
> 詳見 [開發慣例與限制速查 §5.1](開發慣例與限制速查.md)。

> 0.4.34 移除了模板遺留的 `WeatherForecastController`（無授權標註、路由不在 `/api` 之下、回傳裸物件）。

## 七、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)

> 返回 [architecture 索引](README.md)
