# Google OAuth2 第三方登入

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.2.6
- 最後核對日期：2026/10/23

## 目的

本文件說明如何在本系統啟用「使用 Google 帳號登入」，並串接既有的權限控管（權控）與 Web API（JWT）機制。

設計核心：**Google OAuth2 只負責「網頁 Cookie 登入」這條路徑**；API 仍維持既有的「帳號＋密碼換 JWT」機制。因此 `AuthController` 與 `JwtTokenService` 完全沒有更動，Google 帳號只用於網頁登入，**不支援 API**（0.9.63 起移除「設定 API 密碼」）；要呼叫 API 請使用本地帳號。詳見「認證授權與權限機制」一文。

---

## 行為總覽（定案規則）

| 項目 | 行為 |
|------|------|
| 帳號開通 | 首次以 Google 登入時，**自動建立**本地使用者，以 Google 的 Email 作為帳號名稱（`Account`） |
| 網域限制 | **不限制**，任何 Google 帳號皆可進行驗證 |
| 預設角色 | 自動建立的帳號套用設定中的 `DefaultRoleName`（預設「預設角色」） |
| 資安閘門 | 新帳號預設 `Status = false`（**停用**），需由管理者於「使用者管理」**啟用並確認角色**後才能登入 |
| Email 連結 | 若已存在相同 Email 的本地帳號，**自動連結**（寫入 GoogleId，不重建、不覆寫狀態與權限） |
| 網頁登入 | 純 Google 單一登入（SSO），**不需設定密碼** |
| API / JWT | **不支援**（0.9.63 起）：Google 帳號沒有本地密碼，無法呼叫 `/api/v1/auth/login`；要呼叫 API 請使用本地帳號 |

---

## 一、在 Google Cloud Console 建立 OAuth 用戶端

1. 進入 [Google Cloud Console](https://console.cloud.google.com/) 並建立（或選擇）一個專案。
2. 設定「OAuth 同意畫面」（OAuth consent screen）：
   - 使用者類型：內部（限 Google Workspace 組織）或外部皆可。
   - 填寫應用程式名稱、支援電子郵件等必填欄位。
3. 前往「憑證」（Credentials）→「建立憑證」→「OAuth 用戶端 ID」：
   - 應用程式類型：**Web application（網頁應用程式）**。
   - **授權重新導向 URI（Authorized redirect URIs）** 必須填入本系統的回呼路徑（網址結尾固定為 `/signin-google`）：
     - 開發（https）：`https://localhost:7067/signin-google`
     - 開發（http）：`http://localhost:5066/signin-google`
     - 正式：`https://<your-domain>/signin-google`
   - `/signin-google` 是 Google 中介軟體預設的回呼路徑（對應 `CallbackPath`），請務必與系統設定**完全一致**（差一個字 Google 就會拒絕）。本機開發埠以 `Properties/launchSettings.json` 為準；用 `scripts/New-StarterProject.ps1` 產生的衍生專案會拿到另一組隨機埠，腳本結尾會印出要註冊的 redirect URI。
4. 建立完成後，畫面會顯示 **用戶端 ID（ClientId）** 與 **用戶端密鑰（ClientSecret）**，複製下來填入下一節的設定。

---

## 二、系統設定

設定區段位於 `appsettings.json` 的 `GoogleOAuthSettings`，並綁定到強型別 `CustomerHelpAgent.Web.Auth.GoogleOAuthSettings`：

```json
"GoogleOAuthSettings": {
  "Enabled": false,
  "ClientId": "",
  "ClientSecret": "",
  "DefaultRoleName": "預設角色"
}
```

| 欄位 | 該填什麼 | 範例 |
|------|----------|------|
| `Enabled` | 是否啟用 Google 登入。要用就設 `true`；`false` 時不註冊 Google 驗證，登入頁也不顯示按鈕 | `true` |
| `ClientId` | Google Cloud Console 產生的「用戶端 ID」 | `123456789-abc123.apps.googleusercontent.com` |
| `ClientSecret` | Google Cloud Console 產生的「用戶端密鑰」 | `GOCSPX-xxxxxxxxxxxxxxxxxxxx` |
| `DefaultRoleName` | 新帳號自動套用的角色名稱，**保持 `預設角色` 即可**（對應系統 seeding 出來的角色） | `預設角色` |

> ⚠️ **`ClientId` 與 `ClientSecret` 不是自己輸入任意字串，必須先到 Google Cloud Console 申請**（見上方「一、在 Google Cloud Console 建立 OAuth 用戶端」）。未申請或填錯，Google 會在登入時拒絕。

啟用步驟：將 `Enabled` 設為 `true`，並填入向 Google 申請到的 `ClientId` 與 `ClientSecret`。`DefaultRoleName` 一般維持 `預設角色`。

### 填入方式（擇一）

**方式 A：user-secrets（開發環境建議）**

`ClientSecret` 屬機密，**不應寫入版本控管的 `appsettings.json`**。專案已設定 `UserSecretsId`，可直接於 `CustomerHelpAgent.Web` 目錄執行：

```powershell
cd src\CustomerHelpAgent\CustomerHelpAgent.Web
dotnet user-secrets set "GoogleOAuthSettings:Enabled" "true"
dotnet user-secrets set "GoogleOAuthSettings:ClientId" "你的用戶端ID"
dotnet user-secrets set "GoogleOAuthSettings:ClientSecret" "你的用戶端密鑰"
```

`appsettings.json` 內的該區段則維持留空當預設值即可。

**方式 B：環境變數（正式環境建議）**

鍵名以雙底線分隔：

```
GoogleOAuthSettings__Enabled=true
GoogleOAuthSettings__ClientId=你的用戶端ID
GoogleOAuthSettings__ClientSecret=你的用戶端密鑰
```

**方式 C：直接寫入 `appsettings.json`（僅限本機快速測試）**

```json
"GoogleOAuthSettings": {
  "Enabled": true,
  "ClientId": "123456789-abc123.apps.googleusercontent.com",
  "ClientSecret": "GOCSPX-xxxxxxxxxxxxxxxxxxxx",
  "DefaultRoleName": "預設角色"
}
```

> ⚠️ 方式 C 會讓密鑰進版控，**正式上線前務必改用方式 A / B，並把密鑰從 `appsettings.json` 移除**。

只有在 `Enabled = true` 且 `ClientId`、`ClientSecret` 皆非空（`GoogleOAuthSettings.IsConfigured`）時，系統才會在 `Program.cs` 註冊 Google 驗證與外部暫存 Cookie；填好後重啟，登入頁就會出現「使用 Google 登入」按鈕。

---

## 三、驗證流程

### 元件與 scheme

- **主 Cookie scheme**：`MagicObjectHelper.CookieScheme`（既有網頁登入身分）。
- **外部暫存 Cookie scheme**：`MagicObjectHelper.ExternalCookieScheme`（OAuth 流程中暫存 Google 身分，效期 5 分鐘）。
- **Google scheme**：`GoogleDefaults.AuthenticationScheme`，`SignInScheme` 指向外部暫存 Cookie，`CallbackPath = /signin-google`。

### 端點（`ExternalAuthController`，一般 MVC 控制器）

| 路由 | 說明 |
|------|------|
| `GET /Auths/Google/Login?returnUrl=` | 觸發 Google OAuth Challenge，回呼導向 Callback |
| `GET /Auths/Google/Callback?returnUrl=` | Google 驗證完成後的處理端點 |
| `GET /signin-google` | Google 中介軟體內建的回呼，登入到外部暫存 Cookie（不需自行撰寫） |

### 流程步驟

1. 使用者在 `/Auths/Login` 點擊「使用 Google 登入」按鈕（連到 `/Auths/Google/Login`）。
2. 系統發出 `Challenge`，瀏覽器導向 Google 完成驗證，Google 再導回 `/signin-google`。
3. Google 中介軟體把身分寫入外部暫存 Cookie，並導向 `/Auths/Google/Callback`。
4. Callback 讀取外部 Cookie 的 Email、Name、`sub`，呼叫 `ExternalLoginService.FindOrCreateAsync(...)`：
   - 先以 **GoogleId** 比對既有連結；
   - GoogleId 屬於**已刪除**的使用者 → 拒絕（0.9.95 起）；
   - 否則以 **Email** 連結既有本地帳號（寫入 GoogleId，不改狀態與權限）；
   - Email 屬於**已刪除**的使用者 → 拒絕（0.9.95 起）；
   - 都沒有則 **自動建立**新帳號（`Status = false`、套用預設角色、無密碼）。
   - 被拒絕時不寫入、不連結、不新建，導向 `/Auths/Login?sso=deleted`，登入頁顯示「此帳號已被刪除，請洽系統管理員。」；管理員在使用者管理「顯示已刪除」還原後即可再登入。
5. 清除外部暫存 Cookie。
6. 若 `Status = false` → 導向 `/Auths/Pending`（帳號待審核頁，**不**登入）。
7. 若帳號因密碼輸錯被鎖定（0.9.101 起）→ 導向 `/Auths/Login?sso=locked`，不登入。判斷順序集中在 `ExternalLoginResult.Evaluate`：已刪除 → 待開通 → 鎖定 → 登入。
8. 若帳號已啟用兩步驟驗證（0.9.104 起）且這台裝置沒有被記住 → 導向 `/Auths/TwoFactor` 完成第二步。
9. 否則 → 以 `CookieClaims.Create` 建立與帳密登入相同的 Claims（`Role`、`Name`、`NameIdentifier`、`Sid`，以及 0.9.103 起的工作階段版本 `security_stamp`），以主 Cookie scheme 完成登入並導向 `returnUrl`（預設 `/App`）。

```
使用者 ──點擊 Google 登入──▶ /Auths/Google/Login ──Challenge──▶ Google
                                                                  │
        /App ◀──啟用且登入──┐                                       ▼
                           │                          /signin-google（外部暫存 Cookie）
   /Auths/Pending ◀─停用待審─┤                                        │
                           └──── /Auths/Google/Callback ◀───────────┘
                                 （查找/連結/建立帳號 → 依 Status 分流）
```

---

## 四、管理者審核（啟用帳號）

1. 以管理者帳號（預設 `support`）登入。
2. 進入「使用者管理」（`/myusers`）。
3. 找到自動建立的 Google 使用者（帳號為其 Email），編輯：
   - 勾選 **啟用**（`Status`）。
   - 指派 **角色**（`RoleView`），確認權限符合需求。
   - 密碼欄位可留白（Google 帳號不需密碼即可網頁登入）。
4. 儲存後，該使用者即可再次以 Google 登入並進入系統，選單依角色權限顯示。

---

## 五、Google 使用者呼叫 API（0.9.63 起不支援）

Google 使用者預設沒有本地密碼，因此無法用 `/api/v1/auth/login` 取得 JWT。
0.9.63 之前可從使用者選單的「設定 API 密碼」（`/Profile`）自行設定一組密碼，此功能已移除（0.9.102 起 `/Profile` 是與 API 無關的個人資料頁）；
需要呼叫 API 請改用本地帳號。

---

## 六、資料模型異動

`MyUser` 新增兩個可空欄位（對應 EF 遷移 `AddGoogleOAuth`）：

| 欄位 | 型別 | 說明 |
|------|------|------|
| `OAuthProvider` | `string?` | 外部驗證提供者；本地帳號為 `null`，Google 帳號為 `"Google"` |
| `GoogleId` | `string?` | Google 的使用者唯一識別碼（`sub`） |

- **SQLite**：已產生遷移 `AddGoogleOAuth`，啟動時自動套用。
> 0.4.24 起本專案只支援 SQLite，原「SQL Server 需另行處理 schema」的說明已不再適用。

> 「必須變更密碼」（`MustChangePassword` 旗標或密碼到期，0.9.101 起）對沒有本地密碼的 Google 帳號一律不觸發（`IPasswordPolicy` 豁免），否則會永遠卡在變更密碼頁。
> 0.9.101 起帳號因密碼輸錯被鎖定時，Google 登入也會被擋下，導回登入頁顯示「此帳號因連續輸錯密碼已暫時鎖定…」。

---

## 七、安全考量與限制

- **開放註冊風險**：目前為「任何 Google 帳號皆可驗證並自動建帳」。資安閘門是 `Status = false` 預設停用 + 管理者人工審核，請務必落實審核流程。若要更嚴格，可改為限制特定網域或關閉自動建帳。
- 自動建立的帳號雖套用「預設角色」，但在管理者啟用前無法登入。
- `ClientSecret` 請勿進版控；正式環境一律用 user-secrets / 環境變數。
- 回呼 URI（`/signin-google`）必須與 Google Console 設定完全一致，否則 Google 會拒絕。
