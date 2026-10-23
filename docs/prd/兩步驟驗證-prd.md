# 兩步驟驗證 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.104
- 最後核對日期：2026/10/23

## 一、目標與範圍

登入時除了密碼，再要求手機驗證器 App 上的 6 位數驗證碼（TOTP），密碼外洩時別人也登入不了。使用者可以自己開啟；管理員可以要求特定角色、或所有管理員一定要使用。

- **範圍**：設定頁 `/TwoFactorSetup`（QR、備用碼、停用）、登入第二步 `/Auths/TwoFactor`（密碼與 Google 登入共用）、記住這台裝置、Web API 登入的驗證碼欄位、依角色與系統參數強制、管理員重設、稽核。
- **非範圍**：簡訊或 Email 驗證碼、WebAuthn／通行金鑰、多支手機、裝置清單管理、一次性登入連結。

使用者決定（2026/10/04）：

| 題目 | 決定 |
|---|---|
| 誰要用 | 自選開啟；角色可勾「需要兩步驟驗證」，另有系統參數「管理員必須使用」；support 不受強制 |
| Google 登入 | Google 建立、沒有本機密碼的帳號不問；但**帳號已開啟時 Google 登入也要第二步** |
| QR Code | QRCoder 產生 PNG |
| 附加功能 | 10 組備用碼、管理員重設、記住這台裝置 30 天、同一組驗證碼不能重複使用 |
| 鎖定 | 驗證碼輸錯計入與密碼相同的失敗次數 |

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|---|---|---|---|
| `/TwoFactorSetup` | `MainLayout`（不在選單，個人資料頁有連結）| 已登入 | 所有使用帳號密碼登入的人；必須使用卻還沒設定的人每次換頁都被帶來 |
| `/Auths/TwoFactor` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名＋有效的待驗證 Cookie | 密碼或 Google 已通過、帳號已開啟的人 |
| `/myusers` 的「重設兩步驟驗證」 | — | 使用者管理頁權限 | 系統管理員（使用者手機遺失時） |
| `/roleviews` 的「需要兩步驟驗證」 | — | 角色管理頁權限 | 系統管理員 |
| `POST /api/Auth/login` 的 `twoFactorCode` | — | 匿名 | API 用戶端 |

## 三、畫面與欄位

- **設定頁（未開啟）**：說明與「開始設定」→ QR Code（`otpauth://totp/{系統名稱}:{帳號}`）、每 4 字元空一格的文字金鑰、「6 位數驗證碼」與「啟用」。
  啟用成功後顯示 10 組備用碼（`XXXXX-XXXXX`，**只顯示這一次**）與「我已保存，繼續」。必須使用而還沒設定時，上方藍色說明框「系統要求你的帳號使用兩步驟驗證，設定完成前無法使用其他頁面。」。
- **設定頁（已開啟）**：「已啟用」徽章與剩餘備用碼數；「重新產生備用碼」（需驗證碼或備用碼）；「停用兩步驟驗證」（需驗證碼或備用碼；必須使用的人只看到說明文字）。
- **設定頁（沒有本機密碼）**：只顯示「你使用 Google 登入，沒有系統內的密碼，不需要設定兩步驟驗證。」。
- **登入第二步**：見[登入與帳號流程 PRD](登入與帳號流程-prd.md)「三、畫面與欄位」。
- **個人資料頁**：有本機密碼的人多一個「兩步驟驗證」區塊（狀態徽章＋「設定兩步驟驗證」或「管理兩步驟驗證」）；登入紀錄的動作多「兩步驟驗證碼錯誤」。
- **使用者管理**：狀態欄多「兩步驟驗證」徽章；已開啟的人（自己那一列除外）多「重設兩步驟驗證」操作（確認窗）。
- **角色管理**：編輯窗「角色資料」多「需要兩步驟驗證」核取方塊。
- **系統參數**：「密碼與登入」多「管理員必須使用兩步驟驗證」「記住裝置天數」（0–365，0＝不提供）。

## 四、內部系統運作

- **服務**：`ITwoFactorService`（Business，scoped，`IDbContextFactory`）：`IsRequiredAsync`、`BeginEnrollment`、`EnableAsync`、`VerifyAsync`、`RegenerateBackupCodesAsync`、`DisableAsync`、`ResetAsync`、`CountUnusedBackupCodesAsync`。
- **資料**：`MyUser.TwoFactorEnabled`、`TwoFactorSecret`（Data Protection 加密，`ITwoFactorSecretProtector` 實作在 `Web/Auth`）、`TwoFactorLastStep`（最後接受的時間步）；`TwoFactorBackupCode`（`MyUserId` Cascade、`CodeHash`、`CreatedAtUtc`、`UsedAtUtc`）；`RoleView.RequireTwoFactor`。Migration `AddTwoFactorBackupCodes`。
- **啟用**：`BeginEnrollment` 產生密鑰（只放在頁面記憶體）→ 使用者輸入驗證碼 → `EnableAsync` 驗證通過才加密存檔、記下時間步、換工作階段版本、產生 10 組備用碼（舊的刪除）、稽核 `User.TwoFactorEnable`。
- **驗證**：6 位數字走 TOTP（前後各一步誤差），接受條件為條件式 `UPDATE … WHERE TwoFactorLastStep IS NULL OR < step` 影響 1 列；其他輸入當備用碼，去掉橫線與空白、轉大寫後以 HMAC-SHA256（金鑰 `two-factor-backup:{使用者 Id}`）比對，`UsedAtUtc IS NULL` 條件式標記。
- **登入**：`MyUserServiceLogin.LoginAsync` 回 `LoginAttemptResult(Message, User, RequiresTwoFactor)`；需要第二步時**不**歸零失敗次數、不寫成功稽核。
  `CompleteSecondFactorAsync(userId, code, rememberedDevice, provider)` 重查帳號狀態與鎖定 → 驗證 → 錯了 `Login.TwoFactorFailed`＋累加（達門檻 `Login.LockedOut`＋通知管理員）、對了歸零＋`Login.Success`／`Login.Sso.Success`（detail `mfa=totp|backup|device`）。
- **Cookie**（`TwoFactorLoginCookies`，Data Protection 限時、HttpOnly、SameSite=Lax）：`.CustomerHelpAgent.TwoFactorPending`（5 分鐘：使用者 Id、工作階段版本、記住我、返回網址、登入方式）、`.CustomerHelpAgent.TwoFactorDevice`（N 天：使用者 Id、工作階段版本）。
- **強制**：`IsRequiredAsync` ＝ 有本機密碼、不是 support，而且（任一有效角色〔主要＋額外，排除已刪除〕勾了 `RequireTwoFactor`，或是管理員且 `TwoFactorSettings:RequireForAdmins`）。`AuthenticationStateHelper.Check` 在「必須變更密碼」之後判斷，導向 `/TwoFactorSetup`；兩頁互不導向。
- **停用與重設**：`DisableAsync` 先拒絕必須使用的人、再驗證碼；`ResetAsync`（管理員）不需驗證碼。兩者都清掉密鑰、時間步、備用碼並換工作階段版本，稽核 `User.TwoFactorDisable`／`User.TwoFactorReset`（detail `account=`）。
- **這台保持登入**：設定頁啟用（按「我已保存，繼續」時）與停用後呼叫 `SessionRefreshNavigator.KeepSignedInAsync("/Profile")`。

## 五、權限與安全

- 密鑰加密存放；密鑰、備用碼明文、驗證碼都不寫日誌或稽核。備用碼資料庫只存雜湊。
- 第二步錯誤與密碼錯誤共用鎖定次數，而且密碼步驟不歸零 —— 知道密碼的人無法靠重輸密碼無限猜驗證碼。
- 同一組驗證碼只能用一次（並行請求只有一個成功）。
- 待驗證 Cookie 不在網址或表單、5 分鐘到期；送出時重查工作階段版本，期間改密碼、被停用或被強制登出就作廢。
- 「記住這台裝置」綁定工作階段版本：改密碼、強制登出、停用或重設兩步驟驗證之後失效。
- Google 會連結同 Email 的既有帳號，所以已開啟的帳號用 Google 登入也要第二步。
- Web API 不能繞過：已開啟要帶 `twoFactorCode`；必須使用卻還沒設定一律拒絕（API 沒有地方設定）。
- ⚠️ 金鑰環遺失或換掉時所有密鑰解不開，由管理員逐一重設；support 不受強制，是救援入口。備份含金鑰環，外流等於密鑰外流（[備份與還原操作手冊](../operations/備份與還原操作手冊.md)）。

## 六、錯誤與邊界

- 啟用時驗證碼錯：「驗證碼不正確。請確認手機時間正確，輸入 App 上目前顯示的 6 位數。」；什麼都不存。
- 重新產生或停用時驗證碼錯：「驗證碼不正確。」（**不**計入鎖定次數 —— 已登入的人）。
- 必須使用的人停用：「你的帳號必須使用兩步驟驗證，不能停用。」。
- 登入第二步錯誤、鎖定中、帳號已停用：一律「驗證碼不正確，或帳號已被暫時鎖定。」。
- 待驗證 Cookie 過期、被竄改、或期間版本換掉：回登入頁重新輸入密碼。
- 管理員重設不存在的帳號：「找不到這位使用者。」。
- 角色勾了「需要兩步驟驗證」：該角色的人在下一次換頁被帶到設定頁（不換工作階段版本，已開的頁面繼續可用到換頁為止）。
- API：已開啟而未帶驗證碼 → 401「需要兩步驟驗證碼。」（不計失敗）；帶錯 → 401（計失敗）；必須使用卻還沒設定 → 401「請先在網頁完成兩步驟驗證設定。」。訊息在 `ApiResult.ErrorMessage`。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/TwoFactorTests.cs`：啟用（錯碼不存、加密、換版本、10 組、只存雜湊）、⭐ 同一組碼只能用一次（含模擬並行）、其他密鑰與空值、備用碼一次性與正規化、別人的備用碼無效、重新產生需驗證且舊碼失效、
  ⭐ 密碼步驟不歸零不寫成功、⭐ 重輸密碼洗不掉驗證碼失敗次數、成功稽核帶方式、停用帳號不能完成第二步、強制（主要角色、額外角色、管理員設定）與豁免（support、沒有本機密碼、已刪除的角色）、停用（需驗證、必須使用時拒絕、清除並換版本）、管理員重設、另一組金鑰解不開、待驗證 Cookie 往返與 5 分鐘到期、記住裝置比對使用者與版本、天數 0。
- `TwoFactorIntegrationTests`（真正的主機）：API 未帶碼 401 不計次、錯碼計次、正確碼發 token；必須使用卻未設定 401；⭐ Google 登入已開啟的帳號導向第二步、未開啟的直接登入。
- `AuthenticationStateHelperTests`：必須使用而未設定導向設定頁、設定頁本身與已開啟不導向、同時要改密碼時先改密碼且改密碼頁不被帶走（不迴圈）。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Business/Services/Other/TwoFactorService.cs`、`TotpService.cs`、`MyUserServiceLogin.cs`、`AuthenticationStateHelper.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Auth/TwoFactorLoginCookies.cs`、`DataProtectionTwoFactorSecretProtector.cs`、`QrCodeImage.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Components/Auths/TwoFactor.razor(.cs)`、`Components/Pages/TwoFactorSetupPage.razor`、`Components/Views/Profiles/TwoFactorSetupView.razor(.cs)`
- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Controllers/AuthController.cs`、`ExternalAuthController.cs`
- `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas/Models/TwoFactorBackupCode.cs`、`src/CustomerHelpAgent/CustomerHelpAgent.Models/Systems/TwoFactorSettings.cs`
- 說明頁：`Datas/Help/twofactorsetup.md`
- 交叉連結：[登入與帳號流程](登入與帳號流程-prd.md)、[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[個人資料](個人資料-prd.md)、[系統參數](系統參數-prd.md)、[認證授權與權限機制](../security/認證授權與權限機制.md)
