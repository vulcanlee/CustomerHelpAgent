# VS Code 開發環境與新專案上手指南

- 文件版本：3.3
- 文件狀態：已實作
- 現行系統版本：1.0.3
- 首次實作版本：0.9.1
- 最後核對日期：2026/10/23

> 本文是「拿到這個腳手架之後怎麼開始」的單一入口，涵蓋 **VS Code 環境啟動 → 機密設定檔（User Secrets）→ 品牌客製化 → 複製成新專案並更名 → 驗證**全程。
>
> 動手改既有程式前，請另讀 [開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（分層、Migration、權限同步等不變量）。

---

## 目錄

1. [適用對象與前置條件](#1-適用對象與前置條件)
2. [必備工具與 VS Code 擴充套件](#2-必備工具與-vs-code-擴充套件)
3. [取得原始碼與第一次建置](#3-取得原始碼與第一次建置)
4. [在 VS Code 啟動與偵錯](#4-在-vs-code-啟動與偵錯)
5. [機密設定檔（User Secrets）](#5-機密設定檔user-secrets)
6. [資料庫、Migration 與預設帳號](#6-資料庫migration-與預設帳號)
7. [品牌客製化：圖示、圖片、產品名稱與說明](#7-品牌客製化圖示圖片產品名稱與說明)
8. [從腳手架複製出新專案並更名](#8-從腳手架複製出新專案並更名)
9. [更名後驗證清單](#9-更名後驗證清單)
10. [常見疑難排解](#10-常見疑難排解)
11. [相關文件](#11-相關文件)

---

## 1. 適用對象與前置條件

| 你是 | 該讀哪幾章 |
|------|------------|
| 第一次 clone 本 repo，想把它跑起來 | 2 → 3 → 4 → 6 |
| 想把機密金鑰從 `appsettings.json` 搬走 | 5 |
| 要換掉圖示、品牌圖片、產品名稱與說明 | 7 |
| 要用本腳手架開一個**新系統** | 全部，重點在 7 → 8 |

前置知識：基本的 .NET CLI 與 Git 操作。作業系統以 **Windows 10 / 11** 為準（外部目錄預設值 `C:\temp\...` 為 Windows 路徑）。

---

## 2. 必備工具與 VS Code 擴充套件

### 2.1 必裝工具

| 工具 | 版本 / 說明 | 驗證指令 |
|------|-------------|----------|
| .NET SDK | **10.0.400**（`global.json` 已鎖定，`rollForward: latestFeature`，故 10.0.4xx 以上可用） | `dotnet --info` |
| PowerShell 7（`pwsh`）| `scripts/*.ps1` 都以 pwsh 執行；**Windows 內建的 Windows PowerShell 5.1 不算** | `pwsh --version` |
| Git | 任意近期版本 | `git --version` |
| Visual Studio Code | 任意近期版本 | — |

> ⚠️ `global.json` 鎖定 SDK 版本。若 `dotnet --info` 找不到 10.0.4xx，所有 `dotnet` 指令都會直接失敗，請先到 [.NET 下載頁](https://dotnet.microsoft.com/download) 安裝對應 SDK。

### 2.2 VS Code 擴充套件

repo 已提供 `.vscode/extensions.json`，開啟工作區時 VS Code 會主動建議安裝：

| 擴充套件 ID | 用途 |
|-------------|------|
| `ms-dotnettools.csdevkit` | C# Dev Kit —— 方案總管、測試總管、`.slnx` 支援 |
| `ms-dotnettools.csharp` | C# 語言服務與偵錯器（`coreclr`） |
| `ms-dotnettools.vscode-dotnet-runtime` | 前兩者的相依 runtime 管理 |
| `ms-vscode.powershell` | 編輯與偵錯 `scripts/*.ps1` |
| `editorconfig.editorconfig` | 讓編輯器遵守 `.editorconfig`（提交前的 `dotnet format --verify-no-changes` 會檢查） |

### 2.3 選用工具

```powershell
# EF Core 指令列工具（自行新增 Migration，或執行 New-StarterProject.ps1 時需要）
dotnet tool install --global dotnet-ef
dotnet ef --version

# 信任本機開發用 HTTPS 憑證（第一次跑 https profile 前務必執行）
dotnet dev-certs https --trust
```

Migration 指令細節見 [EFCore 指令備忘](EFCore.md)，本文不重複。

---

## 3. 取得原始碼與第一次建置

```powershell
git clone <repo-url> NET10-Blazor-Starter
cd NET10-Blazor-Starter

# 方案檔位於 src/CustomerHelpAgent/，格式為新版 .slnx（本 repo 沒有 .sln）
dotnet restore src/CustomerHelpAgent/CustomerHelpAgent.slnx
dotnet build   src/CustomerHelpAgent/CustomerHelpAgent.slnx -v:minimal

# 第一次執行（會自動建立外部目錄、套用 Migration、Seed 預設角色與帳號）
dotnet run --project src/CustomerHelpAgent/CustomerHelpAgent.Web/CustomerHelpAgent.Web.csproj --launch-profile https
```

### 3.1 第一次啟動會自動產生的東西

| 路徑 | 內容 | 來源設定 |
|------|------|----------|
| `C:\temp\CustomerHelpAgent\DB\BackendDB.db` | SQLite 資料庫 | `SystemSettings:ExternalFileSystem:DatabasePath` |
| `C:\temp\CustomerHelpAgent\Download` / `Upload` / `ProjectFile` | 檔案上傳與下載目錄 | 同上區段的 `DownloadPath`／`UploadPath`／`ProjectFilePath` |
| `C:\temp\CustomerHelpAgent\AfterSalesFile` | 售後申請照片（1.0.3 起） | 同上區段的 `AfterSalesFilePath` |
| `C:\temp\CustomerHelpAgent\Exception` / `TokenUsage` / `AiCallLog` | 例外堆疊、LLM 原始用量、AI 對話紀錄的檔案 | 同上區段的 `ExceptionPath`／`TokenUsagePath`／`AiCallLogPath` |
| `C:\temp\CustomerHelpAgent\Keys` | Data Protection 金鑰環（登入 Cookie 靠它解密，刪掉等於全站登出） | 同上區段的 `DataProtectionKeyPath` |
| `C:\temp\CustomerHelpAgent\Backup` | 系統備份 zip（0.9.99 起，含金鑰環） | 同上區段的 `BackupPath` |
| `C:\temp\Logs\CustomerHelpAgent.Web\` | NLog 日誌 | `NLog:BasePath` + 組件命名空間 |

`ExternalFileSystem` 的十一個目錄（0.9.99 起含 `BackupPath`、0.9.131 起含 `AuditContentPath`、1.0.3 起含 `AfterSalesFilePath`）若不存在，`Program.cs` 的「系統使用的目錄準備」區段會在啟動時自動建立。

### 3.2 建置注意事項

> ⚠️ `src/CustomerHelpAgent/Directory.Build.props` 開啟了 `TreatWarningsAsErrors`（唯一豁免是 `NU1900`）。**任何編譯警告都會讓建置失敗**，這是刻意的品質關卡，不要用 `#pragma warning disable` 繞過。

> ⚠️ 套件版本一律寫在 `src/CustomerHelpAgent/Directory.Packages.props`（Central Package Management）。個別 `.csproj` 的 `<PackageReference>` **不可以**帶 `Version` 屬性。

---

## 4. 在 VS Code 啟動與偵錯

### 4.1 開啟工作區

用 **`File > Open Folder` 開 repo 根目錄**（`NET10-Blazor-Starter\`），**不要**只開 `src/CustomerHelpAgent`。原因是 `scripts/`（文件編碼檢查）與 `docs/` 都必須在工作區內，`.vscode/` 的任務才找得到它們。

### 4.2 repo 提供的 `.vscode` 設定

| 檔案 | 作用 |
|------|------|
| `launch.json` | F5 偵錯設定。使用 **`https` profile**、自動開啟瀏覽器、`ASPNETCORE_ENVIRONMENT=Development` |
| `tasks.json` | 提交前要跑的 `quality-gate`（品質關卡），以及單項任務 `build` / `format-check` / `test` / `docs-encoding` / `restore`（本專案沒有 CI，見 [§9](#9-更名後驗證清單)） |
| `settings.json` | Markdown 預設存成 **UTF-8 含 BOM**（對應本專案不變量）、隱藏 `bin`/`obj`、指定預設方案檔 |
| `extensions.json` | 建議安裝的擴充套件清單 |

### 4.3 常用操作

| 操作 | 快捷鍵 | 結果 |
|------|--------|------|
| 啟動偵錯 | `F5` | 建置後啟動，瀏覽器自動開到 `https://localhost:7067` |
| 不進偵錯器執行 | `Ctrl+F5` | 同上但不附加偵錯器，熱重載較順 |
| 建置 | `Ctrl+Shift+B` | 執行 `build` 任務 |
| 執行其他任務 | `Ctrl+Shift+P` → `Tasks: Run Task` | 選 `format-check` / `test` / `docs-encoding` |

### 4.4 建議用 `https` profile

`Program.cs` 的中介軟體管線**無條件**呼叫 `app.UseHttpsRedirection()`：

```csharp
app.UseHttpsRedirection();
```

`launchSettings.json` 的兩個 profile：

| profile | applicationUrl |
|---------|----------------|
| `http` | `http://localhost:5066` |
| `https` | `https://localhost:7067;http://localhost:5066` |

只跑 `http` profile 時，`UseHttpsRedirection` 找不到 HTTPS 埠，**不會重導**：網站仍以 http 正常運作，日誌會記一筆 `Failed to determine the https port for redirect.` 警告（0.9.87 實測）。
仍**建議使用 `https` profile**（`.vscode/launch.json` 已預設如此）：它比較接近正式環境，Google 登入的 redirect URI 也通常以 https 註冊。

---

## 5. 機密設定檔（User Secrets）

### 5.1 為什麼需要

`appsettings.json` 會進版控，任何寫在裡面的金鑰都等於公開。目前腳手架內的預設值是**刻意留下的假值**：

```json
"JwtSettings": {
  "SigningKey": "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars"
}
```

`Auth/JwtSettings.cs` 的 `IsPlaceholderSigningKey` 以「金鑰是否含 `ChangeThisJwtSigningKey` 標記」當絆索（所以 `New-StarterProject.ps1` 換成 `<新代號>-ChangeThisJwtSigningKey-…` 後一樣會被認出）；`Configuration/StartupSafetyValidator.cs` 在 `ASPNETCORE_ENVIRONMENT=Production` 時遇到它就**丟出例外直接中止啟動**，不會帶著假金鑰上線。

> ⚠️ IIS 等主機**沒有設定 `ASPNETCORE_ENVIRONMENT` 時預設就是 Production**。開發機以 `launchSettings.json` 的 `Development` 執行，不做這項檢查，所以「本機正常、部署後起不來」多半是這個原因；中止時的訊息會寫在 Windows 事件檢視器（0.9.86 起訊息會直接說明這一點）。

**User Secrets** 讓你把真實金鑰放在 repo 之外、又不必改任何程式碼。

### 5.2 運作原理與檔案位置

`src/CustomerHelpAgent/CustomerHelpAgent.Web/CustomerHelpAgent.Web.csproj:6` 宣告了一組識別碼：

```xml
<UserSecretsId>93aff4dd-e822-4569-ac29-89f53416029e</UserSecretsId>
```

這組 Id 決定機密檔案的實體位置：

| 作業系統 | 路徑 |
|----------|------|
| Windows | `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` |
| Linux / macOS | `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json` |

以本 repo 現值為例，Windows 上就是：

```
C:\Users\<你的帳號>\AppData\Roaming\Microsoft\UserSecrets\93aff4dd-e822-4569-ac29-89f53416029e\secrets.json
```

> ⚠️ **只在 Development 環境載入**。本專案使用 `Program.cs` 開頭 `WebApplication.CreateBuilder(args)` 的預設設定來源鏈，全案**沒有**任何明確的 `AddUserSecrets(...)` 呼叫 —— 也就是完全依賴預設行為：`ASPNETCORE_ENVIRONMENT` 不是 `Development` 時，User Secrets **完全不會被讀取**。正式環境請改用環境變數或雲端 secret store，見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。

> ✅ 這個檔案在 repo **之外**，不可能被 `git add` 進去，也不需要在 `.gitignore` 加任何規則。

### 5.3 建立方式 A：使用 CLI（建議）

```powershell
# 必須在 Web 專案的 csproj 目錄下執行（或加 --project 參數）
cd src/CustomerHelpAgent/CustomerHelpAgent.Web

# csproj 已有 UserSecretsId 時可略過；若沒有，這行會自動產生一組並寫回 csproj
dotnet user-secrets init

# 產生一把夠長的隨機金鑰（至少 32 字元）
$key = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
dotnet user-secrets set "JwtSettings:SigningKey" $key

# 確認寫入結果
dotnet user-secrets list
```

其他常用指令：

```powershell
dotnet user-secrets remove "JwtSettings:SigningKey"   # 移除單一鍵
dotnet user-secrets clear                             # 清空整份 secrets.json
```

### 5.4 建立方式 B：直接編輯 `secrets.json`

在 VS Code 裡直接編輯往往比一條條下指令快。先開啟目錄：

```powershell
# 目錄不存在時先建立
$dir = "$env:APPDATA\Microsoft\UserSecrets\93aff4dd-e822-4569-ac29-89f53416029e"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
code "$dir\secrets.json"
```

`secrets.json` 完整範例（可直接貼上後改值）：

```json
{
  "JwtSettings": {
    "SigningKey": "把這裡換成你自己產生的 32 字元以上隨機字串"
  },
  "GoogleOAuthSettings": {
    "ClientId": "",
    "ClientSecret": ""
  },
  "CacheSettings": {
    "RedisConnection": ""
  },
  "EmailSettings": {
    "UserName": "",
    "Password": ""
  },
  "AutoMapper:LicenseKey": ""
}
```

> **兩種寫法等價**：`dotnet user-secrets set "JwtSettings:SigningKey" "..."` 存進檔案後就是上面的巢狀結構。而 `"AutoMapper:LicenseKey"` 之所以維持扁平的冒號鍵，是因為 `appsettings.json` 本來就是這樣宣告（檔案最後一個鍵）、`Program.cs` 也以 `builder.Configuration["AutoMapper:LicenseKey"]` 讀取；兩種格式 .NET 都認得。

### 5.5 該搬哪些鍵

| 鍵路徑 | 目前值 | 必要性 | 不設定會怎樣 |
|--------|--------|--------|--------------|
| `JwtSettings:SigningKey` | `DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars` | **必要** | 開發環境可跑，但 Production 啟動直接中止（`StartupSafetyValidator` 的 SigningKey 檢查）；且此金鑰已公開於版控，任何人都能偽造 JWT |
| `GoogleOAuthSettings:ClientId` | `""` | 啟用 Google 登入時必要 | `Enabled` 設 true 但憑證留空會登入失敗。設定細節見 [Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md) |
| `GoogleOAuthSettings:ClientSecret` | `""` | 同上 | 同上。**這是真正的密鑰，絕不可進版控** |
| `CacheSettings:RedisConnection` | `""` | `CacheSettings:Provider` 改成 `Redis` 時必要 | 連線字串通常含密碼；Production 下留空會被 `StartupSafetyValidator` 的 Redis 檢查擋下 |
| `EmailSettings:UserName` / `EmailSettings:Password` | `""` | `EmailSettings:Provider` 改成 `Smtp` 且 SMTP 需要登入時必要 | 登入失敗、信寄不出去（健康監控「寄信服務」紅燈）。**密碼絕不可進版控**。開發機預設是 `Provider=None`（與出貨值相同）；本機要測忘記密碼，用 User Secrets 設 `Provider=Pickup`，信寫成 `.eml` 檔，不需要任何帳密；要測真的 SMTP，才用 User Secrets 設 `Provider=Smtp` 與這兩個鍵 |
| `AutoMapper:LicenseKey` | `""` | 商業授權情境 | 留空不影響開發（`Program.cs` 讀不到就不設定授權），但授權金鑰不應進版控 |

> ⚠️ `BootstrapSettings:SupportPassword`（預設管理者密碼）**不建議**放進 User Secrets —— 它的行為與一般機密不同，改動會反向覆寫資料庫。詳見 [§6.4](#64-預設管理者帳號)。

### 5.6 驗證有沒有生效

```powershell
cd src/CustomerHelpAgent/CustomerHelpAgent.Web
dotnet user-secrets list
# 應輸出：JwtSettings:SigningKey = <你的值>
```

設定來源的**覆寫優先序**（後者蓋前者）：

```
appsettings.json
  → appsettings.{Environment}.json
    → User Secrets（僅 Development）
      → 環境變數
        → 命令列參數
```

### 5.7 常見誤區

| 誤區 | 說明 |
|------|------|
| 改了 `UserSecretsId` 卻沒重建 | Id 是編譯期嵌入組件的，改完 `.csproj` 必須重新 `dotnet build` 才會指向新目錄 |
| 在 repo 根目錄下 `dotnet user-secrets` | 會找不到專案。請先 `cd` 到 `CustomerHelpAgent.Web`，或加 `--project src/CustomerHelpAgent/CustomerHelpAgent.Web/CustomerHelpAgent.Web.csproj` |
| 以為正式環境也會讀 | 不會。非 Development 環境完全不載入 User Secrets |
| 以為 `secrets.json` 在專案資料夾裡 | 不在。它在 `%APPDATA%` 下，故意放在 repo 之外 |
| 多個專案共用同一個 `UserSecretsId` | 機密會互相污染。從腳手架複製新專案時務必換掉，見 [§8.2 步驟 5](#82-手動更名步驟) |

---

## 6. 資料庫、Migration 與預設帳號

### 6.1 資料庫檔案位置

本系統**只支援 SQLite**。連線字串由 `CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs` 的 `GetSQLiteConnectionString` 組出：

```csharp
public const string SQLiteDatabaseFilename = "BackendDB.db";
public static string GetSQLiteConnectionString(string databasePath)
{
    return $"Data Source={Path.Combine(databasePath, SQLiteDatabaseFilename)}";
}
```

目錄部分來自 `appsettings.json` 的 `SystemSettings:ExternalFileSystem:DatabasePath`，所以開發環境的實際檔案是 **`C:\temp\CustomerHelpAgent\DB\BackendDB.db`**。

> ⚠️ **陷阱**：`appsettings.json` 還有一個 `SystemSettings:ConnectionStrings:SQLiteDefaultConnection`（值為 `Data Source=BackendDB.db`）。這是**死設定，執行期完全不會被讀取**，改它沒有任何作用。要換資料庫位置請改 `ExternalFileSystem:DatabasePath`。

### 6.2 啟動時自動套用 Migration

`Program.cs` 啟動時呼叫 `IDatabaseInitializer`（0.9.91 起，`CustomerHelpAgent.Business/Startup/`）：取得跨行程鎖 → 套用 Migration → 設定 WAL → 執行種子資料（預設角色、support 帳號、RBAC 回填）。你不需要手動執行 `dotnet ef database update`。要加自己的種子資料，實作 `IDatabaseSeeder` 並在 `AddApplicationServices` 註冊，不要寫回 Program.cs。

### 6.3 重建資料庫

```powershell
# 1. 停掉正在執行的程式（重要，SQLite 檔案會被鎖住）
# 2. 刪除資料庫檔（0.9.91 起是 WAL 模式：連同 -wal、-shm 與鎖檔一起刪）
Remove-Item "C:\temp\CustomerHelpAgent\DB\BackendDB.db*" -Force
# 3. 重新執行，會重新套用 Migration 並 Seed
```

新增 Migration 的指令見 [EFCore 指令備忘](EFCore.md)。

### 6.4 預設管理者帳號

| 項目 | 值 | 來源 |
|------|----|------|
| 帳號 | `support` | `appsettings.json` 的 `BootstrapSettings:SupportAccount` |
| 密碼 | `support` | `appsettings.json` 的 `BootstrapSettings:SupportPassword` |
| 權限 | `IsAdmin = true` | 啟動時的 `SupportUserSeeder`（`CustomerHelpAgent.Business/Startup/`）強制設定 |

> ⚠️ **重要行為，不是 bug**：每次啟動時，若資料庫內 `support` 的密碼雜湊**驗不過設定檔的值**，程式會把密碼**覆寫回設定檔的值**，並強制 `IsAdmin = true`（`SupportUserSeeder` 中 `VerifyPassword(bootstrapSettings.SupportPassword, ...)` 那段）。
>
> 這造成兩個容易誤解的現象：
> 1. 在 UI 改了 `support` 的密碼，重啟後又變回 `support`（`/ChangePassword` 頁面本身也會直接拒絕修改 support 的密碼）。
> 2. 把 `SupportPassword` 改成新值後，下次啟動會**靜默重設**資料庫裡的密碼。
>
> 這是刻意的「救援後門」設計，讓你永遠有辦法登入。正式上線前的處理方式見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。

`MagicObjectHelper.cs` 的 `開發者帳號 = "support"` 用於後續識別並保護這個帳號（例如不允許在 UI 刪除、0.9.85 起「忘記密碼」頁也不受理這個帳號）。

> ⚠️ 出貨的 `BootstrapSettings:SupportEmail` 值是 `support`，**不是有效的信箱**。0.9.85 起使用者存檔時會檢查 Email 格式，所以在「使用者管理」編輯 support 帳號時，要先把 Email 改成有效信箱或清空才能存檔。

---

## 7. 品牌客製化：圖示、圖片、產品名稱與說明

把腳手架變成「你的產品」，需要動的是**四樣東西**。本章交代每一樣改哪個檔、規格是什麼、使用者會在哪裡看到。

### 7.1 一次看懂：改哪裡、會在哪裡看到

| 要改的東西 | 檔案 / 設定鍵 | 使用者會在哪看到 |
|------|------|------|
| 網頁圖示 | `wwwroot/favicon.png` | 瀏覽器分頁與書籤（`Components/App.razor:15`） |
| 產品代表圖片 | `wwwroot/images/brand-logo.png` | 啟動頁（`SplashView.razor:5-7`）、登入頁品牌面板（`AuthShell.razor:22`，0.9.60 起登入／忘記密碼／重設密碼三頁共用）、**登入後首頁**（`HomeWelcomeView.razor`，0.9.9 起）|
| 產品名稱 | `SystemSettings:SystemInformation:SystemName` | 啟動頁大標、登入頁大標、**登入後首頁大標**、**側邊欄品牌文字**（0.9.13 起）、「關於」對話窗 |
| 產品簡短說明 | `SystemSettings:SystemInformation:SystemDescription` | 啟動頁副說明、登入頁副說明、**登入後首頁副說明**、「關於」對話窗 |
| 版本號 | `SystemSettings:SystemInformation:SystemVersion` | 「關於」對話窗、**登入後首頁的系統資訊列**、**「系統健康監控」頁**（`/system-health`）的診斷文字 |

> **沿革**：`0.9.2` 之前，登入頁與啟動頁的兩段說明文字是**寫死在 `.razor` 裡**的，`SystemDescription` 只影響「關於」對話窗 —— 改設定檔那兩頁不會變。0.9.2 起兩頁都改讀設定，`SystemName` 與 `SystemDescription` 都是單一來源。

### 7.2 更換產品代表圖片 `brand-logo.png`

現況規格：**1024×1024 PNG**（約 171 KB），滿版構圖、無透明邊。

啟動頁與登入後首頁用 `object-fit: cover` 填滿容器，**會裁成正方形**；登入頁用 `object-fit: contain`（完整縮放、不裁切）。所以主體務必置中、四周留安全邊距：

| 位置 | 容器（CSS） | 實際顯示尺寸 |
|------|------|------|
| 登入頁 | `.brand-logo`（`Login.razor.css:65`），圓角 26px | **104 × 104**（螢幕寬 ≤1023px 時 56 × 56、≤767px 時 44 × 44）|
| 啟動頁 | `.splash-brand-image-wrap`（`SplashView.razor.css:29`），圓角 24px | **120 × 120**（螢幕寬 ≤640.98px 時為 96 × 96） |
| 登入後首頁 | `.home-hero-image-wrap`（`HomeWelcomeView.razor.css`），圓角 24px | **72 × 72**（螢幕寬 ≤640.98px 時為 64 × 64） |

換圖建議：

- 正方形、**至少 512×512**（現況 1024×1024 是為了高 DPI 螢幕）。
- 三頁背景都是**淺色**（啟動頁與登入後首頁為淺藍漸層卡片、登入頁淺色底），圖片本身若也是淺色會糊掉，建議用有對比的深色或彩色主體。
- **檔名不要改** —— 三處 `.razor` 都硬編這個路徑。
- 0.9.2 起改用 `@Assets["images/brand-logo.png"]`（0.9.9 新增的登入後首頁比照辦理），換檔後 URL 會自動帶上新的 fingerprint（形如 `images/brand-logo.854k3jgc1o.png`），**不需要清瀏覽器快取**。

### 7.3 更換網頁圖示 `favicon.png`

現況規格：**64×64 PNG**（約 4.4 KB）。

> ⚠️ 本專案**沒有 `favicon.ico`**，也沒有 apple-touch-icon、`site.webmanifest` 或 PWA 圖示 —— 這是刻意的（Blazor Server，不是 PWA 範本）。整份專案只有 `Components/App.razor:15` 一處宣告圖示。

由 `brand-logo.png` 產生（沿用 0.4.25 的原始作法）：

```bash
ffmpeg -i images/brand-logo.png \
  -vf "crop='min(iw,ih)':'min(iw,ih)',scale=64:64:flags=lanczos" \
  -pix_fmt rgba -y favicon.png
```

沒有 ffmpeg 就用任何影像工具置中裁成正方形、縮到 64×64、存成 PNG 即可。

> ⚠️ **不要把 `@Assets["favicon.png"]` 改回裸相對路徑**（例如 `href="favicon.png"`）。沒有 fingerprint 的話，你覆蓋了檔案但 URL 沒變，使用者會一直看到快取中的舊圖示。

換完看不到新圖示？先 `Ctrl+F5` 強制重新整理 —— favicon 的瀏覽器快取特別黏，必要時到瀏覽器設定清除該站台資料。

### 7.4 更換產品名稱與簡短說明

0.9.2 起兩者都是單一來源，**只改 `appsettings.json` 一處**，啟動頁、登入頁、登入後首頁（0.9.9 起）、側邊欄品牌文字（0.9.13 起）與「關於」對話窗五處同步生效：

```json
"SystemSettings": {
  "SystemInformation": {
    "SystemName": "你的系統名稱",
    "SystemDescription": "一句話說明這個系統在做什麼",
    "SystemVersion": "1.0.0 (2026/01/01)"
  }
}
```

版面提示：

| 欄位 | 建議長度 | 說明 |
|------|------|------|
| `SystemName` | 4–10 字 | 兩頁都是最大的標題字（啟動頁 `.splash-title`、登入頁 `.app-title`），過長會換行擠壓版面。側邊欄（展開寬 300px）約可容納 10 字，超出會以刪節號收尾，完整名稱放在 `title` 屬性 |
| `SystemDescription` | 30–40 字 | 啟動頁單行可容納約 40 字；登入頁面板較窄會折成兩行。超過約 60 字會開始破版 |
| `SystemVersion` | — | 格式固定為 `Major.Minor.Patch (YYYY/MM/DD)`。用 `New-StarterProject.ps1` 複製時已自動重設為 `1.0.0 (執行當天)`，之後每次異動 Patch +1；**留空**會讓「系統健康監控」頁把應用程式判為 Degraded 並顯示「SystemVersion 未設定。」 |

### 7.5 ⚠️ 瀏覽器分頁標題是另一件事

`Components/App.razor` **沒有全域 `<title>`**，瀏覽器分頁標題完全由各頁自己的 `<PageTitle>` 決定 —— **`SystemName` 不參與**。沒有宣告 `<PageTitle>` 的頁面會顯示空白標題。

全專案 31 個 `<PageTitle>`（0.9.114）中只剩一處是範本殘留的英文：

| 檔案 | 目前標題 |
|------|------|
| `Components/Pages/Home.razor` | `Home` ← **這就是啟動頁（網站根路徑 `/`）的分頁標題** |

> `Components/Pages/HomeAuthed.razor` 原本也是 `Home`，0.9.9 已改為「首頁」；`Error.razor` 已是「系統錯誤」。

想讓每個分頁都帶產品名（例如「使用者管理 — 你的系統」），需要自行加後綴機制，本專案未提供。

### 7.6 仍然寫死、需要自行決定的文案

以下都是**設計文案**，刻意不參數化。要不要改由你決定，但更名時建議至少掃過一遍：

| 位置 | 目前文字 |
|------|------|
| `Components/Auths/Login.razor:10` | `使用者登入` / `請輸入您的帳號資訊以存取系統。`（`AuthShell` 的 `Title`／`Subtitle`）|
| `Components/Auths/Login.razor:58` | `企業級安全登入`（寄信啟用時此處改為「忘記密碼？」連結）|
| `Components/Auths/ForgotPassword.razor`、`ResetPassword.razor` | 兩頁的標題、副標題與提示文字（0.9.60 起）|
| `Components/Views/Commons/SplashView.razor:11` | `Welcome` |
| `Components/Views/Commons/HomeWelcomeView.razor` | `Welcome`（Hero 標籤）／`快速入口`／`儀表板`（0.9.106 起）／`系統版本`／`執行環境` |
| `Components/Views/Commons/HomeWelcomeView.razor.cs` 的 `AllQuickLinks` | 登入後首頁快速入口的標題與圖示：`專案項目`／`分類清單`／`團隊清單`（依權限顯示；原本的六張能力卡片已移除）。⚠️ 改成自家系統的入口時，**圖示名稱必須是 classic Material Icons**，用 Material Symbols 專有名稱會渲染失敗（可能是破圖方塊，也可能被拆成數個子字的圖示並撐破容器），改完請實際開 `/App` 確認 |
| `Components/Views/Commons/SplashView.razor:19` | `系統載入中，正在為你準備工作環境...` |
| `Components/Layout/NavMenu.razor` | `管理後台功能清單`（品牌文字下方副標）/ `功能選單`（選單區塊標題） |
| `Components/Layout/MainLayout.razor.cs` 的 `DefaultPageTitle` | `系統首頁`（頂列標題的 fallback，**不是**瀏覽器分頁標題） |
| `Components/Commons/ViewNotification.cs` 的 `SystemTitle` | `系統訊息`（全站通知的標題） |
| `MainLayout.razor`、`EmptyLayout.razor`、`NoFooterLayout.razor` | `An unhandled error has occurred.`（Blazor 預設，尚未中文化） |

另註：全專案**沒有** footer 或版權文字，`.csproj` 也沒有設定 `<Product>` / `<AssemblyTitle>`，所以組件層級沒有產品名要改。

### 7.7 換完怎麼確認

啟動系統後逐項檢查：

- [ ] 啟動頁（`https://localhost:7067/`）：品牌圖片、大標題（`SystemName`）、副說明（`SystemDescription`）
      —— 啟動頁只在驗證身分那一瞬間出現，會很快跳走，可先登出再開首頁觀察
- [ ] 登入頁（`/Auths/Login`）：同樣三項，且說明文字沒有溢出面板
- [ ] 登入後首頁（`/App`）：品牌圖片、大標題、副說明、快速入口（圖示皆為單一圖示、未溢出容器）、儀表板小工具與系統版本
- [ ] 登入後右上使用者選單 →「關於」：系統名稱／系統描述／系統版本三列正確
- [ ] 「系統健康監控」頁（`/system-health`）：診斷文字含正確的 `版本：x.y.z`
- [ ] 瀏覽器分頁圖示已換（先 `Ctrl+F5`）
- [ ] 檢視網頁原始碼，確認 `brand-logo` 的 URL 帶了 fingerprint（形如 `images/brand-logo.<雜湊>.png`）

---

## 8. 從腳手架複製出新專案並更名

目標：把 `CustomerHelpAgent` 這個代號整套換成你的新系統代號（以下以 **`Acme.Erp`** 為例），包含**資料夾名稱、檔案名稱、命名空間、組件名稱、設定值與文件**。

### 8.1 快速捷徑：`New-StarterProject.ps1`

repo 已附一支腳本能一次做掉絕大部分工作：

```powershell
pwsh ./scripts/New-StarterProject.ps1 `
     -ProjectName Acme.Erp `
     -DestinationPath D:\Work\Acme.Erp
```

> **整個方案跑這一次就好。** `-ProjectName` 是整份腳手架的代號，不是單一專案名：
> `.slnx` 內的 7 個專案、方案檔本身，以及外層 `src/CustomerHelpAgent/` 目錄，都在這一次執行中一起改名。
> 對照 [§8.2 步驟 2](#步驟-2改資料夾名稱由深到淺) 的資料夾對照表 —— **那整張表腳本會一次做完**，
> 不需要為了七個專案跑七次。

> 完整參數（`-Force`、`-SourceProjectName`、`-UserSecretsId`、`-KeepStarterHistory`、`-AllowUnsyncedSource`）見 [腳手架開發指引 §2.1](腳手架開發指引.md#21-建議順序)。

> ⚠️ **前置需求**：必須先安裝 `dotnet-ef`（`dotnet tool install --global dotnet-ef`）。
> 腳本一開始就會檢查，找不到會直接中止，不會留下半成品。

**腳本會做的事：**

- **先確認來源是最新的**（0.9.116 起）：腳本複製的是本機磁碟上的工作目錄，不是 git 的某個版本。開跑前會 `git fetch origin`，
  有未提交修改或未追蹤檔、不在 origin 的預設分支、落後 origin 任一項就列出並中止（確定要照現狀複製時加 `-AllowUnsyncedSource`）；
  不是 git repo、沒有 origin 或 fetch 失敗只警告。被 `.gitignore` 忽略、卻不在下方排除清單裡的檔案也會列出警告（不中止）
- 複製整個 repo 到目標路徑，跳過 `.git` / `bin` / `obj` / `.vs` / `.playwright-cli` / `output`，以及開發機上的本機產物 `artifacts` / `.gstack` / `PublishProfiles`、`*.user` / `*.suo`、`.claude\settings.local.json`（0.9.87 起）；複製完會刪掉因此變空的資料夾（例如已移除的 `SqlServerMigrations` 殘骸）
- 把文字檔（`.cs .csproj .slnx .json .md .razor .css .js .ps1 .yml .yaml .config .xml .tmpl .gitignore .props`；`.tmpl` 0.9.136 起、`.gitignore`／`.props` 0.9.137 起）內的 `CustomerHelpAgent` 全部換成新代號；全小寫的 `customerhelpagent`（例如 Gmail 指南的範例信箱）換成新代號的小寫（0.9.87 起）。**逐檔保留原本的 BOM 狀態**（`docs/*.md` 的 BOM 不會被抹掉）
- 由深到淺改資料夾名，再改檔名
- **產生一組新的 `UserSecretsId`** 寫入 Web 專案 csproj，並印在畫面上；文件裡引用到舊 Id 的路徑範例（本文 §5.2、§5.4）也會一併換成新值
- **產生一組新的開發連接埠**寫入 `Properties/launchSettings.json`（http 從 5000–5300、https 從 7000–7300 隨機挑本機可用的埠，同一台機器開多個衍生專案不會搶埠）。舊埠是從來源的 `launchSettings.json` 讀出來的（0.9.87 起），所以從衍生專案再衍生一次，埠也會換新；文件裡的 `localhost:<埠>` 網址（本文 §4、§7.7、§9，以及 Google OAuth 文件的 redirect URI）一併換成新埠。腳本結尾會印出新網址與要到 Google 註冊的 redirect URI
- **把 `SystemVersion` 重設為 `1.0.0 (執行當天)`**（0.9.71 起），文件開頭的「現行系統版本」也一併改為 `1.0.0`；「首次實作版本」保留原值（那是功能的歷史）
- 把 `JwtSettings:SigningKey` 換成 `<新代號>-ChangeThisJwtSigningKey-AtLeast32Chars`（⚠️ 仍是佔位符，上線前必須自己換掉；0.9.48 起 Production 啟動會擋下它）。`SupportPassword` 刻意**不動** —— 換成另一個固定佔位值並不會比較安全，真正的防線是啟動檢查
- **重建 EF Core migration**：清空 `<新代號>.AccessDatas/Migrations/`（腳手架的 migration 歷史對新專案沒有意義），刪除寫死舊 migration 名稱的 `CategoryTeamUniqueIndexMigrationTests.cs` 與 `TeamTreeMigrationTests.cs`（後者 0.9.116 起），先 `dotnet restore`，再以 `dotnet ef migrations add Init` 產生新專案的第一次 migration；失敗會中止並印出可手動重跑的指令
- **清掉腳手架自己的開發史**：清掉 `docs/changelog/` 的歷史紀錄（保留 `README.md` 當索引骨架 —— 維護規範要求每次異動寫一篇，新專案從自己的第一篇開始）；索引與內文裡指向被刪檔案的連結會一併移除或降級為純文字。加上 `-KeepStarterHistory` 可原封不動保留
- 最後掃一次殘留字串並警告。正常情況只會出現一行標題加 5 筆「開發用 JWT 佔位金鑰」的警告：本文 §5.1／§5.5 的說明、腳本自己的兩行、`ApiIntegrationTests.cs` 的測試資料，都是刻意保留的

> `-ProjectName` 每一段都必須是合法的 C# 識別字（0.9.87 起），`Acme..Erp`、`Acme.`、`Acme.1x` 會在一開始就被擋下。

**腳本做完之後你仍須手動處理：**

| 項目 | 說明 |
|------|------|
| 品牌圖檔 | `wwwroot/images/brand-logo.png`、`wwwroot/favicon.png` 是二進位檔，腳本不會動 —— 規格與作法見 [§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明) |
| 產品名稱與說明 | `appsettings.json` 的 `SystemSettings:SystemInformation`（`SystemName` / `SystemDescription`）—— 見 [§7.4](#74-更換產品名稱與簡短說明)。`SystemVersion` 腳本已重設為 `1.0.0`，不必再改 |
| 文件內文 | `docs/**` 與 `readme.md` 裡描述舊系統的敘述句（不只是代號） |
| 外部目錄路徑 | `ExternalFileSystem` 十一個路徑會被換成 `C:\temp\Acme.Erp\...`，確認是你要的位置 |
| 機密 | 用新的 `UserSecretsId` 重新設定一次 User Secrets（見 [§5](#5-機密設定檔user-secrets)） |

即使用了腳本，仍**強烈建議**照 [§8.3 高風險清單](#83-高風險清單) 逐條核對，再跑 [§9 驗證清單](#9-更名後驗證清單)。

### 8.2 手動更名步驟

想完全掌控過程、或腳本在你的環境出狀況時，照以下步驟做。

#### 步驟 1：複製並清乾淨

複製整個目錄後刪除這些（它們都是建置產物或本機狀態，留著會干擾後續的全域取代）：

```
.git/                                  ← 要開新的版控歷史
.playwright-cli/                       ← UI 測試快照
output/                                ← 產生器輸出
src/CustomerHelpAgent/.vs/                     ← Visual Studio 本機狀態
src/CustomerHelpAgent/**/bin/  **/obj/         ← 所有建置產物
src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas.SqlServerMigrations/   ← 殘骸目錄（只剩 bin/obj，沒有 csproj，也不在方案內）
src/CustomerHelpAgent/artifacts/  .gstack/                         ← 本機發佈輸出等產物（若有）
src/CustomerHelpAgent/CustomerHelpAgent.Web/Properties/PublishProfiles/   ← 本機發佈設定
src/CustomerHelpAgent/.claude/settings.local.json                 ← 個人的 Claude Code 權限設定
```

#### 步驟 2：改資料夾名稱（由深到淺）

先改內層再改外層，否則路徑會失效：

| 順序 | 舊名 | 新名 |
|------|------|------|
| 1 | `src/CustomerHelpAgent/CustomerHelpAgent.Share` | `src/CustomerHelpAgent/Acme.Erp.Share` |
| 2 | `src/CustomerHelpAgent/CustomerHelpAgent.Models` | `src/CustomerHelpAgent/Acme.Erp.Models` |
| 3 | `src/CustomerHelpAgent/CustomerHelpAgent.Dtos` | `src/CustomerHelpAgent/Acme.Erp.Dtos` |
| 4 | `src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas` | `src/CustomerHelpAgent/Acme.Erp.AccessDatas` |
| 5 | `src/CustomerHelpAgent/CustomerHelpAgent.Business` | `src/CustomerHelpAgent/Acme.Erp.Business` |
| 6 | `src/CustomerHelpAgent/CustomerHelpAgent.Web` | `src/CustomerHelpAgent/Acme.Erp.Web` |
| 7 | `src/CustomerHelpAgent/CustomerHelpAgent.Tests` | `src/CustomerHelpAgent/Acme.Erp.Tests` |
| 8 | `src/CustomerHelpAgent` | `src/Acme.Erp` |

#### 步驟 3：改檔案名稱

7 個 `.csproj` 加 1 個方案檔：

```
Acme.Erp.Share/CustomerHelpAgent.Share.csproj             → Acme.Erp.Share.csproj
Acme.Erp.Models/CustomerHelpAgent.Models.csproj           → Acme.Erp.Models.csproj
Acme.Erp.Dtos/CustomerHelpAgent.Dtos.csproj               → Acme.Erp.Dtos.csproj
Acme.Erp.AccessDatas/CustomerHelpAgent.AccessDatas.csproj → Acme.Erp.AccessDatas.csproj
Acme.Erp.Business/CustomerHelpAgent.Business.csproj       → Acme.Erp.Business.csproj
Acme.Erp.Web/CustomerHelpAgent.Web.csproj                 → Acme.Erp.Web.csproj
Acme.Erp.Tests/CustomerHelpAgent.Tests.csproj             → Acme.Erp.Tests.csproj
CustomerHelpAgent.slnx                                    → Acme.Erp.slnx
```

> **不需要**設定 `<AssemblyName>` 或 `<RootNamespace>` —— 本 repo 全部專案都沒有明確宣告這兩個屬性，組件名稱與根命名空間都隱含取自檔名。**改了 csproj 檔名，兩者就一起改好了。**

#### 步驟 4：全域文字取代

在 VS Code 按 `Ctrl+Shift+H`：

| 欄位 | 值 |
|------|-----|
| Search | `CustomerHelpAgent` |
| Replace | `Acme.Erp` |
| files to include | （留空 = 整個工作區） |
| files to exclude | `**/bin,**/obj,**/.vs,**/.git,**/.playwright-cli` |

按 `Alt+C` 開啟**大小寫相符**（Match Case），避免誤傷。取代前先看一次結果清單。

> 這一步會處理掉大部分內容：命名空間、`using`、`_Imports.razor`（25 行 `@using`）、`.csproj` 的 `ProjectReference`、`.slnx` 的專案路徑、`appsettings.json`、`docs/**`、`scripts/New-CrudModule.ps1` 與 `scripts/crud-templates/*.tmpl`（合計約 90 處）、`CLAUDE.md` / `AGENTS.md` / `src/CustomerHelpAgent/.github/copilot-instructions.md`。
>
> 開了大小寫相符，全小寫的 `customerhelpagent`（例如 `docs/operations/Gmail寄信設定指南.md` 的範例信箱）不會被換到，記得另外搜尋一次。

#### 步驟 5：換掉 `UserSecretsId`

```powershell
[guid]::NewGuid().ToString()
# 例如輸出 a1b2c3d4-5e6f-7890-abcd-ef1234567890
```

把新值填進 `src/Acme.Erp/Acme.Erp.Web/Acme.Erp.Web.csproj`：

```xml
<UserSecretsId>a1b2c3d4-5e6f-7890-abcd-ef1234567890</UserSecretsId>
```

> ⚠️ **不換的後果**：所有從這個腳手架複製出來的專案會共用同一份 `secrets.json`。A 專案設定的 Redis 連線字串會出現在 B 專案；在 A 專案 `dotnet user-secrets clear` 會把 B 專案的機密一起清掉。這種污染很難察覺，務必換掉。
>
> 換完記得 `dotnet build`（Id 是編譯期嵌入的），再依 [§5.3](#53-建立方式-a使用-cli建議) 重新設定機密。

#### 步驟 6：逐條核對高風險清單

見 [§8.3](#83-高風險清單)。步驟 4 的全域取代通常已經蓋掉這些，但**務必逐條確認**，因為漏掉其中幾條不會有任何錯誤訊息。

#### 步驟 7：設定值與品牌

`src/Acme.Erp/Acme.Erp.Web/appsettings.json`：

| 鍵 | 建議值 |
|----|--------|
| `SystemSettings:SystemInformation:*` | 產品名稱、簡短說明與版本 —— 完整說明見 [§7.4](#74-更換產品名稱與簡短說明)；版本建議比照腳本重設為 `1.0.0 (當天日期)` |
| `SystemSettings:ExternalFileSystem:*` | 十一個路徑，確認 `C:\temp\Acme.Erp\...` 是你要的 |
| `JwtSettings:Issuer` / `Audience` | `Acme.Erp` / `Acme.Erp.WebApi` |
| `JwtSettings:SigningKey` | 換成你自己的值；建議直接搬進 User Secrets |
| `BootstrapSettings:*` | 改掉預設的 `support` / `support` |
| `CacheSettings:InstanceName` | `Acme.Erp:`（Redis 鍵前綴，避免與其他系統衝突） |

品牌圖檔（二進位，全域取代不會處理）—— 尺寸規格、裁切行為與 favicon 產生指令見 **[§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明)**：

```
src/Acme.Erp/Acme.Erp.Web/wwwroot/images/brand-logo.png   ← 登入頁與啟動畫面
src/Acme.Erp/Acme.Erp.Web/wwwroot/favicon.png             ← 瀏覽器分頁圖示
```

#### 步驟 8：文件與工具設定

| 檔案 | 要改什麼 |
|------|----------|
| `readme.md` | 系統介紹、架構圖、專案結構樹、快速開始指令 |
| `docs/**` | 內文敘述（不只代號，還有描述舊系統功能的句子）。`docs/changelog/` 的歷史紀錄腳本已自動清掉（保留 `README.md`），手動更名才需要自己處理 |
| `CLAUDE.md` / `AGENTS.md` / `src/CustomerHelpAgent/.github/copilot-instructions.md` | LLM 協作準則裡的專案描述 |
| `.vscode/launch.json` / `tasks.json` / `settings.json` | 內含 `src/CustomerHelpAgent/...` 路徑，確認已更新 |

### 8.3 高風險清單

以下是**改資料夾名稱不會自動修好**、且多數**漏改也不會報錯**的字串。請逐條核對：

| 位置 | 內容 | 漏改的後果 |
|------|------|------------|
| `Components/App.razor:13` | `@Assets["CustomerHelpAgent.Web.styles.css"]` | ⚠️ **最危險**：Blazor scoped CSS 套件名取自組件名。漏改會讓**全站 scoped CSS 完全不載入**，畫面嚴重跑版，但**不會有任何錯誤訊息** |
| `CustomerHelpAgent.Web.csproj` | `<InternalsVisibleTo Include="CustomerHelpAgent.Tests" />` | 慣例守門測試無法編譯（會報錯，容易發現） |
| `Program.cs`（NLog 設定區段） | `typeof(Program).Namespace ?? nameof(CustomerHelpAgent.Web)` | 決定 NLog 目錄與檔名前綴。漏改會讓日誌寫到舊名目錄，系統內的「日誌檢視」頁讀不到任何資料 |
| `Program.cs`（`AddSwaggerGen`） | Swagger `Title = "CustomerHelpAgent API"` | Swagger UI 殘留舊系統名 |
| `Extensions/ApplicationBuilderExtensions.cs` | `SwaggerEndpoint(..., "CustomerHelpAgent API v1")` | 同上 |
| `Program.cs`（`AddCookie(MagicObjectHelper.CookieScheme, ...)`） | `options.Cookie.Name = ".CustomerHelpAgent.Auth"` | 登入 Cookie 名稱殘留舊名。Cookie 不分連接埠，同一主機名稱部署多個系統時會**互相覆蓋、互相登出**（`ApiIntegrationTests.AuthCookieName_ShouldBeProjectSpecific` 會紅） |
| `Program.cs`（`AddCookie(MagicObjectHelper.ExternalCookieScheme, ...)`） | `options.Cookie.Name = ".CustomerHelpAgent.External"` | 外部登入（OAuth）暫存 Cookie 名稱殘留舊名；同機多系統時可能互撞 |
| 🔴 `Share/Helpers/MagicObjectHelper.cs` | `DataProtectionApplicationName = "CustomerHelpAgent"` | **改了會讓全站既有登入 Cookie 立刻失效**（連同記住我）。這是 Data Protection 金鑰環的用途判別子。⚠️ 與上面那些不同，這一條**建議不要跟著改**；真要改請安排在可接受全體重新登入的時機 |
| `Configuration/CacheSettings.cs:9` | `InstanceName { get; set; } = "CustomerHelpAgent:"` | Redis 鍵前綴的程式預設值。共用 Redis 時會與其他系統鍵值衝突 |
| `Components/Views/Analytics/LogViewerView.razor.cs` | 下載檔名 `CustomerHelpAgent.Web-logs-{時間}.log` | 使用者下載的日誌檔名殘留舊名 |
| `AccessDatas/Migrations/*.Designer.cs`、`BackendDBContextModelSnapshot.cs` | 數百處 `modelBuilder.Entity("CustomerHelpAgent.AccessDatas.Models.X", ...)` 字串常值 | Model snapshot 與實際模型不符，EF Core 會誤判「有尚未產生的 Migration」 |
| `wwwroot/images/brand-logo.png`、`wwwroot/favicon.png` | 二進位圖檔，全域文字取代**完全不會處理** | 新系統掛著舊產品的圖示與品牌圖片。作法見 [§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明) |
| `CustomerHelpAgent.Tests/*.cs` | 數十支測試以字串或路徑比對專案名，例如 `LoggingConventionTests`、`ButtonIconConventionTests`、`MenuPermissionConsistencyTests`、`LogQueryServiceTests`、`SystemHealthTests`、`ApiIntegrationTests`、`PageHelpCatalogTests`、`StartupSafetyConventionTests` | 測試失敗。**這其實是好事** —— 它們是漏改的偵測網，所以 [§9](#9-更名後驗證清單) 一定要跑 `dotnet test` |
| `.vscode/launch.json`、`tasks.json`、`settings.json` | `src/CustomerHelpAgent/...` 路徑與 `CustomerHelpAgent.Web.dll` | F5 啟動失敗、任務找不到方案檔 |

### 8.4 不需要改的東西

避免過度改名造成不必要的風險：

| 項目 | 為什麼不用改 |
|------|--------------|
| `BackendDBContext` 類別名 | 本來就沒有品牌字，只是 DbContext 的名字 |
| `Migrations/` 的檔名（腳手架是 `20260316083639_init.cs` 起的一串；腳本重建後是 `<時間戳記>_Init.cs`） | 檔名是 EF 的時間戳記慣例，與專案名無關 |
| `launchSettings.json` 的 `http` / `https` profile 名稱 | 兩個 profile 名都是通用字，不含品牌 |
| `Directory.Build.props` / `global.json` / `.editorconfig` | 經確認**都不含** `CustomerHelpAgent` 字串 |
| `Directory.Packages.props` / `.gitignore` | 含 `CustomerHelpAgent`（前者是註解；後者是 `!src/CustomerHelpAgent/CustomerHelpAgent.Web/Backup/` 例外規則），0.9.137 起腳本會自動換掉，不必手動改。⚠️ 手動複製時 `.gitignore` 這一行一定要改，否則 `Backup/` 底下的程式碼被 `Backup*/` 忽略，提交不進 git、乾淨 clone 建置失敗 |
| `Datas/Menu.json` | 選單定義檔不含專案名 |
| `MagicObjectHelper` 的常數值 | `開發者帳號`、`預設角色`、`NeedChangePassword` 等都是業務語意，不是品牌 |

---

## 9. 更名後驗證清單

跑一次品質關卡。本專案**沒有 CI**（`.github/workflows/dotnet-ci.yml` 已於 2026-09-20 移除），所以每次提交前都要自己跑；要自行建立 CI 可參考 [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md)：

```powershell
# restore → Release 建置（任何警告都會失敗）→ format → 測試（守門測試會抓出漏改的專案名）→ 文件編碼 → 弱點掃描
pwsh ./scripts/Invoke-QualityGate.ps1
```

遇到失敗就停，結尾的摘要會指出停在哪一步；下表是各步驟常見的失敗原因。

手動驗收項目：

- [ ] `dotnet run --launch-profile https` 能啟動，`https://localhost:7067` 開得起來
- [ ] **畫面樣式正常**（若整站沒有樣式，回頭查 `App.razor` 的 `styles.css` —— 見 [§8.3](#83-高風險清單)）
- [ ] 能以 `BootstrapSettings` 設定的帳密登入
- [ ] Swagger UI（`/swagger`）標題正確，且能用 Bearer token 呼叫受保護 API
- [ ] `C:\temp\Acme.Erp\{DB,Download,Upload,ProjectFile,Exception,TokenUsage,AiCallLog,Keys,Backup}` 已正確產生
- [ ] `C:\temp\Logs\Acme.Erp.Web\` 有新日誌檔產生，且系統內的「日誌檢視」頁讀得到
- [ ] 全文搜尋（排除 `bin`/`obj`）已無任何 `CustomerHelpAgent` 殘留
- [ ] `git grep -i "DevelopmentOnly-ChangeThis"` 無殘留，或已改用 User Secrets
- [ ] `Acme.Erp.Web.csproj` 的 `UserSecretsId` 是新產生的 GUID

---

## 10. 常見疑難排解

| 症狀 | 原因 | 解法 |
|------|------|------|
| 日誌出現 `Failed to determine the https port for redirect.` | 用了 `http` profile，`UseHttpsRedirection` 找不到 HTTPS 埠（不影響使用，不會重導） | 改用 `https` profile（`.vscode/launch.json` 已預設），或忽略這筆警告 |
| 瀏覽器顯示憑證不受信任 | 尚未信任本機開發憑證 | `dotnet dev-certs https --trust` |
| `Address already in use` / 埠被占用 | `launchSettings.json` 的 `applicationUrl` 所用連接埠被其他程式占用 | 改 `launchSettings.json` 的 `applicationUrl`，或 `Get-NetTCPConnection -LocalPort <埠號>` 找出占用者 |
| **畫面完全沒有樣式** | `App.razor:13` 的 `CustomerHelpAgent.Web.styles.css` 沒跟著改名 | 改成 `<新代號>.Web.styles.css` |
| User Secrets 設了卻沒生效 | ①`ASPNETCORE_ENVIRONMENT` 不是 `Development` ②`UserSecretsId` 與實際目錄不符 ③改了 csproj 沒重建 | 逐項確認；`dotnet user-secrets list` 應讀得到值 |
| `dotnet user-secrets` 說找不到專案 | 不在 csproj 目錄下 | `cd src/.../*.Web`，或加 `--project <csproj 路徑>` |
| 建置因為一個小警告就失敗 | `Directory.Build.props` 的 `TreatWarningsAsErrors` | 修掉警告，別 disable。這是刻意的品質關卡 |
| `dotnet format --verify-no-changes` 失敗 | 格式不符 `.editorconfig` | 先跑一次不帶 `--verify-no-changes` 的 `dotnet format` 讓它自動修 |
| `Test-DocsEncoding.ps1` 失敗 | 某個 `docs/**/*.md` 或根目錄 `*.md` 缺 BOM 或含 U+FFFD 亂碼 | 以 UTF-8 **含 BOM** 重存。用 PowerShell 寫檔時必須 `-Encoding utf8BOM`，**`-Encoding utf8` 在 pwsh 7 不含 BOM** |
| EF 說有尚未套用的模型變更 | Migration 的 `.Designer.cs` / snapshot 內的 `CustomerHelpAgent.AccessDatas.Models.*` 字串沒換 | 對 `Migrations/` 目錄再做一次全域取代 |
| 登入失敗，或密碼自己變回舊值 | `SupportUserSeeder` 的密碼救援覆寫行為（開發環境還會讀 User Secrets 的 `BootstrapSettings`，它優先於 appsettings） | 見 [§6.4](#64-預設管理者帳號)。要改密碼請同步改 `BootstrapSettings:SupportPassword` |
| 部署到 IIS 後啟動即中止，本機卻正常 | IIS 沒設 `ASPNETCORE_ENVIRONMENT` 時預設是 Production，會跑 `StartupSafetyValidator`；開發機是 Development，不做這項檢查 | 看 Windows 事件檢視器的「Production 啟動安全檢查失敗」訊息，逐項補齊；見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md) |
| 資料庫檔刪不掉 | 程式還在執行，SQLite 檔案被鎖 | 先停掉程式（VS Code 按 `Shift+F5`）再刪 |
| C# Dev Kit 找不到方案 | 工作區開錯層級 | 用 `File > Open Folder` 開 **repo 根目錄**；`.vscode/settings.json` 的 `dotnet.defaultSolution` 已指定方案檔 |

---

## 11. 相關文件

| 文件 | 內容 |
|------|------|
| [開發慣例與限制速查](../architecture/開發慣例與限制速查.md) | **改程式前必讀**：分層、Migration、權限同步等不變量 |
| [腳手架總覽與複刻上手](../features/腳手架總覽與複刻上手.md) | 給新手的白話導覽：內建功能、AI 協作提示詞、複刻流程 |
| [腳手架新專案啟動流程](腳手架新專案啟動流程.md) | 改名與 API/DTO 待辦的勾選式檢查清單 |
| [日誌與設定檔說明](../operations/日誌與設定檔說明.md) | `appsettings.json` 每個區段的完整說明 |
| [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md) | 上線前必須處理的機密、帳號與 Swagger 設定 |
| [Gmail 寄信設定指南](../operations/Gmail寄信設定指南.md) | 用 Gmail 帳號寄信（忘記密碼、例外告警）的逐步設定 |
| [密碼種類與儲存機制](../security/密碼種類與儲存機制.md) | 各種密碼與金鑰分別存在哪、怎麼雜湊 |
| [Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md) | Google SSO 憑證申請與設定 |
| [EFCore 指令備忘](EFCore.md) | Migration 指令範本 |
| [測試指南](測試指南.md) | 測試分類、本機執行與覆蓋率 |
| [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md) | 參考設計：本專案目前沒有 CI，要自行建立時的步驟與弱點掃描允許清單 |
| [維護規範](../operations/維護規範.md) | 版本號、文件同步與 commit 前的檢查 |
| [建立一個新 CRUD 操作網頁說明](建立一個新%20CRUD%20操作網頁說明.md) | 新專案開好後，怎麼加第一個功能模組 |

> 返回 [文件總索引](../README.md)
