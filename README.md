# NET10-Blazor-Starter

一份基於 **.NET 10** 與 **Blazor Server**（全域 SSR）所建立的企業級應用程式樣板，預先整合 Ant Design Blazor、EF Core、Cookie 認證、角色權限、多語系、檔案上傳、Swagger 與 NLog，協助開發團隊以最低成本啟動內部管理類系統。

當前版本資訊定義在 [`src/CustomerHelpAgent/CustomerHelpAgent.Web/appsettings.json`](src/CustomerHelpAgent/CustomerHelpAgent.Web/appsettings.json) 之 `SystemSettings.SystemInformation.SystemVersion` 欄位。

---

## 1. 專案介紹

- **定位**：可立即啟動、預先配置、易於擴充的 Blazor Server 樣板。
- **適用情境**：管理後台、內部營運系統、專案追蹤平台。
- **設計理念**：分層清晰、慣例優先、模板可複刻（CRUD 模板可直接套用至新實體）。

---

## 2. 技術棧

| 類別 | 採用方案 | 版本 |
|------|----------|------|
| 框架 | .NET / ASP.NET Core | 10.0 |
| 前端 | Blazor Server（Interactive Server）+ 全域 SSR 渲染 | — |
| UI 元件庫 | AntDesign Blazor | 1.6.0 |
| 圖示 | BlazorMaterialIcons（Google Material Icons） | 0.0.1 |
| 資料存取 | Entity Framework Core | 10.0.5 |
| 資料庫 | SQLite（唯一支援；檔案位置由 `appsettings.json` 控制） | — |
| 認證 | ASP.NET Core Cookie Authentication（含 RememberMe） | — |
| 授權 | 宣告式 RBAC：`Menu.json` id → 權限鍵 → `IPermissionChecker`（動作級 `view/create/edit/delete`）| — |
| 語系 | `RequestLocalization` + `AntDesignLocaleFactory` | 只支援 zh-TW（0.9.136 起）|
| 物件對映 | AutoMapper | 16.1.1 |
| API 文件 | Swashbuckle Swagger UI | 10.2.3 |
| 日誌 | NLog.Web.AspNetCore | 6.1.2 |
| Markdown 渲染 | Markdig（AI 分析結果，經安全管線後渲染）| 1.3.2 |
| PDF 產生 | PDFsharp + MigraDoc（AI 分析報告，內嵌中文字型）| 6.2.4 |
| 寄信 | MailKit（`EmailSettings` 切換 None／Pickup／Smtp，0.9.59）| 4.18.0 |
| 弱點修補 | System.Security.Cryptography.Pkcs（`CustomerHelpAgent.Web` 直接參考，覆蓋 MailKit → MimeKit 遞移帶入的弱點版 10.0.0，0.9.126；MimeKit 改相依修補版後移除）| 10.0.13 |
| QR Code | QRCoder（兩步驟驗證的設定 QR，0.9.104）| 1.6.0 |
| Excel 匯出 | ClosedXML（業務頁匯出 .xlsx，0.9.107）| 0.105.1 |
| 排程 | Cronos（排程作業的 cron 運算式，0.9.96）| 0.13.0 |

---

## 3. 系統架構速覽

```
CustomerHelpAgent.Web ──► CustomerHelpAgent.Business ──► CustomerHelpAgent.AccessDatas
       │                  │                       │
       └──► CustomerHelpAgent.Dtos / CustomerHelpAgent.Models / CustomerHelpAgent.Share
```

| 專案 | 角色 |
|------|------|
| `CustomerHelpAgent.Web` | Blazor Server 宿主、Razor 元件、Web API Controller、登入頁、本地化、靜態資源。 |
| `CustomerHelpAgent.Business` | 商業邏輯：Services、Repositories、AutoMapper Profile、Helpers。 |
| `CustomerHelpAgent.AccessDatas` | EF Core `BackendDBContext`、Entity 定義、Migrations。 |
| `CustomerHelpAgent.Models` | 系統設定、AdapterModel、共用領域模型。 |
| `CustomerHelpAgent.Dtos` | API 對外傳輸物件：`ApiResult<T>`、`PagedResult<T>`、各模組 DTO。 |
| `CustomerHelpAgent.Share` | 跨層共用：Helpers、Extensions（無外部相依）。 |

啟動流程詳見 [docs/architecture/架構總覽.md](docs/architecture/架構總覽.md)。

---

## 4. 主要功能

- 使用者帳號 CRUD（含預設開發者帳號自動 Seed）
- 角色管理（`RoleView`）與二維權限樹（對應 `Menu.json`）
- 登入 / 登出（Cookie 驗證、記住我、4 位數驗證碼、粉梅暖雪 RWD 果凍 UI 與柔和飄雪動畫）
- 忘記密碼：以帳號或 Email 申請、信中連結重設（30 分鐘、單次使用、防帳號列舉、同帳號冷卻），需啟用寄信（0.9.60）；
  0.9.85 起輸入內建帳號 `support` 會明確拒絕、送出後改顯示中性說明，「使用者管理」存檔時檢查 Email 格式
- 專案領域實體 CRUD（可作為新增其他領域模組的樣板）
- 資料定義主資料：分類清單（Category）、團隊清單（Team）管理頁面與 Web API
  （0.4.40 起分類可指定適用團隊、下拉依使用者所屬團隊過濾；0.4.41 起名稱唯一性由資料庫唯一索引保證）
- 紀錄分類/團隊標籤：專案可標記分類與團隊，並支援團隊行級權控（非管理員僅見公開或團隊交集紀錄）；
  使用者的有效團隊＝直接綁定的 `UserTeam` ∪ 其角色的 `DefaultTeamsJson`
- 每筆紀錄可附加多檔案，自動依年月分目錄存放
- Web API（含 Swagger UI、`ApiResult<T>` 信封、分頁搜尋）
- 平行 API 路由：保留 `/api/...`，新增 `/api/v1/...` 作為新用戶端標準入口
- Health checks：`/health/live`、`/health/ready`
- 系統健康監控頁：`/system-health`，管理員可查看健康百分比、紅黃綠燈號與最近 24 小時 WARN 以上日誌（0.9.111 起）；0.9.59 起含「寄信服務」檢查與寄信測試，
  0.9.79 起含「日誌管線」自我監控、0.9.96 起含「排程作業」（共 14 項）；0.9.122 起可「發送測試通知」給全體管理員
- 寄信服務：`IEmailSender`（同步）與 `IEmailQueue`（背景佇列）兩個入口，`EmailSettings` 切換不寄／寫 `.eml` 檔（開發）／MailKit SMTP；出貨預設不寄信（0.9.59）
- 日誌檢視頁：`/logs`，管理員可依等級／關鍵字／時間區間查詢並匯出（0.4.26）；
  0.9.69 起可全部展開／收合、點列複製單筆、一鍵複製查詢結果，0.9.78 起可依錯誤追蹤碼篩選，
  0.9.111 起有「最近 5 分／15 分／1 小時／今天」快速區間與展開列「查前後 1 分鐘」
- 日誌 AI 分析：日誌檢視頁可把查詢結果送 Azure OpenAI 或 OpenAI 整理，結果以唯讀對話窗呈現，
  可複製或匯出成 PDF 報告；供應商與金鑰在 `appsettings.json` 設定（0.9.4）
- 資料庫用量頁：`/database-usage`，管理員可查看各資料表筆數與估算用量（0.4.28）
- 日誌等級設定頁：`/log-level-setting`，管理員可在執行期調整日誌等級（0.4.29）
- 系統例外紀錄：全專案任何 `logger.LogError(ex, …)` 自動收進 `/system-exceptions`，依簽章聚合並保存堆疊檔（0.9.11）；
  0.9.73 起可點列複製（明細＋完整堆疊）與複製目前查詢結果
- AI 例外分析：例外明細窗按「AI 分析」，產生管理者摘要與技術細節報告，可多輪追問、複製對話或下載 PDF；不保存（0.9.68）
- 日誌與例外處理強化（0.9.77–0.9.79）：
  - 例外不再漏記、記錯與重複（帳號補齊、程序層級與啟動失敗補登、浮層錯誤邊界、查詢關鍵字不落日誌）
  - 8 碼錯誤追蹤碼：錯誤提示、`/Error` 頁、API 回應、日誌檔與例外紀錄共用同一碼
  - 例外 Email 告警（新種類、Critical、暴增；含節流，出貨預設停用）
  - 例外紀錄與稽核紀錄自動保存期限（預設 90／365 天，每日清理；0.9.96 起改由排程作業執行，Token 用量也納入，預設 365 天）
  - 瀏覽器端 JavaScript 錯誤回報、慢操作 Warning（HTTP、資料庫指令、寄信、AI 呼叫）
- 稽核紀錄：登入、使用者／角色／權限異動等事件寫入 `AuditLog`，管理員可於 `/audit-logs` 查詢、匯出與清除；
  清除與清空動作本身也會留下紀錄（0.9.42）；0.9.78 起補齊分類／團隊／專案增刪改、專案附件、登出、Google SSO、
  自行變更密碼、頁面權限拒絕、匯出與自動清理；0.9.117 起每筆記錄來源 IP（清單、明細與 CSV 顯示，關鍵字可搜），0.9.123 起可依來源 IP 篩選
- Token 用量與費用：每次 LLM 呼叫記錄 token 與花費（USD／TWD），可依使用者／作業／模型／型別彙總，支援 CSV 與 PDF 匯出（0.9.14、0.9.17）
- AI 對話紀錄：保存每次 AI 呼叫實際送出的 Prompt 與取得的 Response（管理員專屬、預設保留 90 天自動過期），五種檢視、JSON／PDF 匯出，並與 Token 用量以呼叫識別碼互相連結（0.9.72）
- 排程作業：`/scheduled-jobs`，以 cron 排程的背景工作框架（0.9.96 起；現內建 8 個作業：6 個清理作業、系統備份、密碼到期提醒），可看下次執行與上次結果、立即執行、停用；啟動時補跑錯過的時段，IIS 重疊回收也只跑一次（0.9.96）；已刪除的專案、分類、團隊、使用者、角色預設 90 天後自動永久刪除（0.9.97）
- 系統備份：`/backups`，每天 02:00 自動備份資料庫（一致快照）、附件、例外堆疊檔、Token 原始檔與金鑰環成 zip，保留 7 份；管理員可立即備份與下載；停站後以 `scripts/Restore-Backup.ps1` 還原（0.9.99）
- 站內通知與公告：右上角鈴鐺保存通知並即時更新，排程作業失敗會通知管理員與觸發者（同時寄信）；程式以 `INotificationSender` 發通知；管理員在 `/announcements` 對全體或角色／團隊發公告，每頁上方顯示、各自關閉（0.9.100）；
  0.9.125 起鈴鐺每則可刪除、可「清除已讀」，已讀的通知預設 7 天後自動清除（`NotificationSettings:ReadRetentionDays`）
- 密碼原則與登入鎖定：密碼長度、字元種類、不可重複最近幾組、有效天數與鎖定次數／分鐘都在系統參數調整；「下次登入須變更密碼」旗標；管理員可解鎖、鎖定時通知管理員；登入失敗訊息不透露帳號是否存在（0.9.101）
- 個人資料：`/Profile` 看自己的角色、團隊、密碼狀態與最近 20 筆登入紀錄，自己修改姓名；右上角改為姓名縮寫（0.9.102）；0.9.123 起登入紀錄與首頁「上一次登入」顯示來源 IP
- 工作階段失效：改密碼、停用、刪除、改角色或管理員身分、管理員「強制登出」都讓那個人所有已登入的瀏覽器與 API refresh 失效；自己改密碼的這台保持登入；Bearer 只接受 access token（0.9.103）
- 兩步驟驗證：使用者以驗證器 App 掃 QR 開啟（TOTP），10 組備用碼、記住這台裝置、同一組驗證碼只能用一次；角色或系統參數可強制使用；Google 與 Web API 登入也要第二步；管理員可重設（0.9.104）
- 部門樹：團隊可設定上層部門，上層看得到所有下屬部門的資料；團隊清單以樹狀顯示；改名自動更新專案、分類與角色預設團隊；Web API 與畫面套用同一套團隊過濾，非管理員只能指派自己範圍內的團隊（0.9.105）
- 首頁儀表板：首頁改為精簡歡迎列＋快速入口＋小工具區（我的通知、我的帳號、排程作業〔管理員〕）；小工具依權限過濾、各自有錯誤邊界，新增一個只要寫元件並登記一行（0.9.106）
- 通用匯出：專案、分類、團隊、使用者頁「匯出 Excel」，依目前條件與團隊範圍、上限 10000 筆、寫稽核；診斷頁的 CSV 匯出收斂到同一個服務（0.9.107）
- AI 提示詞：管理員在畫面上修改日誌分析與例外分析的提示詞，存成版本、可切回；防注入與 PDF 格式規則固定附加、改不到（0.9.108）
- AI 用量上限：全系統與每人的每日、每月新台幣上限（系統參數），達到時不再送出 AI 呼叫，80%／100% 通知本人或管理員；健康檢測記帳不擋（0.9.109）
- CRUD 產生器一行到位：`New-CrudModule.ps1` 直接產生實體、Web API、頁面（軟刪除、並行、稽核、匯出、說明、測試，可選團隊範圍或管理員專屬）並完成所有登記與 migration；`Test-CrudGenerator.ps1` 驗證；畫面依權限顯示按鈕改用可測的 `<RequirePermission>`（0.9.110）
- 清單點狀態標籤切換：團隊、分類、使用者清單的「啟用／停用」標籤可直接點（停用先確認；沒有修改權限或已刪除時不可點；support 與自己不可停用；0.9.120）
- 系統參數：`/system-parameters`，管理員在畫面上修改系統名稱、保留天數、監控與告警門檻、流量與忘記密碼限制，存檔即生效、不需重啟，每次修改寫稽核（0.9.98）
- 全站視覺系統：粉梅暖雪色票收斂為 `wwwroot/theme.css` 單一來源，浮層果凍化、深梅側邊欄、共用狀態徽章（0.9.25–0.9.31）
- API 安全基礎設施：依呼叫端分割的速率限制、安全回應標頭、上傳副檔名白名單（0.4.35）
- 分散式快取：`ICacheService` 統一抽象，透過 `appsettings.json` 在 Memory ↔ Redis 間切換（側邊選單已套用）
- Production 啟動安全檢查：JWT key、預設密碼、Swagger 暴露策略、Redis 連線字串、寄信設定（不可用 Pickup、Smtp 須填齊）
  與 AI 設定（填了金鑰時 `Model`、Azure 的 `Endpoint` 須齊）需明確設定；0.9.86 起錯誤訊息會說明「未設定環境時預設即為 Production（例如 IIS）」，
  並帶出 `PublicBaseUrl` 實際讀到的值
- Sidebar 導覽：JSON 定義、可收合、自動套用使用者角色權限；0.9.112 起寬螢幕預設展開並記住該瀏覽器上次的選擇（窄螢幕從收合開始）
- 頁面操作說明：每個登入後頁面的頂欄頁名旁都有說明鈕，開啟近滿版的「〈頁名〉・操作說明」窗（固定七段、相關頁面依權限過濾）；
  內容在 `Datas/HelpTopics.json` ＋ `Datas/Help/*.md`，由 `PageHelpCatalogTests` 守門（0.9.66–0.9.67）；0.9.121 起說明窗可切換字體「小／中／大」
- 右上角使用者選單「關於」對話窗：顯示系統名稱／描述／版本、執行環境、啟動與已運作時間（0.4.45 起不再顯示 .NET 版本）
- 多語系：以瀏覽器 `Accept-Language` 自動切換，AntDesign 元件本地化
- 全站請求耗時 / 例外統一寫入 NLog
- 靜態資源外部對應（`/UploadFiles` → 實體下載目錄；**0.4.35 起需登入**才可取用）

---

## 5. 快速開始

> 需求：Windows 10 / 11、.NET 10 SDK、IDE（Visual Studio 2026 / Rider / VS Code）。

```powershell
# 1. 還原與編譯
cd src/CustomerHelpAgent
dotnet restore
dotnet build

# 2. 第一次跑（會自動建立目錄、套用 Migration、Seed 預設角色與帳號）
dotnet run --project CustomerHelpAgent.Web/CustomerHelpAgent.Web.csproj
```

預設帳號 / 密碼由 `appsettings.json` 之 `BootstrapSettings`（`SupportAccount` / `SupportPassword`）定義，首次啟動會自動建立 `support` 管理員帳號；`MagicObjectHelper.開發者帳號` 則用於後續識別並保護該開發者帳號。

外部目錄與資料庫檔位置由 `appsettings.json` 之 `SystemSettings.ExternalFileSystem` 控制，預設位於 `C:\temp\CustomerHelpAgent\…`，啟動時若不存在會自動建立。

關於 EF Core Migration 指令請見 [docs/guides/EFCore.md](docs/guides/EFCore.md)。

> **第一次接觸本專案，或要用本腳手架開新系統**，請改讀 [VS Code 開發環境與新專案上手指南](docs/guides/VS%20Code%20開發環境與新專案上手指南.md) —— 涵蓋必備工具、VS Code 啟動與偵錯、User Secrets 機密設定、複製後的完整更名步驟與驗證清單。

---

## 6. 專案結構

```
.
├── readme.md                       ← 本檔（系統入口說明）
├── AGENTS.md / CLAUDE.md           ← LLM 協作行為準則與專案速查入口
├── .editorconfig                   ← 程式碼格式規則（提交前以 dotnet format --verify-no-changes 檢查）
├── global.json                     ← 鎖定 .NET SDK 版本
├── .vscode/                        ← VS Code 啟動／任務（提交前四道關卡）／編輯器設定與建議擴充套件
├── docs/                           ← 系統設計與規範文件（依特性分類，見第 9 節）
│   ├── README.md                   ← 文件目錄索引與分類規則
│   ├── architecture/               ← 架構、資料模型、API/DTO 規範、開發慣例速查
│   ├── security/                   ← 認證、授權、密碼與機密金鑰
│   ├── features/                   ← 個別功能機制（腳手架總覽、快取、上傳、健康監控、AI 分析）
│   ├── guides/                     ← 開發/操作教學（VS Code 上手、CRUD、EFCore、測試）
│   ├── operations/                 ← 維護、部署、設定檔、品質檢查（含 CI 參考設計）
│   ├── prd/                        ← 產品需求文件（能力覆蓋矩陣 + 各能力 PRD）
│   └── changelog/                  ← 變更紀錄
├── scripts/                        ← Invoke-QualityGate.ps1 / Install-GitHooks.ps1 / New-StarterProject.ps1 / New-CrudModule.ps1（樣板在 crud-templates/）/ Test-CrudGenerator.ps1 / Restore-Backup.ps1 / Test-DocsEncoding.ps1
└── src/CustomerHelpAgent/
    ├── CustomerHelpAgent.slnx              ← 方案檔（新版 .slnx 格式）
    ├── Directory.Build.props       ← 共用建置屬性（Nullable、TreatWarningsAsErrors）
    ├── Directory.Packages.props    ← 套件版本單一來源（Central Package Management）
    ├── CustomerHelpAgent.Web/              ← Blazor Server 宿主
    │   ├── Components/             ← Pages / Views / Layout / Auths / Commons
    │   ├── Controllers/            ← Web API（Project / Category / Team / Auth …）
    │   ├── Extensions/             ← ★ 服務與中介軟體註冊（AddApplicationServices 等）
    │   ├── Configuration/          ← 強型別設定（CacheSettings、RateLimitSettings …）與 StartupSafetyValidator
    │   ├── Auth/                   ← JwtTokenService、RecordAccessScopeProvider、CookieSettings
    │   ├── Ai/                     ← LLM 日誌／例外分析、安全 Markdown 渲染、PDF 報告、用量與計價
    │   ├── Backup/                 ← 系統備份（VACUUM INTO 快照＋檔案打包成 zip）
    │   ├── Caching/                ← ICacheService（Memory ↔ Redis）
    │   ├── Dashboard/              ← 首頁儀表板小工具登記
    │   ├── Diagnostics/            ← 例外紀錄管線、日誌查詢、錯誤追蹤碼、告警、保存期限、慢操作、瀏覽器錯誤回報
    │   ├── Email/                  ← 寄信實作（None／Pickup／Smtp）、背景寄信佇列
    │   ├── Export/                 ← 通用匯出（TabularExport、業務頁 Excel）
    │   ├── Fonts/                  ← PDF 內嵌中文字型（Noto Sans TC）與授權
    │   ├── Health/                 ← 健康檢查與計分
    │   ├── Properties/             ← launchSettings.json、發行設定檔
    │   ├── Scheduling/             ← 排程作業框架與內建作業（Jobs/）
    │   ├── wwwroot/                ← 靜態資源
    │   ├── nlog.config
    │   ├── Localization/           ← AntDesignLocaleFactory
    │   ├── Datas/Menu.json         ← Sidebar 導覽與權限定義
    │   ├── Datas/HelpTopics.json   ← 頁面操作說明索引（路由 → 標題 → 檔名）
    │   ├── Datas/Help/             ← 各頁操作說明內容（*.md，UTF-8 含 BOM）
    │   ├── Filters/                ← ApiValidationFilter 等
    │   ├── Program.cs              ← 啟動主程式
    │   └── appsettings.json        ← 系統設定（含 SystemVersion）
    ├── CustomerHelpAgent.Business/
    │   ├── Services/DataAccess/    ← Domain Service（CRUD）
    │   ├── Services/Other/         ← AuthenticationStateHelper 等
    │   ├── Repositories/           ← API 層使用的 Repository
    │   └── Models/AutoMapping.cs   ← AutoMapper Profile
    ├── CustomerHelpAgent.AccessDatas/
    │   ├── BackendDBContext.cs
    │   ├── Models/                 ← Entity（MyUser、Project、Category、Team…）
    │   └── Migrations/
    ├── CustomerHelpAgent.Models/           ← AdapterModel、Systems、AutoMapper 來源
    ├── CustomerHelpAgent.Dtos/             ← API DTO（含 ApiResult/PagedResult）
    ├── CustomerHelpAgent.Share/            ← Helpers、Extensions
    └── CustomerHelpAgent.Tests/            ← xUnit 測試（含慣例守門測試；Components/ 為 bUnit 元件測試）
```

---

## 7. 設定檔說明（`appsettings.json`）

| 區段 | 用途 |
|------|------|
| `AllowedHosts` | 允許的 Host 標頭白名單，預設 `*`。 |
| `Logging` | .NET 預設記錄等級設定（NLog 啟動後會接管實際輸出）。 |
| `Swagger.EnabledInProduction` | 是否在 Production 環境暴露 Swagger UI，預設 `false`。 |
| `Security.ReturnExceptionDetails` | 是否於 `ApiResult.Exception` 回傳例外細節；`null` 時依環境決定。 |
| `Cors.AllowedOrigins` | CORS 允許來源白名單（陣列）；留空表示不額外開放跨來源。 |
| `RateLimit.ApiRequestsPerMinute` | 一般 API 每分鐘配額，**每個呼叫端各自計算**，預設 `120`。 |
| `RateLimit.LoginRequestsPerMinute` | 登入端點每分鐘配額，預設 `10`（比一般 API 嚴格）。 |
| `ForwardedHeaders.KnownProxies` / `KnownNetworks` | **預設未寫入 `appsettings.json`**（程式面支援，未設定時採空白預設）。信任的反向代理 IP／網段；走代理時必設，否則來源 IP 會全部變成代理伺服器且 `X-Forwarded-For` 可被偽造。 |
| `CacheSettings.Provider` | 快取 provider，支援 `Memory` 與 `Redis`，預設 `Memory`（見 [分散式快取機制](docs/features/分散式快取機制.md)）。 |
| `CacheSettings.RedisConnection` | `Provider=Redis` 時的 Redis 連線字串；Production 使用 Redis 時必須設定。 |
| `CacheSettings.InstanceName` | Redis 快取鍵前綴，預設 `CustomerHelpAgent:`。 |
| `CacheSettings.DefaultExpirationMinutes` | 快取項目預設存活時間（分鐘），預設 `30`。 |
| `EmailSettings` | 寄信服務（0.9.59 起）：`Provider`（`None` 預設／`Pickup` 開發用／`Smtp`）、`Host`、`Port`、`Security`、`UserName`、`Password`、`FromAddress`、`FromName`、`PickupDirectory`、`PublicBaseUrl`、`TimeoutSeconds`。⚠️ `Password` 一律留空、走 User Secrets 或環境變數；Production 不可用 `Pickup`，用 `Smtp` 時須填 `Host`／`FromAddress`／`PublicBaseUrl`。 |
| `PasswordResetSettings` | 忘記密碼（0.9.60 起）：`TokenLifetimeMinutes`（重設連結效期，預設 30 分）、`RequestCooldownSeconds`（同帳號申請間隔，預設 60 秒）。只在寄信啟用時有作用。 |
| `NLog.BasePath` | NLog 寫入的根目錄；專案會在其下建立 `CustomerHelpAgent.Web` 子目錄並輸出檔案日誌。 |
| `JwtSettings` | Web API JWT 設定：`Issuer`、`Audience`、`SigningKey`、`AccessTokenMinutes`、`RefreshTokenDays`、`ClockSkewMinutes`；Production 啟動時若仍為開發用 `SigningKey` 會中止啟動。 |
| `CookieSettings` | Blazor UI 登入 Cookie 效期（0.9.39 起）：`ExpireMinutes`（未勾「記住我」，預設 480 分）、`RememberMeDays`（勾了「記住我」，預設 30 天）、`SlidingExpiration`（預設 `true`）、`ValidationIntervalMinutes`（工作階段失效的重新檢查間隔，預設 5 分，0.9.103 起）。超出範圍啟動失敗。見 [日誌與設定檔說明 §4.3.2.1](docs/operations/日誌與設定檔說明.md)。 |
| `BootstrapSettings` | 預設 `support` 帳號種子設定：`SupportAccount` / `SupportName` / `SupportEmail` / `SupportPassword`（首次啟動建立，重啟時更新密碼）。 |
| `GoogleOAuthSettings` | Google OAuth2 第三方登入：`Enabled`、`ClientId`、`ClientSecret`、`DefaultRoleName`（見 [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md)）。 |
| `SystemSettings.ConnectionStrings.SQLiteDefaultConnection` | SQLite 連線範本；實際連線字串由 `MagicObjectHelper.GetSQLiteConnectionString` 結合 `DatabasePath` 產生。 |
| `SystemSettings.SystemInformation.SystemName` | 顯示用系統名稱。 |
| `SystemSettings.SystemInformation.SystemDescription` | 顯示用系統描述。 |
| **`SystemSettings.SystemInformation.SystemVersion`** | **唯一版本來源**，每次完成異動必須遞增（見第 8 節）。 |
| `SystemSettings.ExternalFileSystem.DatabasePath` | SQLite 資料庫檔放置目錄。 |
| `SystemSettings.ExternalFileSystem.DownloadPath` | `/UploadFiles` 對應的實體目錄（靜態資源外掛）。 |
| `SystemSettings.ExternalFileSystem.UploadPath` | 通用上傳暫存目錄。 |
| `SystemSettings.ExternalFileSystem.ProjectFilePath` | 專案附件根目錄（再依年/月細分）。 |
| `SystemSettings.ExternalFileSystem.ExceptionPath` | 系統例外紀錄的堆疊追蹤檔放置目錄（0.9.11 起）。 |
| `SystemSettings.ExternalFileSystem.TokenUsagePath` | LLM 原始 usage JSON 放置目錄（0.9.14 起）。 |
| `SystemSettings.ExternalFileSystem.AiCallLogPath` | AI 對話紀錄的內容檔目錄（完整 Prompt／Response，0.9.72 起）。⚠️ 內容含日誌與例外堆疊，**絕不可放在 `DownloadPath` 底下**。 |
| `SystemSettings.ExternalFileSystem.BackupPath` | 系統備份 zip 放置目錄（0.9.99 起）。 |
| `SystemSettings.ExternalFileSystem.DataProtectionKeyPath` | ASP.NET Core Data Protection 金鑰環目錄（0.9.39 起）。登入 Cookie 以此加密，⚠️ 金鑰換掉全站立刻被登出；多機部署須共用此目錄。 |
| `SystemSettings.Upload.AllowedExtensions` | **預設未寫入 `appsettings.json`**。允許上傳的副檔名白名單（陣列）；留空採用 `UploadFileTypePolicy` 內建預設（不含 `.html`/`.svg`/`.exe` 等）。 |
| `AiSettings` | 日誌 AI 分析：`Provider`（`AzureOpenAI` / `OpenAI`）、`Endpoint`、`ApiKey`、`Model` 與 `TimeoutSeconds`（預設 600 秒）；其餘欄位刻意不寫進範本、走程式預設，完整預設值表見 [AI 日誌分析 §2.1](docs/features/AI日誌分析.md)。Azure 走 v1 API，`Endpoint` 直接貼入口網站的「Azure OpenAI 端點」、`Model` 填部署名稱；OpenAI 則 `Endpoint` 留空、`Model` 填模型 id。沒有獨立的啟用開關，**有沒有填 `ApiKey` 就是開關**。⚠️ `ApiKey` 在 `appsettings.json` 一律留空，實際值走 User Secrets 或環境變數。 |
| `AiPricingSettings` | LLM 費率表與匯率（0.9.17 起）。費用是**呼叫當下的快照**，改這裡不會回頭修正既有紀錄；模型找不到費率會記成「未定價」（不是 0）。費率表與模型比對規則見 [日誌與設定檔說明 §4.9](docs/operations/日誌與設定檔說明.md)。 |
| `AiCallLogSettings` | AI 對話紀錄：`Enabled`（預設 `true`）與 `RetentionDays`（預設 90，範圍 1～3650，超出啟動失敗）。停用只代表不再記，舊紀錄照樣過期。見 [日誌與設定檔說明 §4.10](docs/operations/日誌與設定檔說明.md)。 |
| `LogRetentionSettings` | 自動保存期限（0.9.78 起）：`ExceptionLogDays`（系統例外紀錄，預設 90）、`AuditLogDays`（稽核紀錄，預設 365）、`TokenUsageLogDays`（Token 用量，預設 365，0.9.96 起），範圍 0～36500，`0`＝不自動清理；0.9.96 起由排程作業清理（預設每日 03:00）。見 [日誌與設定檔說明 §4.11](docs/operations/日誌與設定檔說明.md)。 |
| `ExceptionAlertSettings` | 系統例外 Email 告警（0.9.78 起）：`Recipients`（預設空清單＝停用）、`BurstThreshold`／`BurstWindowMinutes`（暴增門檻，預設 20 次／10 分）、`PerSignatureCooldownMinutes`（預設 60）、`MaxEmailsPerHour`（預設 20）。需寄信已啟用。見 [日誌與設定檔說明 §4.12](docs/operations/日誌與設定檔說明.md)。 |
| `SlowOperationSettings` | 慢操作門檻（0.9.79 起，毫秒，超過記 Warning，`0`＝停用）：`HttpRequestMs`（3000）、`DbCommandMs`（1000）、`ExternalCallMs`（寄信，10000）、`AiCallMs`（60000）。見 [日誌與設定檔說明 §4.13](docs/operations/日誌與設定檔說明.md)。 |
| `ClientErrorReporting` | 瀏覽器端 JavaScript 錯誤回報（0.9.79 起）：`Enabled`（預設 `true`）、`MaxPerCircuitPerMinute`（預設 10，範圍 1～1000）。見 [日誌與設定檔說明 §4.14](docs/operations/日誌與設定檔說明.md)。 |
| `AutoMapper:LicenseKey` | AutoMapper 商業授權金鑰（可留空）。 |

> 本表是摘要。**完整且具權威性的逐鍵說明在 [docs/operations/日誌與設定檔說明.md](docs/operations/日誌與設定檔說明.md) §4**
> —— 新增 `appsettings.json` 區段時請以那份為主、本表為輔（見 [維護規範](docs/operations/維護規範.md) §2）。

---

## 8. 版本管理與維護規範（**重要**）

本專案採用單一版本來源策略。請務必遵守下列規範：

1. **每次更新完成必須 bump 版本**：
   每次異動一律將 `appsettings.json` 之 `SystemSettings.SystemInformation.SystemVersion` 的**最後一碼（Patch）+1**（例：`0.4.0 → 0.4.1`，不進位、不依異動性質區分），並把括號內日期更新為當天。

   ```json
   "SystemVersion": "0.4.1 (2026/06/22)"
   ```

   `SystemVersion` 為任何提交都應 bump 的最小單位。版號顯示格式為 `Major.Minor.Patch (YYYY/MM/DD)`；遞增規則詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md) 第 1.2 節。

2. **影響到既有文件就必須同步更新**：
   只要本次異動會改變既有功能、模組、API、資料表、設定欄位或 UI 行為，必須同步修訂相關文件。對應表詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md)。

3. **檔案編碼**：
   `docs/` 下與根目錄的所有 `.md` 一律使用 **UTF-8 含 BOM**（提交前在本機以 [`scripts/Test-DocsEncoding.ps1`](scripts/Test-DocsEncoding.ps1) 遞迴檢查，缺 BOM 或含亂碼即失敗）；其餘原始碼、設定檔採 UTF-8 即可。提交前需自行確認繁體中文無亂碼。

4. **撰寫文件時請對應實際 codebase**：
   引用程式檔請使用 `相對路徑:行號` 格式，避免假設、推測或外部連結失效。

5. **提交前跑完品質關卡**（專案**沒有 CI**，2026-09-20 已移除 GitHub Actions 工作流程，這是唯一的關卡）：
   一行指令依序跑 restore、Release 建置（0 warning）、format、約 1600 個測試、文件編碼、弱點掃描，遇到失敗就停；
   VS Code 可跑 `.vscode/tasks.json` 的 `quality-gate` 任務。想在每次 push 前自動執行，可選用 `pwsh ./scripts/Install-GitHooks.ps1`。

   ```powershell
   pwsh ./scripts/Invoke-QualityGate.ps1          # -Quick 略過弱點掃描
   ```

   衍生專案若要建立 CI，可依 [CI-CD 與品質檢查](docs/operations/CI-CD與品質檢查.md) 的參考設計建立。

---

## 9. 文件索引

完整分類規則與清單見 [docs/README.md](docs/README.md)；新增文件請先依特性歸入既有子目錄，無適用分類時自動新增英文小寫目錄並同步該索引與本節。

### 架構與設計（architecture）

- [開發慣例與限制速查](docs/architecture/開發慣例與限制速查.md) — **AI/開發者必讀**：分層、SQLite migration、DbContextFactory（0.4.36 起取代追蹤清理）、權限同步等不變量速查。
- [架構總覽](docs/architecture/架構總覽.md) — 6 個專案分層、依賴方向、啟動流程、DI 註冊清單。
- [資料模型與資料庫](docs/architecture/資料模型與資料庫.md) — `BackendDBContext`、主要 Entity、關聯與刪除政策。
- [DTO 與模型邊界規範](docs/architecture/DTO%20與模型邊界規範.md) — API / UI / Business / Entity 資料邊界原則與新 CRUD 模組待辦。
- [Web API 設計慣例](docs/architecture/Web%20API%20設計慣例.md) — Controller 樣板、`ApiResult<T>`、`PagedResult<T>`、Search DTO。
- [Web API 端點目錄](docs/architecture/Web%20API%20端點目錄.md) — 全部 controller 的實際路由、授權與回傳型別對照表。
- [對話窗 UI 設計規範](docs/architecture/對話窗%20UI%20設計規範.md) — 表單窗的滿版 2 欄版型與未儲存保護、小型確認窗、通知與消息條的果凍視覺。
- [介面視覺設計規範](docs/architecture/介面視覺設計規範.md) — 常駐介面的分層強度、全站色票單一來源、樣式歸屬判準、側邊欄與表格規格、狀態徽章。
- [API Versioning 策略](docs/architecture/API%20Versioning%20策略.md) — `/api/...` 與 `/api/v1/...` 平行路由、Swagger v1 分組與後續導入策略。

### 認證與安全（security）

- [認證授權與權限機制](docs/security/認證授權與權限機制.md) — Cookie / JWT 雙 scheme、Claims、RBAC 關聯表與角色／團隊指派、動作級授權、`<RequirePermission>` 與稽核事件。
- [密碼種類與儲存機制](docs/security/密碼種類與儲存機制.md) — 密碼種類盤點、`MyUser.Password` 雜湊、種子密碼與機密金鑰。
- [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md) — Google SSO 設定、自動建帳與審核、串接權控（0.9.63 起不支援 API／JWT）。
- [記住我登入原理說明](docs/security/記住我登入原理說明.md) — Cookie + RememberMe 完整原理。

### 功能機制（features）

- [腳手架總覽與複刻上手](docs/features/腳手架總覽與複刻上手.md) — ⭐ 新手入口：為什麼需要腳手架、已內建的企業共通功能、vibe coding 提示詞、複刻步驟與後續待辦（0.9.82）；0.9.116 起附章節目錄、25 題 FAQ（含用 Claude Code／Codex 開發的注意事項）與名詞解釋附錄。
- [分散式快取機制](docs/features/分散式快取機制.md) — `ICacheService`、Memory ↔ Redis 切換、選單快取與失效行為。
- [檔案上傳機制](docs/features/檔案上傳機制.md) — 專案附件、年月目錄、刪除同步、1GB 上限與副檔名白名單（0.4.35）。
- [系統健康監控](docs/features/系統健康監控.md) — 健康百分比、紅黃綠燈號、部署探針與最近 24 小時 WARN 以上日誌。
- [AI 日誌分析](docs/features/AI日誌分析.md) — 日誌送 Azure OpenAI／OpenAI 整理、Markdown 安全渲染管線、PDF 報告與內嵌中文字型（0.9.4）。
- [AI 例外分析](docs/features/AI例外分析.md) — 例外明細送 AI 產生分析報告、多輪追問、複製對話與 PDF 下載（0.9.68）。
- [售後 Agent 示範系統](docs/features/售後Agent示範系統.md) — Microsoft Agent Framework 示範；M0 計費接軌：Agent 每次模型呼叫都查額度、記 Token 用量與 AI 對話紀錄（1.0.2）；M1 模擬鞋服電商與傳統售後流程（顧客 `/shop`、客服審核、規則版本，1.0.3）。

### 開發與操作指南（guides）

- [VS Code 開發環境與新專案上手指南](docs/guides/VS%20Code%20開發環境與新專案上手指南.md) — **新手入口**：必備工具、VS Code 啟動與偵錯、User Secrets 機密設定、品牌客製化（網頁圖示／品牌圖片／產品名稱與說明）、複製腳手架的完整更名步驟、驗證清單與疑難排解。
- [建立一個新 CRUD 操作網頁說明](docs/guides/建立一個新%20CRUD%20操作網頁說明.md) — `scripts/New-CrudModule.ps1` 一行產生並登記新模組（0.9.110 起）與產生後的檢查清單
- [腳手架開發指引](docs/guides/腳手架開發指引.md) — **總指引兼技術與參數百科**：16 項技術各自在解決什麼問題與怎麼調、設定檔逐鍵、沒寫在設定檔裡的旋鈕、**權控機制白話導讀（§6）與權限控管技術細節（§7）**、換色與換主題、還缺什麼與還能加什麼、部署維運、AI 協作約束。
- [畫面與欄位字典](docs/guides/畫面與欄位字典.md) — 每一個畫面與每一個欄位的意思、用途與陷阱。
- [腳手架新專案啟動流程](docs/guides/腳手架新專案啟動流程.md) — 從本腳手架複製成新系統的改名與設定檢查清單（操作細節見上方上手指南）。
- [EFCore 指令備忘](docs/guides/EFCore.md) — Migration 指令範本。
- [測試指南](docs/guides/測試指南.md) — 測試類別、本機執行、整合測試與覆蓋率。
- `scripts/New-StarterProject.ps1` — 從本腳手架複製新專案並替換 namespace / project 名稱；**整個方案執行一次即可**，`.slnx` 內 7 個專案會一起改名；0.9.116 起開跑前會確認腳手架來源是最新的（工作目錄乾淨、在 `main`、不落後 origin），否則中止（`-AllowUnsyncedSource` 可放行）。
- `scripts/New-CrudModule.ps1` — 一行產生新 CRUD 模組（13 個樣板檔，含操作說明初稿與服務測試，對齊現行慣例），並自動完成所有登記與 migration（0.9.110 起）；改了樣板跑 `scripts/Test-CrudGenerator.ps1` 驗證。

### 維運與部署（operations）

- [維護規範](docs/operations/維護規範.md) — 版本 bump、文件同步、commit 前自我檢查清單。
- [正式部署與安全檢查清單](docs/operations/正式部署與安全檢查清單.md) — 上線前 JWT、預設帳號、Swagger、例外揭露等必查項目。
- [日誌與設定檔說明](docs/operations/日誌與設定檔說明.md) — NLog 配置、各層級用法、`appsettings.json` 全表。
- [備份與還原操作手冊](docs/operations/備份與還原操作手冊.md) — 系統備份的範圍與理由、下載、停站後以 `scripts/Restore-Backup.ps1` 還原。
- [Gmail 寄信設定指南](docs/operations/Gmail寄信設定指南.md) — 用 Gmail 帳號寄信：應用程式密碼、`EmailSettings` 逐值設定與疑難排解。
- [AI 模型計費更新指南](docs/operations/AI模型計費更新指南.md) — 依 OpenAI 官方頁面更新 `AiPricingSettings` 的逐步流程（調價、新模型、Deprecated）。
- [CI-CD 與品質檢查](docs/operations/CI-CD與品質檢查.md) — 提交前本機四道關卡、文件編碼檢查、弱點掃描；並保留已移除之 GitHub Actions 流程作為衍生專案自建 CI 的參考設計。

### 產品需求文件（prd）

- [PRD 主控台（能力覆蓋矩陣）](docs/prd/README.md) — 以產品能力為單位的單一入口：能力→入口→程式來源→狀態。
- 各能力 PRD 一律由上方主控台的**能力覆蓋矩陣**索引，避免此處與 `docs/prd/README.md` 長期不同步。

### 變更紀錄（changelog）

- [docs/changelog/](docs/changelog/README.md) — 全部改版紀錄的單一索引（依版本新→舊）。
  此處刻意不重複列舉，避免三處索引長期不同步。

## 10. 參考連結

- AntDesign Blazor — https://antblazor.com/
- BlazorMaterialIcons — https://github.com/dimohy/BlazorMaterialIcons
- Google Material Icons — https://fonts.google.com/icons
- NLog — https://nlog-project.org/
- AutoMapper — https://automapper.org/

---

## 11. 授權與貢獻

本專案為內部樣板，請依團隊約定條款使用。提交異動前請確認已遵守第 8 節「版本管理與維護規範」全部要求。

內含的第三方資產：`src/CustomerHelpAgent/CustomerHelpAgent.Web/Fonts/NotoSansTC-Regular.ttf` 為 Google Noto Sans TC，
以 [SIL Open Font License 1.1](src/CustomerHelpAgent/CustomerHelpAgent.Web/Fonts/OFL.txt) 釋出，明確允許內嵌與重新發行。
字型的來源與產生方式見 [`Fonts/README.md`](src/CustomerHelpAgent/CustomerHelpAgent.Web/Fonts/README.md)。
