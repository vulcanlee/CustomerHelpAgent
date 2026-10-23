# EF Core 指令備忘

- 文件版本：1.3
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/10/23

> **前置說明**
>
> - 本專案**只支援 SQLite**（0.4.24 起已移除 SQL Server 軌道），只有一個 `DbContext`：`BackendDBContext`。
> - `BackendDBContext` 與 migration 在 `CustomerHelpAgent.AccessDatas`（migration 放在 `CustomerHelpAgent.AccessDatas/Migrations/`）；
>   `CustomerHelpAgent.Web` 是啟動專案（參考 `Microsoft.EntityFrameworkCore.Design`，`dotnet ef` 從它的設定與 DI 取得 `DbContext`）。
>   所以 `--project` 指 AccessDatas、`--startup-project` 指 Web，兩個都要給。
> - 需要全域工具 `dotnet-ef`：`dotnet tool install --global dotnet-ef`（repo 沒有 local tool manifest）。
> - 以下指令以 **repo 根目錄**為工作目錄；在 `src/CustomerHelpAgent/` 底下執行時把路徑改成 `CustomerHelpAgent.AccessDatas`／`CustomerHelpAgent.Web`。

## 1. 新增 migration（改了 Entity 或欄位一定要做）

```powershell
dotnet ef migrations add AddInvoiceNote --project src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas --startup-project src/CustomerHelpAgent/CustomerHelpAgent.Web
```

- 名稱用 PascalCase 描述這次變更（例：`AddAuditLogClientIp`）；全小寫的類別名會觸發 CS8981，在 `TreatWarningsAsErrors` 下建置失敗。
- 產出 `<時間戳>_<名稱>.cs`、`.Designer.cs`，並更新 `BackendDBContextModelSnapshot.cs`，三個檔都要提交。
- `dotnet ef` 會在設計階段建立 Web 主機（讀 `appsettings.json` 的外部目錄與日誌路徑）。不想寫進開發機的 `C:\temp` 時，
  比照 `scripts/New-CrudModule.ps1` 的 migration 區段，先用環境變數把 `SystemSettings__ExternalFileSystem__*` 與 `NLog__BasePath` 導到暫存目錄。

## 2. 套用到資料庫（通常不用手動做）

系統**每次啟動時自動套用**尚未套用的 migration（0.9.91 起由 `CustomerHelpAgent.Business/Startup/DatabaseInitializer` 執行，含跨行程鎖），
本機開發直接啟動網站即可。要不啟動網站就更新開發資料庫時：

```powershell
dotnet ef database update --project src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas --startup-project src/CustomerHelpAgent/CustomerHelpAgent.Web
```

更新的是設定裡 `SystemSettings:ExternalFileSystem:DatabasePath` 指向的 `BackendDB.db`。

## 3. 移除最後一個 migration

```powershell
dotnet ef migrations remove --project src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas --startup-project src/CustomerHelpAgent/CustomerHelpAgent.Web
```

- 只能移除**最後一個**，並還原 snapshot。
- 它會先檢查資料庫：若網站已經啟動過、自動套用了這個 migration，會拒絕移除。開發資料庫可以加 `--force`（先在資料庫上還原再移除）；
  已經提交或部署出去的 migration 不要移除，改再加一個 migration 修正。

## 4. 產生 SQL 腳本（審查或交給 DBA）

```powershell
dotnet ef migrations script --idempotent --output artifacts/migrations.sql --project src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas --startup-project src/CustomerHelpAgent/CustomerHelpAgent.Web
```

- `--idempotent` 產生可重複執行、會先檢查 `__EFMigrationsHistory` 的腳本；只要某一段時用 `dotnet ef migrations script <From> <To>`。
- 本系統的正式升級靠啟動時自動套用，腳本主要用來**事先看清楚會執行什麼 SQL**（尤其是下一節的整表重建）。

## 5. Visual Studio 套件管理器主控台（PMC）對照

預設專案選 `CustomerHelpAgent.AccessDatas`，或每個指令都帶 `-Project CustomerHelpAgent.AccessDatas -StartupProject CustomerHelpAgent.Web`：

| dotnet CLI | PMC |
|---|---|
| `dotnet ef migrations add <名稱>` | `Add-Migration <名稱>` |
| `dotnet ef database update` | `Update-Database` |
| `dotnet ef migrations remove` | `Remove-Migration` |
| `dotnet ef migrations script --idempotent` | `Script-Migration -Idempotent` |

## 6. ⚠️ SQLite 的整表重建不在交易保護內

SQLite 不支援的結構變更（`DropColumn`、改欄位型別、對既有資料表加外鍵等），EF Core 會改成
「建新表 → 搬資料 → 丟舊表 → 改名」的整表重建，套用時 EF 會警告
`The migration operation 'PRAGMA foreign_keys = 0;' ... cannot be executed in a transaction.`
若程序在執行中途被中斷，migration 會停在**部分套用**的狀態，必須手動還原才能重跑。

- 產生 migration 後打開 `.cs` 檢查有沒有這類操作；能改寫成 `ALTER TABLE … ADD COLUMN` 的就改寫（例：`AddTeamParent`，見[速查表 §6.22](../architecture/開發慣例與限制速查.md)）。
- 加唯一索引前先清理既有的重複資料，否則每次啟動都會套用失敗（速查表 §4.2）。
- **正式環境升級前一定先備份 `BackendDB.db`**（[備份與還原操作手冊](../operations/備份與還原操作手冊.md)）—— 這是部分套用時唯一的復原手段。

> 返回 [guides 索引](README.md)
