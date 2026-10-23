# 登入與帳號流程 PRD

- 文件版本：1.16
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供本專案的身分驗證與帳號自助維護能力，涵蓋本地帳密登入、Google OAuth2 第三方登入、登出、待審核導向、變更密碼，以及忘記密碼／以 Email 連結重設密碼（0.9.60 起）。網頁採 Cookie 驗證、API 採 JWT Bearer，兩者共用同一份使用者與 RBAC 權威來源。

- **範圍**：`/Auths/Login`、`/Auths/TwoFactor`（0.9.104 起，登入第二步）、`/Auths/Logout`、`/Auths/Pending`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/ChangePassword`；Google 導向端點 `/Auths/Google/Login`、`/Auths/Google/Callback`；API 端 `/api/v1/auth/login`、`/refresh`、`/me`。帳號安全（PBKDF2 雜湊、登入失敗鎖定、登入時雜湊自動升級）。
- **非範圍**：帳號 CRUD 與角色／團隊指派見 [使用者管理](使用者管理-prd.md)；角色與權限矩陣見 [角色管理](角色管理-prd.md)。兩步驟驗證的設定頁、備用碼、強制與管理員重設見 [兩步驟驗證](兩步驟驗證-prd.md)；本 PRD 只描述登入流程裡的第二步。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/Auths/Login` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名 | 所有人 |
| `/Auths/Logout` | `EmptyLayout`（0.9.95 起；之前沒有指定而套用預設的 `MainLayout`）| 已登入 | 所有登入者 |
| `/Auths/Pending` | `NoFooterLayout` | 匿名 | Google 自動建帳待審核者 |
| `/Auths/TwoFactor` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名＋有效的待驗證 Cookie | 已啟用兩步驟驗證、密碼或 Google 已通過的人（0.9.104 起）|
| `/Auths/ForgotPassword` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名 | 忘記密碼者（0.9.60 起；寄信未啟用時只顯示「未啟用」）|
| `/Auths/ResetPassword?token=…` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名＋有效 token | 收到重設信的人（0.9.60 起）|
| `/ChangePassword` | 預設版面 | 已登入 | 需變更密碼者 |
| `/api/v1/auth/login`、`/refresh` | — | 匿名（帳密換 JWT）| API 用戶端 |
| `/api/v1/auth/me` | — | JWT Bearer | API 用戶端 |

## 三、畫面與欄位

- **登入頁**：帳號、密碼、驗證碼（4 碼數字，前端產生存於隱藏欄位 `CaptchaCode`，以 `Ordinal` 比對）、「記住我」核取方塊。`GoogleOAuthSettings.IsConfigured` 為真時顯示「使用 Google 登入」按鈕，連結至 `/Auths/Google/Login`（帶 `returnUrl`）。前端逐項檢查空值與驗證碼，錯誤顯示「請輸入帳號／密碼／驗證碼」「驗證碼錯誤」。
- **登入頁（0.9.60 起）**：`EmailSettings:Provider` 不是 `None` 時，「記住我」右側的「企業級安全登入」改為「忘記密碼？」連結；從重設頁成功導回（`?reset=1`）時，表單上方顯示綠色「密碼已重設，請以新密碼登入。」。外觀由共用元件 `AuthShell` 提供，三頁一致。出貨與開發環境都是 `None`（0.9.62 起；0.9.61 曾在 `appsettings.Development.json` 預設 `Pickup`），預設看不到；本機要測，用 User Secrets 設 `Provider=Pickup`。
- **忘記密碼頁**：帳號或 Email、驗證碼（與登入頁同一套 4 碼）。送出後**一律**以中性色說明框（`.info-message`）顯示「申請已送出。若帳號存在、為啟用狀態且登記了有效的 Email，您會在幾分鐘內收到重設密碼的信，信中連結 N 分鐘內有效。若一直沒收到…請聯絡系統管理員協助。」—— 不論帳號是否存在、有沒有真的寄出（0.9.85 起；之前是綠色「…信已經寄出…」）。唯一例外：輸入 `support`（不分大小寫）時留在表單，紅字顯示「support 是系統內建帳號，不提供忘記密碼服務…」。寄信未啟用時整頁只顯示「系統目前未啟用寄信功能…請聯絡系統管理員」。
- **重設密碼頁**：副標題顯示「為帳號「X」設定新密碼。密碼規則：…」（0.9.101 起由 `IPasswordPolicy.Describe()` 產生）；新密碼、確認新密碼。token 無效／過期／已用過一律顯示「重設連結無效或已過期，請重新申請。」並提供「重新申請重設信」連結。成功後導回登入頁。
- **待審核頁**：靜態說明，告知 Google 帳號已建立但預設停用，須管理者啟用後再登入，提供返回登入頁連結。
- **變更密碼**：目前密碼、新密碼（下方顯示密碼規則）、確認新密碼（`[Compare]` 驗證一致）。`support` 開發帳號被禁止。0.9.101 起被導到這一頁時上方說明原因（管理員要求／密碼已到期）；右上角使用者選單的「變更密碼」直接開這一頁（原本的對話窗已移除）。
- **兩步驟驗證頁（0.9.104 起）**：「驗證碼或備用碼」（App 上的 6 位數或一組 `XXXXX-XXXXX` 備用碼）、「記住這台裝置 N 天」（`TwoFactorSettings:RememberDeviceDays` 為 0 時不顯示）、「驗證並登入」、「取消，回到登入頁」。錯誤一律「驗證碼不正確，或帳號已被暫時鎖定。」。
- **登入頁的鎖定說明（0.9.101 起）**：表單下方固定顯示「連續輸錯 N 次密碼會暫時鎖定 M 分鐘；被鎖定時可以用「忘記密碼」重設，或請系統管理員解鎖。」（N、M 取自 `LockoutSettings`；寄信停用時改為「請稍後再試」）。

## 四、內部系統運作

- **本地登入**（`MyUserServiceLogin.LoginAsync`，View → Service → `BackendDBContext`）：以 `Account` 查 `MyUser`；帳號不存在、密碼錯誤、鎖定中一律回 `InvalidCredentialsMessage`「帳號或密碼不正確，或帳號已被暫時鎖定。」（0.9.101 起；不區分，避免帳號枚舉）。密碼以 `SecurePasswordHasher.VerifyPassword`（PBKDF2）驗證。
- **帳號鎖定**（0.9.101 起讀 `LockoutSettings`，系統參數可調）：失敗時 `AccessFailedCount++`，達 `MaxFailedAttempts`（預設 5）設 `LockoutEndUtc = UtcNow + LockoutMinutes`（預設 15 分）並通知全體管理員（同時寄信）；
  鎖定期間不驗證密碼、不累加次數，稽核記 `Login.Failed`（`reason=Locked`），`Login.LockedOut` 只記造成鎖定的那一次。⚠️ 鎖定**到期時先歸零再驗證**（之前到期後錯一次就再鎖）。成功登入後 `AccessFailedCount` 歸零。
- **舊的 123456**：登入成功時明文是 `123456` 就設 `MustChangePassword`（舊資料只有雜湊，無法用 SQL 轉換）。
- **Google 登入**：處理順序集中在 `ExternalLoginResult.Evaluate`：已刪除 → 待開通 → **鎖定（0.9.101 起，導回 `?sso=locked`）** → 登入。
- **雜湊自動升級**：驗證回傳 `SuccessRehashNeeded`（舊格式）時，即時以 PBKDF2 重新雜湊並存回。
- **Cookie 簽發**（Login.razor.cs，claim 由 `Auth/CookieClaims.Create` 組成）：建立 `ClaimTypes.Role=User`、`Name`、`NameIdentifier=Account`、`Sid=Id`、`security_stamp`（0.9.103 起），以 `CookieAuthenticationScheme` `SignInAsync`；`IsPersistent = RememberMe`（記住我 → 持久性 Cookie），`RedirectUri` 取 `ReturnUrl` 或 `/App`。
- **Google 登入**（`ExternalAuthController` + `ExternalLoginService.FindOrCreateAsync`）：Callback 驗證 `ExternalCookieScheme` 後，依序「有效的 GoogleId → 已刪除的 GoogleId（拒絕）→ 有效的 Email 連結既有帳號 → 已刪除的 Email（拒絕）→ 自動建立停用新帳號」（`Status=false`、`IsAdmin=false`、`Password=""`、`Salt=null`、指派預設角色）。命中已刪除的使用者（0.9.95 起）時不寫入、不連結、不新建，稽核 `Login.Sso.Failed`（`reason=Deleted`）並導向 `/Auths/Login?sso=deleted`，登入頁顯示固定文字「此帳號已被刪除，請洽系統管理員。」（`sso` 只認得固定代碼，其他值一律忽略，不回顯網址內容）。GoogleId 排在 Email 前面（使用者決定）：否則已刪除者的 Google 帳號會被連到同 Email 的另一人，之後他就因 GoogleId 衝突而無法還原。`!Status` 導向 `/Auths/Pending`，否則簽發 Cookie 並導回本地安全的 `returnUrl`。
- **登出**：`SignOutAsync(CookieScheme)` 後 `NavigateTo("/Auths/Login", forceLoad: true)`。
  - **使用者主動登出**（0.9.29 起）先經二次確認：三個 UI 入口都走
    `Components/Commons/LogoutConfirm.cs`，確認後才導向 `/Auths/Logout`。
  - ⚠️ **系統強制登出不確認**：`AuthenticationStateHelper` 的六處
    `NavigateTo("/Auths/Logout", true, true)`（無效 Sid／查無使用者／工作階段版本不符（`?reason=session`）／停用／無角色／壞 RoleJson；
    未驗證則導向 `/Auths/Login`、不清 Cookie，0.9.39 起）是系統行為，直接登出 —— 對 session 已經失效的人跳「確定要登出嗎？」只會讓他卡住。
    這兩條路徑在分層上就分開：`LogoutConfirm` 在 Web 層，`AuthenticationStateHelper` 在 Business 層，
    後者參照不到前者。
  - 直接在網址列輸入 `/Auths/Logout` 維持立即登出，刻意不擋（刻意輸入網址不是誤觸）。
  - ⚠️ **登出頁不可套用 `MainLayout`**（0.9.95 修正）：`MainLayout` 會先跑 `Check`，使用者已停用或已刪除時又導向 `/Auths/Logout`，而這個導向發生在登出頁清掉 Cookie 之前 —— 瀏覽器顯示「重新導向太多次」，被停用的人永遠回不到登入頁（至少從 0.9.41 起）。現在指定 `EmptyLayout`；由 `PageAuthorizationTests.Logout_WithLiveCookieOfDisabledOrDeletedUser_ShouldRedirectToLogin` 守門。
- **工作階段失效**（0.9.103 起）：登入 Cookie 與 JWT 帶著 `MyUser.SecurityStamp`（`CookieClaims.Create`、`JwtTokenService`）；改密碼、停用、刪除、角色或管理員身分變更、管理員強制登出時換版本。
  `SecurityStampCookieEvents` 在 HTTP 請求上比對（快取 `CookieSettings:ValidationIntervalMinutes`）、`Check` 在換頁時比對（不符 → `/Auths/Logout?reason=session`、稽核 `Login.SessionExpired`）、refresh 比對（不符 401）。
  Bearer 只接受 access token。在 `/ChangePassword` 改自己的密碼後經 `/Auths/RefreshSession` 換發這台的 Cookie（一次性 ticket，須帶著原本的 Cookie）。
- **兩步驟驗證的第二步**（0.9.104 起）：帳號已啟用時 `LoginAsync` 回 `RequiresTwoFactor`（⚠️ 不歸零失敗次數、不寫 `Login.Success`）；登入頁寫入 5 分鐘的加密 Cookie `.CustomerHelpAgent.TwoFactorPending`（使用者 Id、工作階段版本、記住我、返回網址）並導向 `/Auths/TwoFactor`。
  這台裝置被「記住」而且版本沒換時直接完成（稽核 detail `mfa=device`）。送出時重查版本與帳號狀態（期間改了密碼或被強制登出 → 回登入頁），`CompleteSecondFactorAsync` 驗證碼對了才歸零、`SignInAsync`、稽核 `Login.Success`（`mfa=totp|backup`）；錯了稽核 `Login.TwoFactorFailed` 並累加鎖定次數。
  Google 回呼：帳號已啟用時同樣走第二步（稽核 `Login.Sso.Success`，detail 帶 `provider=Google; mfa=…`）。API 登入以 `twoFactorCode` 欄位帶驗證碼。
- **登入後狀態**（`AuthenticationStateHelper.Check`）：驗證已登入、`Sid` 有效、使用者存在且 `Status` 啟用、具角色；`IPasswordPolicy.RequiresChange`（0.9.101 起：`MustChangePassword` 旗標或密碼已到期；support 與沒有本機密碼的帳號豁免）為真且不在改密碼頁時強制導向 `/ChangePassword`；之後（0.9.104 起）必須使用兩步驟驗證卻還沒設定、而且不在設定頁時導向 `/TwoFactorSetup`。載入 `CurrentUser`，`RoleList` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync`（RBAC 多角色聯集）為權威、`TeamList` 由 `EffectiveTeamResolver` 決定。
- **API 登入**（`AuthController`）：`login` 以帳密（已啟用兩步驟驗證時加 `twoFactorCode`）換 `TokenResponseDto`（JWT + Refresh），`refresh` 換新 Token，`me` 回目前使用者；一律包 `ApiResult<T>`，失敗回 401。
- **稽核**：登入寫入 `Login.Success` / `Login.Failed` / `Login.Disabled` / `Login.LockedOut` / `Login.TwoFactorFailed`，Google 登入寫 `Login.Sso.Success` / `Login.Sso.Failed`，登出寫 `Logout`，工作階段失效被系統登出寫 `Login.SessionExpired`（`AuditLog`）；忘記密碼寫入 `Password.ResetRequested` / `Password.ResetCompleted` / `Password.ResetFailed`（見下）。
- **登入紀錄的來源 IP**（0.9.117 起寫入，0.9.123 起顯示）：每筆稽核由 `AuditLogService` 經 `IClientIpProvider` 自動帶入 `AuditLog.ClientIp`（登入頁、兩步驟驗證頁、登出是 SSR，取 `HttpContext`；API 登入同），呼叫端不傳。首頁「我的帳號」的「上一次登入」（`ProfileService.GetPreviousLoginAsync`：`Login.Success`／`Login.Sso.Success` 的倒數第二筆）在登入方式後面顯示 IP（有值才顯示）；個人資料頁「最近 20 筆登入紀錄」（`GetRecentLoginsAsync`：`Login.*` 與 `Logout`）欄位為「時間、動作、來源 IP、結果」，0.9.117 之前的舊紀錄顯示「—」。見[個人資料](個人資料-prd.md)、[首頁儀表板](首頁儀表板-prd.md)。
- **忘記密碼 → 重設**（`PasswordResetService`，0.9.60 起；Business 層、注入 `IDbContextFactory`）：
  1. `RequestAsync(identifier, 重設頁網址)`：先刪所有過期 token；以 `Account` 精確比對（與登入相同），對不到再以 `Email` 不分大小寫比對（可能多筆 → **每個帳號各寄一封**）。
  2. 逐帳號判斷資格：`support`、停用（`Status=false`）、沒有本地密碼（Google-only，`Password=""`）、Email 空白或無效 → 不寄；同帳號 `RequestCooldownSeconds`（預設 60 秒）內已申請過 → 不寄。原因寫進稽核 detail（`reason=NotFound|Support|Disabled|NoLocalPassword|InvalidEmail|Cooldown|QueueFull`），畫面不透露。`RequestAsync` 回傳 `PasswordResetRequestResult`：輸入內建帳號名稱回 `SupportAccountNotAllowed`（大小寫不同也先正規化成 `support`，稽核照記 `reason=Support`），其餘一律 `Accepted`。
  3. 符合資格：刪掉該帳號舊 token → 產生 32 bytes 亂數（Base64Url）→ **資料表 `PasswordResetToken` 只存 SHA-256** 與到期時間（`TokenLifetimeMinutes`，預設 30 分；0.9.98 起可在「系統參數」頁修改，`PasswordResetService` 以 `IOptionsMonitor` 即時讀取，只影響之後寄出的連結）→ 重設信交給 `IEmailQueue` 背景寄出（回應時間不因「有寄信」而變長）。
  4. 重設頁開啟：`ValidateTokenAsync` 只讀不寫，決定顯示表單或「連結無效」。
  5. `ResetAsync`：先驗密碼規則（0.9.101 起依 `PasswordPolicySettings`）與兩次一致，再查歷史（不可與最近 N 次相同，含目前的），**不過時不消耗 token**；再於交易內以 `ExecuteDelete` **搶占** token（刪到 1 列才繼續，並發送出兩次只有一次成功）→ `IPasswordPolicy.ApplyAsync`（雜湊、記設定時間、清旗標、解除鎖定、寫歷史）→ 刪除該帳號其餘 token → commit → 稽核 `Password.ResetCompleted` → 背景寄「密碼已變更」通知信。
  6. 信中連結的網址基準是 `EmailSettings:PublicBaseUrl`；留白時只有非 Production 會退回目前請求的網址（不信任 Host header）。

## 五、權限與安全

- 網頁 Cookie、API JWT 各自獨立；權限判定統一由 RBAC 表（`IPermissionChecker`）為單一權威，管理員短路一律通過。
- 錯誤訊息不洩漏帳號是否存在（登入與忘記密碼皆然；忘記密碼的信一律走背景佇列，回應時間也一致）；輸出模型不含密碼、`Salt`、Token（`MyUserService.OtherDependencyData` 清空密碼欄位）。
- 帳號停用者於 `Check` 一律導回登出；Google 自動建帳預設停用並導向待審核，須管理者啟用。
- 重設 token 只存 SHA-256、效期 30 分、單次使用；新申請與重設成功都作廢該帳號其餘 token。重設頁回應 `Referrer-Policy: no-referrer` 與 `Cache-Control: no-store`。
- 日誌不記錄忘記密碼輸入的帳號／Email、token 與連結；稽核的 `Password.ResetRequested`（NotFound）會記下輸入值（截 64 字），與 `Login.Failed` 記錄輸入帳號的作法一致。
- 密碼雜湊與儲存、記住我原理、Google 流程細節見交叉文件，本 PRD 不重述。

## 六、錯誤與邊界

- 驗證碼錯誤／欄位空白：停留登入頁並重新產生驗證碼。
- 連續輸錯 `MaxFailedAttempts` 次鎖定 `LockoutMinutes` 分鐘（預設 5 次、15 分鐘）；鎖定到期後可再次登入，失敗次數從 0 重新算；管理員可在使用者清單立即解鎖。
- 兩步驟驗證：驗證碼錯、備用碼用過、同一組驗證碼第二次使用 → 「驗證碼不正確，或帳號已被暫時鎖定。」並計入鎖定次數；待驗證 Cookie 超過 5 分鐘或被竄改 → 回登入頁重新輸入密碼。
- 帳號被鎖定時用 Google 登入：導回 `/Auths/Login?sso=locked`，顯示「此帳號因連續輸錯密碼已暫時鎖定…」（Google 已驗證身分，明確告知無洩漏疑慮）。
- Google Callback 缺 `subject`／`email`：登出外部身分並導回登入頁。
- 已刪除的使用者（0.9.95 起）：密碼登入回與密碼錯誤相同的訊息（不透露帳號曾存在）；Google 登入導回 `/Auths/Login?sso=deleted`（Google 已驗證身分，告知原因無洩漏疑慮）。
- `support` 帳號於 `/ChangePassword` 一律被拒。Google 帳號只用於網頁登入，不支援 API（0.9.63 起移除「設定 API 密碼」；`/Profile` 自 0.9.102 起是與 API 無關的[個人資料](個人資料-prd.md)頁）。
- 使用者無角色、`RoleView` 為 null 或 `TabViewJson` 解析失敗：導向登出。
- 忘記密碼：輸入空白 →「請輸入帳號或 Email」；驗證碼錯 → 重新產生驗證碼；查無帳號、不符資格、冷卻中 → 畫面與成功時相同；背景佇列滿 → 不寄（稽核 `QueueFull`）；資料庫錯誤 →「系統暫時無法處理您的申請，請稍後再試。」。
- 重設：連結過期、已用過、偽造、或申請後帳號被停用 → 一律「重設連結無效或已過期」；密碼規則不過 → 顯示原因、連結仍可再用。

### 6.1 已知限制（忘記密碼）

- **其他工作階段失效**（0.9.103 起）：重設密碼會換掉工作階段版本，其他裝置上已開著的頁面在下一次換頁被登出、新的 HTTP 請求最晚 5 分鐘、API 無法再 refresh，已發出的 access token 也在同樣的時間內失效（0.9.136 起；之前在 60 分鐘效期內仍可用）。
- **沒有依 IP 限流**：`/Auths/*` 不在 `api` 限流政策內；防線是同帳號冷卻時間。驗證碼答案在頁面隱藏欄位，只是減速帶，擋不了腳本。
- token 在網址 query 裡：IIS 的 W3C 日誌、或開啟 `Microsoft.AspNetCore.Hosting.Diagnostics` 的 NLog 規則會記到完整網址。
- 背景佇列只在記憶體：程式重啟時未寄出的信會遺失，使用者重新申請即可。
- Email 不分大小寫比對由 SQLite `lower()` 處理，只摺 ASCII。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/MyUserServiceLoginTests.cs`：新舊雜湊登入、舊雜湊自動升級、密碼錯誤、5 次失敗鎖定並拒絕正確密碼、成功後歸零、鎖定到期放行、成功／失敗稽核。
- `CustomerHelpAgent.Tests/MyUserServicePasswordTests.cs`：`ChangeOwnPasswordAsync` 正確／錯誤舊密碼、空白新密碼、確認不一致、`support` 帳號被拒。
- `CustomerHelpAgent.Tests/SecurePasswordHasherTests.cs`：自述式雜湊、非決定性、新舊格式驗證與要求 rehash。
- `CustomerHelpAgent.Tests/AuthenticationStateHelperTests.cs`：未驗證導向登入頁且不清 Cookie、無效 Sid／查無使用者／停用／已刪除／無角色／壞 RoleJson 導向登出、旗標與到期導向改密碼頁（0.9.101 起；只有 123456 而沒有旗標不導向）、多角色聯集初始化。
- `CustomerHelpAgent.Tests/SessionTests.cs`（0.9.103）：⭐ 換版本的時機（改密碼換、登入時舊雜湊升級不換、只改姓名或 Email 不換、停用／管理員／主要或額外角色變更換、刪除與強制登出換、support 設定密碼變更才換）、空版本登入時補上、快取存活與輪替立即清除、空字串一律不符、ticket 一次性／竄改／過期；
  `SessionIntegrationTests`：換版本後與沒有版本的 Cookie 被拒、停用的帳號 Cookie 被拒、⭐ refresh token 不能當 Bearer、換版本後 refresh 401 並稽核、⭐ 換發頁只接受原本的 Cookie 與資料庫目前的版本、ticket 不能重用。
- `CustomerHelpAgent.Tests/PasswordPolicyTests.cs`（0.9.101）：規則邊界、歷史（目前、第 N、第 N+1、0 關閉、裁切、舊格式目前密碼）、必須變更（旗標、到期邊界、support 與 Google 豁免）、新增／修改使用者與變更密碼都套用原則、⭐ 雜湊只准出現在政策服務等三處、⭐ 鎖定門檻與到期先歸零、稽核標籤、同一則訊息、只通知一次、123456 登入補旗標、解鎖不換版本號、Google 被鎖定擋下、到期提醒作業。
- `CustomerHelpAgent.Tests/TotpServiceTests.cs`：TOTP 產碼／驗證（RFC 6238 測試向量）。
- `CustomerHelpAgent.Tests/TwoFactorTests.cs`（0.9.104）：登入兩段（⭐ 密碼對了不歸零、⭐ 重輸密碼也洗不掉驗證碼失敗次數、成功稽核帶方式、停用帳號不能完成第二步）、待驗證與記住裝置 Cookie；`TwoFactorIntegrationTests`：API 三種情況、⭐ Google 登入已啟用的帳號要第二步。其餘見[兩步驟驗證 PRD](兩步驟驗證-prd.md)。
- `CustomerHelpAgent.Tests/PasswordResetServiceTests.cs`（0.9.60）：帳號比對、Email 不分大小寫、帳號優先、共用 Email 各寄一封、查無只稽核、五種不符資格、Google 有本地密碼允許、冷卻、新申請作廢舊連結、只存雜湊、過期、重設成功（新密碼可驗＋解鎖＋token 清空＋通知信）、同連結第二次失敗、規則不過不消耗 token、申請後停用、刪除使用者連帶刪 token（cascade）。
- `CustomerHelpAgent.Tests/EmailPagesIntegrationTests.cs`（0.9.60）：Pickup 時登入頁有連結、忘記頁有表單、壞 token 重設頁回 `no-referrer`／`no-store` 且不給表單；None 時沒有連結、兩頁顯示未啟用。
- `CustomerHelpAgent.Tests/PageAuthorizationTests.cs`：匿名白名單含 `ForgotPassword`、`ResetPassword`、`TwoFactor`。
- `CustomerHelpAgent.Tests/ProfileTests.cs`、`Components/DashboardTests.cs`（0.9.123）：登入紀錄帶出來源 IP（舊紀錄為 null）、「我的帳號」顯示上一次登入的 IP。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Auths/Login.razor`、`Login.razor.cs`（登入表單與 Cookie 簽發）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Auths/AuthShell.razor`（登入／忘記／重設三頁共用外框）、`ForgotPassword.razor(.cs)`、`ResetPassword.razor(.cs)`、`AuthCaptcha.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Auths/Logout.razor.cs`、`Pending.razor`、`TwoFactor.razor(.cs)`（0.9.104）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Auth/TwoFactorLoginCookies.cs`（待驗證與記住裝置 Cookie）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Pages/ChangePassword.razor`
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/MyUserServiceLogin.cs`（鎖定、PBKDF2、升級、稽核）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/ExternalLoginService.cs`（Google 查找／建立）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/PasswordResetService.cs`（忘記密碼）、`src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/PasswordResetToken.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Models/Systems/PasswordResetSettings.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Email/PublicBaseUrlResolver.cs`（信中連結的網址基準）
- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/AuthenticationStateHelper.cs`（登入後檢查與 RBAC 載入）
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/AuthController.cs`、`ExternalAuthController.cs`
- 交叉連結：[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[寄信服務](寄信服務-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)、[記住我登入原理說明](../security/記住我登入原理說明.md)
