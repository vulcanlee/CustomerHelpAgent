# CI-CD 與品質檢查

- 文件版本：1.12
- 文件狀態：已實作（本機品質關卡）
- 現行系統版本：1.0.0
- 首次實作版本：0.2.8
- 最後核對日期：2026/10/23

> ⚠️ **腳手架沒有 CI。** 原本的 GitHub Actions 工作流程已於 **2026-09-20** 移除，repo 根目錄沒有 `.github/` 資料夾。
> **沒有任何機制會自動擋下違規**，品質關卡全靠提交前在本機執行 `scripts/Invoke-QualityGate.ps1`（0.9.89 起，見 §1）。

---

## 1. 提交前跑一行品質關卡（0.9.89 起）

```powershell
pwsh ./scripts/Invoke-QualityGate.ps1          # 完整關卡，約 1 分鐘（0.9.89 實測 42～66 秒，已建置過的情況）
pwsh ./scripts/Invoke-QualityGate.ps1 -Quick   # 略過弱點掃描（不需連網），約少 10 秒
pwsh ./scripts/Test-CrudGenerator.ps1           # 不在關卡裡（數分鐘）：修改 CRUD 產生器或樣板後必跑（0.9.110 起），在暫存 worktree 產生三種模組並跑全部測試
```

從任何目錄執行都可以（腳本以自己的位置找 repo 根目錄）。**全部通過（結束代碼 0）才提交。**
依序執行下列步驟，**遇到第一個失敗就停止**，結尾印出各步驟的結果與耗時，失敗時結束代碼為 1：

| # | 步驟 | 指令 | 擋什麼 |
|---|------|------|--------|
| 1 | Restore | `dotnet restore` | 套件還原失敗（腳本會把 `NUGET_HTTP_TIMEOUT_SECONDS` 設為 180，見 §4）|
| 2 | Build | `dotnet build --configuration Release --no-restore` | 編譯錯誤與**任何警告**（`TreatWarningsAsErrors`）|
| 3 | Format | `dotnet format --verify-no-changes --no-restore` | 格式不符 `.editorconfig`；改跑不帶旗標的 `dotnet format` 會自動修正 |
| 4 | Test | `dotnet test --configuration Release --no-build` | 1774 個測試（0.9.136），含十多組慣例守門測試 |
| 5 | Docs encoding | `scripts/Test-DocsEncoding.ps1` | `docs/**/*.md` 與根目錄 `*.md` 沒有 BOM 或有亂碼（見 §3）|
| 6 | Vulnerability | 解析 `dotnet list package --vulnerable` 的輸出 | 不在允許清單中的套件弱點，以及已過時的允許清單（見 §4）。`-Quick` 時略過 |

**為什麼用 Release**：與部署產物一致；而且開發中的網站鎖住的是 Debug 的 `bin`，
用 Release 建置不會跟它搶檔案，網站開著也能跑關卡。

VS Code 使用者可執行 [`.vscode/tasks.json`](../../.vscode/tasks.json) 的 `quality-gate` 任務。
`build`／`test`／`format-check`／`docs-encoding` 四個任務保留給「只想單獨跑某一道」時使用（它們用 Debug 組態）。

### 1.1 選用：push 前自動執行

```powershell
pwsh ./scripts/Install-GitHooks.ps1             # 安裝 pre-push hook：每次 git push 先跑 -Quick 關卡
pwsh ./scripts/Install-GitHooks.ps1 -Uninstall  # 移除
```

- **不會自動安裝**：每次 push 都要等測試跑完，是否接受由你決定。臨時要跳過用 `git push --no-verify`。
- 已經有別的 pre-push hook 時拒絕覆寫（加 `-Force` 才覆寫）；`-Uninstall` 只會刪除本腳本裝的那一個。
- hooks 目錄以 `git rev-parse --git-path hooks` 取得，worktree 與自訂 `core.hooksPath` 都適用。

### 1.2 自建 CI 時

在任何 CI 平台上只要執行同一行指令即可，不用把關卡重寫成 YAML 步驟：
換平台時改的只有「怎麼呼叫這支腳本」。測試細節見 [測試指南](../guides/測試指南.md)。

> 品質關卡用 `--configuration Release`；VS Code 的單項任務則用預設 Debug 組態。兩者都會套用 `TreatWarningsAsErrors`。

---

## 2. 建置設定與品質防線（0.4.32 起）⚠️

品質關卡由四個檔案構成，**新增專案或升級套件時請一律改這裡，不要回頭寫進個別 `.csproj`**：

| 檔案 | 作用 |
|------|------|
| [`.editorconfig`](../../.editorconfig)（repo 根目錄）| 程式碼格式規則，供 `dotnet format` 與 IDE 依循；提交前以 `dotnet format --verify-no-changes` 檢查（§1）。 |
| [`src/CustomerHelpAgent/Directory.Build.props`](../../src/CustomerHelpAgent/Directory.Build.props) | 全方案共用建置屬性：`Nullable`、`ImplicitUsings`、**`TreatWarningsAsErrors`**，以及 CVE 抑制。 |
| [`src/CustomerHelpAgent/Directory.Packages.props`](../../src/CustomerHelpAgent/Directory.Packages.props) | Central Package Management：所有套件版本的單一來源。 |
| [`global.json`](../../global.json)（repo 根目錄）| 鎖定 .NET SDK 版本（`10.0.400` + `rollForward: latestFeature`）。 |

幾個要點：

- **`TreatWarningsAsErrors` = true**：專案長期維持 0 warning，此設定是為了鎖住這個成果、避免警告悄悄回流。
  需要豁免時請針對**單一規則碼**加 `NoWarn` 並註明原因與解除條件（比照 `NuGetAuditSuppress` 的寫法），**不要整包關閉**。
  注意它是 MSBuild 屬性，**不只影響編譯** —— NuGet restore 階段的 `NU****` 警告同樣會被升級為 error。
- **唯一的警告豁免：`WarningsNotAsErrors` = `NU1900`**（0.4.47 起）。
  `NU1900` 是「**取不到**弱點資料」（NuGet Audit 連不上 nuget.org 弱點索引，逾時／限流／暫時性網路失敗），
  與「發現弱點」無關；被升級成 error 後，nuget.org 抖一下就會讓還原與建置失敗（當時的建置就因此中斷，見
  changelog 0.4.47）。
  用 `WarningsNotAsErrors` 而非 `NoWarn`，是為了讓警告仍印在 log 上、看得出是否常態性連不到來源。
  **真正代表發現弱點的 `NU1901`~`NU1904` 不在豁免清單，維持 error。**
- **各 `.csproj` 不再寫 `Nullable` / `ImplicitUsings` / 套件 `Version`**，只保留自己特有的設定
  （`TargetFramework`、`UserSecretsId`、`IsPackable` 等）。
- **`.editorconfig` 的定位是「描述現有慣例」**，不是引入新風格重新格式化整個 repo。
  因此刻意**不強制** namespace 宣告形式、`this.` 前綴、`var` 用法、檔案 BOM 與 using 排序
  —— 這些在專案中兩種寫法並存，強制會產生大量與需求無關的異動。

這些檔案在本機建置時就會生效；提交前的完整檢查見 §1。

---

## 3. 文件編碼檢查 ⚠️

`scripts/Test-DocsEncoding.ps1` 會**遞迴**掃描 `docs/` 下所有 `.md`，加上 repo 根目錄的 `*.md`（`readme.md`、`CLAUDE.md`、`AGENTS.md`，0.9.89 起），逐檔驗證：

- **必須含 UTF-8 BOM**（檔頭 `EF BB BF`），缺少即失敗。
- **不得含取代字元**（`U+FFFD`），出現代表編碼轉換時已產生亂碼。

> 注意：檔案移入子目錄後，此腳本以 `-Recurse` 涵蓋所有層級。以 PowerShell 建立／另存文件時請使用含 BOM 的 UTF-8（例如 `Set-Content -Encoding utf8BOM`），避免被擋下。編碼規定詳見 [維護規範 §3](維護規範.md)。

它是品質關卡（§1）的第 5 步，也可以單獨執行（從任何目錄都可以）：

```powershell
pwsh ./scripts/Test-DocsEncoding.ps1
```

只會印出失敗的檔案，最後一行是檢查的檔案總數。

---

## 4. 弱點掃描

`dotnet list package --vulnerable --include-transitive` 會列出含已知弱點的直接與遞移相依套件。發現弱點時應升級對應套件版本。

> 現況（0.9.89 起）：已納入本機品質關卡（§1 第 6 步），允許清單是 `scripts/Invoke-QualityGate.ps1` 的 `$allowedAdvisories`。

為降低 NuGet 來源偶發逾時造成的假失敗，品質關卡在開頭把 `NUGET_HTTP_TIMEOUT_SECONDS` 設為 180（你自己設定過就不覆蓋），涵蓋其後全部步驟。

⚠️ **這個指令找到弱點時仍然回傳 0。** 直接執行它等於沒有關卡（0.9.32 之前就是這樣，從來沒有擋下過任何東西）。
0.9.32 起改由 pwsh 解析輸出並比對**允許清單**：

| 情況 | 結果 |
|------|------|
| 出現不在允許清單中的諮詢 | ❌ 失敗 |
| 只出現允許清單中的諮詢 | ✅ 通過 |
| 允許清單中某筆**已不再出現**（上游修好了）| ❌ 失敗，要求你把它從清單與本文件移除 |

最後一列是刻意的：豁免只能是暫時的。清單在 `scripts/Invoke-QualityGate.ps1` 的 `$allowedAdvisories`，
目前只有一筆 `GHSA-2m69-gcr7-jv3q`（見 §4.1）。
**新增或移除清單項目時，本節與 §4.1 要一起改。**
比對用諮詢代號而非訊息文字 —— dotnet CLI 的輸出會隨執行環境語系改變。

核心邏輯如下（完整版見 `Invoke-QualityGate.ps1` 的 `Invoke-VulnerabilityScan`，它另外在擷取輸出時暫時把 `[Console]::OutputEncoding` 改成 UTF-8，避免繁中 Windows 上的中文訊息變亂碼）：

```powershell
$allowed = @('GHSA-2m69-gcr7-jv3q')
$output = dotnet list src/CustomerHelpAgent/CustomerHelpAgent.slnx package --vulnerable --include-transitive 2>&1 | Out-String
$found = [regex]::Matches($output, 'advisories/(?<id>[A-Za-z0-9-]+)') |
  ForEach-Object { $_.Groups['id'].Value } | Sort-Object -Unique
$unexpected = @($found | Where-Object { $allowed -notcontains $_ })   # 非空 → 失敗
$stale = @($allowed | Where-Object { $found -notcontains $_ })        # 非空 → 失敗（清單過時）
```

> 0.9.59 新增 `MailKit` 4.18.0（寄信服務，只加在 `CustomerHelpAgent.Web`），遞移帶入 `MimeKit` 4.18.0 與
> `BouncyCastle.Cryptography` 2.7.0。加入當下 `--vulnerable --include-transitive` 對這三個套件**沒有任何諮詢**，
> 允許清單不需變動。
> 2026-10-13 起 `MimeKit` 遞移帶入的 `System.Security.Cryptography.Pkcs` 10.0.0 出現弱點諮詢，
> 0.9.126 以直接參考修補版處理，允許清單仍不需變動（見 §4.3）。

它與 restore/build 階段的 `NU1903` 稽核警告仍是兩條獨立路徑。

### 4.1 已知並已抑制的弱點：CVE-2025-6965

| 項目 | 內容 |
|------|------|
| 套件 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（bundled SQLite < 3.50.2） |
| Advisory | [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) / CVE-2025-6965（High，CVSS 7.2） |
| 引入來源 | 由 `Microsoft.EntityFrameworkCore.Sqlite 10.0.5` **遞移**引入（EF Core Sqlite → Microsoft.Data.Sqlite.Core → SQLitePCLRaw.bundle_e_sqlite3 → lib.e_sqlite3） |
| 為何不升級 | NuGet 上 `SQLitePCLRaw.*` 最新即 2.1.11，**尚無修補版**（無 2.1.12 / 2.2.x），EF Core 亦未帶入新版，目前無從升級 |
| 風險評估 | 低：EF Core 採參數化查詢，無未受信任的原始 SQL 進入 SQLite（0.4.24 起 SQLite 為唯一支援的資料庫） |
| 處置 | 於 [`src/CustomerHelpAgent/Directory.Build.props`](../../src/CustomerHelpAgent/Directory.Build.props) 以 `NuGetAuditSuppress` 抑制該 advisory，消除 restore/build 的 `NU1903` 警告 |

**重要行為差異**：`NuGetAuditSuppress` 只抑制 **restore/build 的 `NU1903` 警告**；`dotnet list package --vulnerable` 是獨立查詢，**仍會列出**此 advisory。因此掃描時看到它屬預期；它必須同時出現在 `Invoke-QualityGate.ps1` 的 `$allowedAdvisories` 允許清單中，兩處缺一不可。

**移除條件**：待 `SQLitePCLRaw`（或 `Microsoft.EntityFrameworkCore.Sqlite`）釋出 bundled SQLite ≥ 3.50.2 的版本後，升級套件、移除 `Directory.Build.props` 內的 `NuGetAuditSuppress`、並刪除本小節。

### 4.2 已解決：GHSA-v5pm-xwqc-g5wc（0.4.32）

| 項目 | 內容 |
|------|------|
| 套件 | `Microsoft.OpenApi` 2.4.1（受影響範圍 `>= 2.0.0-preview.11, <= 2.7.4`，修補版 **2.7.5**）|
| Advisory | [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)（High）—— 循環 schema 參考可導致 OpenAPI 解析中止 |
| 引入來源 | 由 `Swashbuckle.AspNetCore` 10.1.5 → `Swashbuckle.AspNetCore.Swagger` **遞移**引入 |
| 處置 | **升級 `Swashbuckle.AspNetCore` 10.1.5 → 10.2.3**。10.2.1 起其相依改為 `Microsoft.OpenApi >= 2.7.5`，弱點自然消失，**不需要抑制、也不需要 pin 遞移套件** |

> 這是導入 `TreatWarningsAsErrors` 時才浮現的：該弱點以 `NU1903` 警告形式存在，
> 原本不會讓建置失敗，因此在 0.4.31 之前一直沒被注意到。

### 4.3 已解決：GHSA-2fmv-rv2c-59x7、GHSA-846f-f764-76mx（0.9.126）

| 項目 | 內容 |
|------|------|
| 套件 | `System.Security.Cryptography.Pkcs` 10.0.0（受影響範圍 `>= 10.0.0, <= 10.0.12`，修補版 **10.0.13**）|
| Advisory | [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7)（中，CVE-2026-70162）、[GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx)（高，CVE-2026-84008）—— 兩則都是 .NET 阻斷服務（DoS），2026-10-13 發布 |
| 引入來源 | 由 `MailKit` 4.18.0 → `MimeKit` 4.18.0 **遞移**引入（`PDFsharp-MigraDoc` 6.2.4 也相依 8.0.1，但 NuGet 解析到最高的 10.0.0）|
| 症狀 | 還原時出現 `NU1902`／`NU1903`，被 `TreatWarningsAsErrors` 升級成 error，`CustomerHelpAgent.Web` 與參考它的 `CustomerHelpAgent.Tests` 都無法建置（VS 與 `dotnet build` 相同）|
| 為何不升級 MailKit | `MimeKit` 最新版 4.18.1 仍相依 Pkcs 10.0.0，升級沒有效果 |
| 處置 | 於 `Directory.Packages.props` 加 `System.Security.Cryptography.Pkcs` 10.0.13，並由 `CustomerHelpAgent.Web.csproj` **直接參考**覆蓋遞移版本（直接參考會流向 `CustomerHelpAgent.Tests`）。不啟用全域 `CentralPackageTransitivePinningEnabled`，避免影響其他遞移套件；不用抑制，因為已有修補版 |

**移除條件**：待 `MailKit`／`MimeKit` 改為相依 Pkcs ≥ 10.0.13 後，升級 `MailKit`，並移除 `Directory.Packages.props` 的 `PackageVersion` 與 `CustomerHelpAgent.Web.csproj` 的 `PackageReference`。

---

## 5. 延伸閱讀

- [測試指南](../guides/測試指南.md) — 測試類別、本機執行與覆蓋率。
- [維護規範](維護規範.md) — 版本 bump、文件同步與 commit 前檢查清單。
- [正式部署與安全檢查清單](正式部署與安全檢查清單.md) — 上線前必查項目。
