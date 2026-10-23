# Gmail 寄信設定指南

- 文件版本：1.0
- 文件狀態：維護中
- 現行系統版本：1.0.0
- 首次實作版本：0.9.84
- 最後核對日期：2026/10/23

本文件一步一步說明：如何讓系統透過**某一個 Gmail 帳號**寄信（忘記密碼、例外告警、測試信），
`appsettings.json` 的 `EmailSettings` 每個值要填什麼、要到 Gmail／Google 帳戶網頁的哪裡取得。
各鍵的權威定義見 [日誌與設定檔說明 §4.3.2.2](日誌與設定檔說明.md)，上線前另需逐項核對
[正式部署與安全檢查清單](正式部署與安全檢查清單.md)「寄信」段。

> Google 網頁的選單文字會隨改版略有不同；找不到時，在 Google 帳戶頁上方的搜尋框輸入「兩步驟驗證」或「應用程式密碼」最快。

## 0. 摘要

| 項目 | 說明 |
|------|------|
| 要做的事 | Gmail 端：開兩步驟驗證 → 產生應用程式密碼；系統端：填 `appsettings.json` → 帳密放 User Secrets／環境變數 → 重啟 → 寄測試信 |
| 需要的時間 | 約 10 分鐘 |
| 適用 | 個人 Gmail（`@gmail.com`）或 Google Workspace 帳號 |
| 寄信量 | 個人 Gmail 每天約 500 封（Workspace 較多），超過當天會被拒絕。適合通知類信件，**不適合**電子報或大量寄送 |
| 建議 | 開一個**專用帳號**（例如 `customerhelpagent.noreply@gmail.com`），不要用個人主帳號：密碼外洩或被停權時影響最小 |

## 1. 步驟一：開啟兩步驟驗證（Google 帳戶網頁）

Gmail 不允許用一般登入密碼走 SMTP，必須用「應用程式密碼」；而**沒開兩步驟驗證就不會出現應用程式密碼的選項**。

1. 用要寄信的那個帳號登入，開啟 <https://myaccount.google.com>。
   （從 Gmail 網頁進入：右上角大頭貼 →「管理您的 Google 帳戶」。）
2. 左側點「**安全性**」（新版可能叫「安全性與登入」）。
3. 在「您登入 Google 的方式」區塊點「**兩步驟驗證**」，依畫面設定手機或驗證器 App 並開啟。

## 2. 步驟二：產生應用程式密碼

1. 開啟 <https://myaccount.google.com/apppasswords>（或在 Google 帳戶頁搜尋「應用程式密碼」）。
2. 「應用程式名稱」輸入可辨識的名字，例如 `CustomerHelpAgent`，按「**建立**」。
3. 畫面會顯示一組 **16 個英文字母**的密碼（顯示成 `abcd efgh ijkl mnop` 四組）。
   - **只會顯示這一次**，關掉就看不到了，忘了只能刪掉重建。
   - 填進系統時**去掉空白**：`abcdefghijklmnop`。

找不到「應用程式密碼」時，通常是下列原因：

| 原因 | 處理 |
|------|------|
| 兩步驟驗證還沒開 | 回到步驟一 |
| 兩步驟驗證只設定了「安全金鑰」 | 另外加一種驗證方式（手機或驗證器 App） |
| 公司／學校的 Workspace 帳號，管理員停用了這個功能 | 請 Workspace 管理員開放，或改用管理員提供的 SMTP 轉發服務 |
| 帳號加入了「進階保護計畫」 | 此計畫不支援應用程式密碼，請改用其他帳號 |

## 3. 步驟三：每個設定值填什麼、從哪裡來

> ⚠️ **SMTP 主機、連接埠、加密方式在 Gmail 網頁的設定畫面裡看不到**。它們是 Google 公布的固定值
> （Google 說明中心〈在其他電子郵件用戶端中查看 Gmail〉文章中的「外寄郵件 (SMTP) 伺服器」），所有 Gmail 帳號都一樣，照下表填即可。

| 設定鍵 | 要填的值 | 從哪裡取得 |
|--------|---------|-----------|
| `Provider` | `Smtp` | 固定值。`None` 是不寄信，`Pickup` 只是把信存成檔案（開發用） |
| `Host` | `smtp.gmail.com` | Google 公布的固定值 |
| `Port` | `587` | Google 公布的固定值（也可用 `465`，見下一列） |
| `Security` | `StartTls` | 搭配 587。若改用 465，這裡要填 `SslOnConnect`。兩組擇一，**不可混搭** |
| `UserName` | 完整的 Gmail 位址，例如 `customerhelpagent.noreply@gmail.com` | 就是登入 Gmail 的帳號；Gmail 網頁右上角大頭貼可看到 |
| `Password` | 步驟二的 16 碼應用程式密碼（無空白） | 步驟二。🔴 **機密**，不寫進 `appsettings.json`，見步驟五 |
| `FromAddress` | 與 `UserName` 相同 | 收件者看到的寄件位址。要用別的位址，見下方說明 |
| `FromName` | 例如 `CustomerHelpAgent 系統通知`；留空則用系統名稱 | 自行決定。收件者信箱裡顯示的寄件人名稱 |
| `PublicBaseUrl` | 使用者實際打開系統的網址，例如 `https://erp.example.com` | 自行決定。信中所有連結（如重設密碼連結）以它為開頭；本機測試可填 `https://localhost:<埠號>` |
| `TimeoutSeconds` | `30` | 維持預設即可 |
| `PickupDirectory` | 不用改 | `Smtp` 模式不會用到 |

**`FromAddress` 想用 Gmail 帳號以外的位址時**：先在 Gmail 網頁右上角齒輪 →「查看所有設定」→「**帳戶和匯入**」→
「選擇寄件地址」→「新增另一個電子郵件地址」，完成驗證後才能用；否則 Gmail 會把寄件者**改寫回登入帳號**。

**不需要**開啟 Gmail 設定裡的「轉寄和 POP/IMAP」—— 那是用來收信的，SMTP 寄信不受影響。

## 4. 步驟四：填寫 `appsettings.json`（不含帳密）

檔案位置：`src/CustomerHelpAgent/CustomerHelpAgent.Web/appsettings.json`。`UserName`、`Password` **維持空字串**，下一步另外放。

```json
"EmailSettings": {
  "Provider": "Smtp",
  "Host": "smtp.gmail.com",
  "Port": 587,
  "Security": "StartTls",
  "UserName": "",
  "Password": "",
  "FromAddress": "customerhelpagent.noreply@gmail.com",
  "FromName": "CustomerHelpAgent 系統通知",
  "PickupDirectory": "C:\\temp\\CustomerHelpAgent\\Mails",
  "PublicBaseUrl": "https://erp.example.com",
  "TimeoutSeconds": 30
}
```

> 只想在自己電腦上試、不想動到版控裡的 `appsettings.json`：整段都可以改放 User Secrets（步驟五的指令把每個鍵都 `set` 一次），
> `appsettings.json` 維持 `None`。

## 5. 步驟五：放入帳號與應用程式密碼

### 開發機：User Secrets

Web 專案已設定 `UserSecretsId`，在 repo 根目錄執行：

```powershell
dotnet user-secrets set "EmailSettings:UserName" "customerhelpagent.noreply@gmail.com" --project src/CustomerHelpAgent/CustomerHelpAgent.Web
dotnet user-secrets set "EmailSettings:Password" "abcdefghijklmnop" --project src/CustomerHelpAgent/CustomerHelpAgent.Web
```

確認：`dotnet user-secrets list --project src/CustomerHelpAgent/CustomerHelpAgent.Web`。User Secrets 存在使用者目錄，不會進版控。

### 正式機：環境變數

鍵名的冒號改成**兩個底線**：`EmailSettings__UserName`、`EmailSettings__Password`。

Windows 伺服器（以系統管理員身分執行 PowerShell）：

```powershell
[Environment]::SetEnvironmentVariable("EmailSettings__UserName", "customerhelpagent.noreply@gmail.com", "Machine")
[Environment]::SetEnvironmentVariable("EmailSettings__Password", "abcdefghijklmnop", "Machine")
```

設定後要**重啟 IIS**（`iisreset`）或重啟服務，新的環境變數才會被程式讀到。
使用 Docker／雲端平台時，改在該平台的環境變數或密鑰保管庫設定同名的鍵。

## 6. 步驟六：重啟並驗證

1. 重新啟動系統。`EmailSettings` 在啟動時驗證，**填錯會直接啟動失敗**（例如 `Port` 超出範圍、`Security` 拼錯、`Host` 空白、`FromAddress` 不是 Email）；
   Production 另外要求 `PublicBaseUrl` 必須是完整的 `http(s)` 網址。錯誤原因寫在啟動日誌。
2. 以管理員登入，開啟「**系統健康監控**」（`/system-health`）：
   - 「**寄信服務**」項：會實際連線 `smtp.gmail.com` 並登入（不寄信）。**綠燈**代表主機、連接埠與帳密都正確。
   - 「**寄信測試**」區塊：按「**寄出測試信**」寄一封給自己，到收件匣（或垃圾郵件匣）確認有收到。
3. 登出後看登入頁：寄信啟用後會出現「**忘記密碼？**」。建議用測試帳號實際走一次「忘記密碼 → 收信 → 點連結重設 → 登入」，
   並確認信中連結的網域就是 `PublicBaseUrl`。

## 7. 疑難排解

錯誤訊息會出現在「寄信服務」紅燈的說明、或「系統例外紀錄」（來源＝背景作業）。

| 症狀／錯誤 | 原因 | 處理 |
|-----------|------|------|
| `535 5.7.8 Username and Password not accepted` | 帳號或密碼錯；最常見是填了**一般登入密碼**而不是應用程式密碼 | 確認 `Password` 是 16 碼應用程式密碼、無空白；`UserName` 是完整位址 |
| `534 5.7.9 Application-specific password required` | 帳號開了兩步驟驗證，但用的是一般密碼 | 依步驟二產生應用程式密碼 |
| 連線逾時、無法連線 | 防火牆或網路業者封鎖對外的 587／465 | 請網管放行對外 587（或 465）；可改試另一組連接埠＋加密方式 |
| TLS／憑證驗證失敗 | 公司網路有攔截 TLS 的設備，憑證被替換 | 請網管把 `smtp.gmail.com` 排除在攔截之外。程式**刻意不略過**憑證驗證，不要改程式繞過 |
| 587 埠搭 `SslOnConnect`、或 465 搭 `StartTls` 連不上 | 連接埠與加密方式混搭 | 587 ↔ `StartTls`、465 ↔ `SslOnConnect` |
| `550 5.4.5 Daily user sending limit exceeded` | 超過每日寄信上限 | 等 24 小時自動解除；長期需要大量寄信請改用 Workspace 或專業寄信服務 |
| 收件者看到的寄件者不是 `FromAddress` | 該位址沒在 Gmail「選擇寄件地址」登記 | 見步驟三下方說明，或把 `FromAddress` 改成與 `UserName` 相同 |
| 信進了垃圾郵件匣 | 收件端判斷為可疑 | 請收件者標示「不是垃圾郵件」；`FromName` 取有意義的名稱；避免短時間大量寄送 |
| 原本可以寄，某天突然 535 | 帳號變更過 Google 密碼，所有應用程式密碼已被撤銷 | 重新產生應用程式密碼並更新 User Secrets／環境變數 |
| 系統啟動失敗 | `Host` 空白、`FromAddress` 不是有效 Email；Production 另有 `PublicBaseUrl` 不是完整 `http(s)` 網址 | 依啟動日誌的錯誤訊息補齊 |

## 8. 安全提醒

- 應用程式密碼等同於帳號密碼（可寄信、可讀信），**不可**寫進 `appsettings.json`、commit、截圖或貼到聊天群組。
- 變更該 Google 帳號的密碼，會**撤銷所有**應用程式密碼，系統會開始寄信失敗，需重新產生並更新設定。
- 不再使用時，到 <https://myaccount.google.com/apppasswords> 刪除該組密碼。
- 系統日誌不會記錄 SMTP 密碼與收件者 Email（寄信日誌只記信件種類），見 [日誌與設定檔說明 §2.4](日誌與設定檔說明.md)「不可寫入日誌的資料」。

> 返回 [operations 目錄](README.md)
