# Web API 設計慣例

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.1.61
- 最後核對日期：2026/10/23

## 目的
本文件記錄腳手架 Web API 的固定設計規範，未來新增 API 時應遵守同一套 contract，讓前端與外部用戶端能用一致格式處理成功、失敗、驗證錯誤、授權錯誤與例外。

## Controller 規範
- API Controller 放在 `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/`。
- 路由需同時提供 `[Route("api/[controller]")]` 與 `[Route("api/v1/[controller]")]`；新用戶端優先使用 `/api/v1/...`。
- Controller 必須使用 `[ApiController]` 與 `[ApiValidationFilter]`。
- 需要保護的 CRUD API 必須加上 `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`。
- 除驗證身分外，受保護動作方法**必須**以 `[HasPermission("resource", "action")]` 標註做**功能級／動作級授權**（見「功能級／動作級授權」節）。
- API request/response 必須使用 DTO，不可以直接接收或回傳 Entity。
- Entity 與 DTO 轉換優先使用 AutoMapper profile 維護。
- 寫入動作（新增、修改、刪除）成功後要寫稽核（0.9.78 起，LOG-14）：呼叫 `this.WriteAuditAsync(AuditActions.Xxx.Create, ...)`
  （`Controllers/ControllerAuditExtensions.cs`，操作者由 `RequestActorResolver` 從 JWT／Cookie 身分解析）。
  動作代碼一律引用 `CustomerHelpAgent.Business/Helpers/AuditActions.cs` 的常數，不可寫字串字面值（`AuditConventionTests` 守門）。
- 例外：`ProjectFileController`（附件下載）**只收 Cookie 驗證**、路由為 kebab-case 的 `api/project-files`（另有 `api/v1/project-files`），
  因為呼叫端是 Blazor 畫面上由瀏覽器直接導覽的連結，帶的是登入 Cookie 而不是 Bearer token。
  `BackupController`（系統備份下載，0.9.99 起）同理只收 Cookie，路由 `api/backups`（無 `api/v1`），改以「是否為管理員」判權。

## 統一回傳格式
所有一般 Web API 回應固定使用 `CustomerHelpAgent.Dtos.Commons.ApiResult<T>` 或非泛型 `ApiResult`。

標準欄位：
- `Success`：是否成功。
- `StatusCode`：HTTP 狀態碼。
- `Message`：使用者可讀訊息。
- `Data`：成功時的回傳資料。
- `Errors`：欄位或規則錯誤集合。
- `TraceId`：**錯誤追蹤碼**（0.9.78 起，LOG-10）。值是 8 碼短碼（例：`7K3QX9MD`），由 `UseHttpRequestLogging`
  以 `TraceCode.New()` 產生並**取代 `HttpContext.TraceIdentifier`**，因此與日誌檔的 TraceId 欄位、`/Error` 頁、
  系統例外紀錄的 `LastTraceId` 是同一個碼，管理員可在 `/logs` 以「錯誤追蹤碼」篩選找回同一次請求。
  目前會填入的路徑：`ApiExceptionFilter`（未處理例外）、`this.ApiServerError(...)`、JWT 的 401／403 事件；
  一般驗證錯誤（400）、`HasPermission` 的 403、限流的 429 不帶（為 `null`）。
- `Exception`：例外資訊。Development 或 `Security:ReturnExceptionDetails=true` 時回傳；Production 預設不回傳。

相容欄位：
- `ErrorMessage`：舊版錯誤訊息欄位，暫時保留。
- `ErrorDetail`：舊版錯誤詳細欄位，暫時保留。
- `Timestamp`：舊版時間戳欄位，暫時保留。

## HTTP 狀態碼
API 必須維持語意正確的 HTTP 狀態碼，Body 再包成 `ApiResult<T>`。

| 狀態碼 | 使用情境 |
| --- | --- |
| 200 | 查詢、更新、刪除等成功 |
| 400 | ModelState 或商業規則驗證失敗（例：指派了自己範圍外的團隊、部門樹的上層不合法、還有下屬的團隊不可刪除）|
| 401 | 未登入、未提供 Bearer token、token 無效 |
| 403 | 已登入但權限不足（由 `HasPermissionAttribute` 判定產生） |
| 404 | 找不到指定資源 |
| 409 | 資料衝突：名稱重複，或 `PUT` 帶來的 `ConcurrencyStamp` 與資料庫不符（別人已先修改，0.9.93 起）|
| 429 | 超過限流配額（`api` 分區；登入端點另有較嚴的配額），Body 同樣是 `ApiResult` |
| 500 | 未預期例外 |

## JWT 與 Swagger
Swagger 已設定 Bearer security definition。開發者可在 Swagger UI 透過 Authorize 輸入 JWT access token 後測試受保護 API。

JWT access token 與 refresh token 由 `AuthController` 提供：
- `POST /api/Auth/login`
- `POST /api/Auth/refresh`
- `GET /api/Auth/me`
- `POST /api/v1/Auth/login`
- `POST /api/v1/Auth/refresh`
- `GET /api/v1/Auth/me`

## 功能級／動作級授權
身分驗證（`[Authorize]`）之外，受保護 CRUD **每個動作方法**再以 `[HasPermission(頁面鍵, 動作)]`（`src/CustomerHelpAgent/CustomerHelpAgent.Web/Filters/HasPermissionAttribute.cs`）強制授權：

- 由 `IPermissionChecker`（`CustomerHelpAgent.Business/Services/Other/PermissionChecker.cs`，UI 與 API 共用的單一 RBAC 權威來源）判權；**管理員短路**一律通過。
- 動作對應：GET/`search`→`view`、POST→`create`、PUT→`edit`、DELETE→`delete`（動作常數見 `CustomerHelpAgent.Share/Helpers/PermissionKeys.cs`）。
- **權限鍵兩型**：裸頁面鍵（如 `專案項目`）＝該頁全動作（向後相容）；動作鍵（如 `專案項目:edit`）＝特定動作。「唯讀角色」只給 `專案項目:view`。
- 無權限一律回 **403**，Body 維持 `ApiResult` 格式並標示缺少的權限鍵。多角色時權限取聯集。
- 詳見 [認證授權與權限機制](../security/認證授權與權限機制.md)。

## 例外與驗證
- `ApiValidationFilter` 會將 ModelState 錯誤轉成 `ApiResult<T>.Errors`。
- `ApiExceptionFilter` 會攔截未處理 API 例外，依 `Security:ReturnExceptionDetails` 決定是否填入 `ApiResult.Exception`。
- Controller 自行 `catch` 後回 500 時，請用 `this.ApiServerError(...)`
  （`Controllers/ControllerApiResponseExtensions.cs`），它**同樣**遵守該設定。
  兩條路徑共用 `Configuration/ExceptionDetailPolicy` 這個單一判斷來源。
  ⚠️ **不要自己組 `ApiResult.ServerErrorResult(message, exception)` 回傳** ——
  那個多載會無條件塞入 `exception.ToString()` 與堆疊追蹤，Production 會外洩。

> 沿革：0.4.34 之前 `ApiServerError` 正是這樣繞過設定的（Controller 中共 16 處），
> 導致 Production 設了 `ReturnExceptionDetails: false` 仍回傳完整堆疊。
> 已由 `ApiExceptionDetailSuppressionTests` 兩條路徑一起守門。

- 例外本身請由呼叫端自行 `logger.LogError(ex, ...)` 留在**伺服器日誌**；
  要不要回傳給呼叫端，一律交由 `ExceptionDetailPolicy` 決定，不要在 Controller 內自行判斷。

## 待辦
- [x] 補完整 integration tests，實際驗證 400、401、403、404、409、500 的 HTTP body。
- [x] 正式環境預設隱藏 `Exception.StackTrace`。
- [x] 已導入 `/api/v1/...` 平行路由，目前保留 `/api/...` 相容路由。
