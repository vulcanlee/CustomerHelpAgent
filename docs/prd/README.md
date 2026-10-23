# prd — 產品需求文件主控台

- 文件版本：2.26
- 文件狀態：維護中
- 現行系統版本：1.0.0
- 首次實作版本：0.4.23
- 最後核對日期：2026/10/23

本目錄是產品需求的單一入口。PRD 以**產品能力**為單位；「已實作／部分實作」描述程式現況，「規劃中」必須獨立分區，不代表系統已提供。本專案為通用 Blazor 腳手架，**內建 LLM 日誌分析、AI 例外分析（0.9.68 起，系統例外紀錄明細窗，含針對單筆例外的多輪追問）、Token 用量／費用計價與 AI 對話紀錄（0.9.72 起，管理員專屬、保存完整 Prompt／Response）**（見「日誌檢視」「系統例外紀錄」「Token 用量」「LLM 呼叫費用估算」「AI 對話紀錄」PRD，機制見 [AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md)），但**不含 RAG／向量檢索／通用對話機器人**；PRD 內容一律以程式碼、`Menu.json` 與測試為準。

## 一、能力覆蓋矩陣

| 產品能力 | PRD | 入口／路由 | 主要程式來源 | 狀態 | 核對版本 |
|----------|-----|-----------|--------------|------|----------|
| 首頁與導覽 | [首頁與導覽](首頁與導覽-prd.md) | `/`、`/App` | `Pages/Home.razor`、`Pages/HomeAuthed.razor`、`SidebarMenuService`、`Menu.json`、`MainLayout`（含「關於」對話窗）、`PageHelpDialog`／`PageHelpService`／`Datas/HelpTopics.json`＋`Datas/Help/*.md`（頂欄「操作說明」對話窗，每個登入後頁面一份） | 已實作（0.9.66 起含頁面操作說明對話窗；0.9.121 起說明窗可切換字體小／中／大）| 0.9.135 |
| 登入與帳號流程 | [登入與帳號流程](登入與帳號流程-prd.md) | `/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/ChangePassword` | `Components/Auths/*`、`MyUserServiceLogin`、`ExternalLoginService`、`PasswordResetService`、`AuthController` | 已實作（0.9.60 起含忘記密碼）| 0.9.136 |
| 專案項目 | [專案項目](專案項目-prd.md) | `/projects` | `Pages/Projects/ProjectPage.razor`、`ProjectService`、`ProjectController`、`ProjectFileController` | 已實作（0.9.78 起增刪改與附件上傳／刪除寫稽核）| 0.9.136 |
| 使用者管理 | [使用者管理](使用者管理-prd.md) | `/myusers` | `Pages/Admins/MyUserPage.razor`、`MyUserService` | 已實作（0.9.120 起點狀態標籤切換啟用／停用）| 0.9.136 |
| 角色管理 | [角色管理](角色管理-prd.md) | `/roleviews` | `Pages/Admins/RoleViewPage.razor`、`RoleViewService`、`RbacWriteService` | 已實作 | 0.9.136 |
| 分類清單 | [分類清單](分類清單-prd.md) | `/categories` | `Pages/Categories/CategoryPage.razor`、`CategoryService`、`CategoryController` | 已實作（0.4.40 起可指定適用團隊；0.9.78 起增刪改寫稽核；0.9.120 起點狀態標籤切換啟用／停用） | 0.9.135 |
| 團隊清單 | [團隊清單](團隊清單-prd.md) | `/teams` | `Pages/Teams/TeamPage.razor`、`TeamService`、`TeamController` | 已實作（0.9.78 起增刪改寫稽核；0.9.120 起點狀態標籤切換啟用／停用）| 0.9.135 |
| 系統健康監控 | [系統健康監控](系統健康監控-prd.md) | `/system-health` | `Pages/SystemHealthPage.razor`、`Health/SystemHealthService` 等 | 已實作（14 項檢查；0.9.59 起含寄信服務項與寄信測試；0.9.79 起含日誌管線項；0.9.96 起含排程作業項；0.9.122 起含站內通知測試）| 0.9.135 |
| 日誌檢視 | [日誌檢視](日誌檢視-prd.md) | `/logs` | `Pages/Analytics/LogViewerPage.razor`、`LogQueryService`、`NLogFilePathResolver`、`AiLogAnalysisService` | 已實作（0.9.69 起可展開收合與複製；0.9.78 起可依錯誤追蹤碼篩選；0.9.111 起有快速區間與「查前後 1 分鐘」）| 0.9.135 |
| 資料庫用量 | [資料庫用量](資料庫用量-prd.md) | `/database-usage` | `Pages/Analytics/DatabaseUsagePage.razor`、`DatabaseUsageService` | 已實作 | 0.9.135 |
| 日誌等級設定 | [日誌等級設定](日誌等級設定-prd.md) | `/log-level-setting` | `Pages/Analytics/LogLevelSettingPage.razor`、`LogLevelRuntimeState` | 已實作 | 0.9.135 |
| 系統例外紀錄 | [系統例外紀錄](系統例外紀錄-prd.md) | `/system-exceptions` | `Pages/Admins/ExceptionLogPage.razor`、`Diagnostics/ExceptionLogProvider`、`ExceptionLogService`、`AiExceptionAnalysisService`、`CrashMarkerStore`、`ProcessExceptionHooks` | 已實作（0.9.77 補齊漏記、重複與帳號；0.9.78 加上錯誤追蹤碼、告警、自動清理）| 0.9.135 |
| Token 用量 | [Token 用量](Token用量-prd.md) | `/token-usage` | `Pages/Analytics/TokenUsagePage.razor`、`ITokenUsageRecorder`、`TokenUsageLogService`、`TokenUsageRawStore` | 已實作（0.9.17 起每列含費用估算；0.9.50 起有「最近 1／7／30 天」摘要卡與每日費用趨勢；0.9.51 起 PDF 可只匯出目前頁籤且含趨勢；0.9.55 起有折線趨勢圖頁籤，PDF 同步輸出）| 0.9.135 |
| AI 對話紀錄 | [AI 對話紀錄](AI對話紀錄-prd.md) | `/ai-call-logs`（`?callId=` 深連結）| `Pages/Analytics/AiCallLogPage.razor`、`IAiCallLogRecorder`、`AiCallLogService`、`AiCallLogFileStore`、`AiCallCapture`、`AiCallLogRetentionJob` | 已實作（0.9.72；0.9.96 起自動過期改由排程作業執行）| 0.9.135 |
| 系統備份與還原 | [系統備份與還原](系統備份與還原-prd.md) | `/backups`、`GET /api/backups/{檔名}/download` | `Pages/Admins/BackupPage.razor`、`Web/Backup/*`、`SystemBackupJob`、`BackupController`、`scripts/Restore-Backup.ps1` | 已實作（0.9.99）| 0.9.135 |
| 資料保留與清理 | [資料保留與清理](資料保留與清理-prd.md) | `/data-retention` | `Pages/Admins/DataRetentionPage.razor`、`Web/DataRetention/*`、`Scheduling/RetentionCutoffs`、各資料服務的 `GetRetentionStatsAsync` | 已實作（0.9.133；0.9.134 起含自訂清除）| 0.9.134 |
| 個人資料 | [個人資料](個人資料-prd.md) | `/Profile`（右上角使用者選單）| `Pages/ProfilePage.razor`、`Views/Profiles/ProfileView`、`ProfileService`、`Layout/UserInitials` | 已實作（0.9.102；0.9.123 起登入紀錄顯示來源 IP）| 0.9.135 |
| AI 用量配額 | [AI 用量配額](AI用量配額-prd.md) | 全系統與每人的每日、每月新台幣上限，送出前擋下、80%／100% 通知 | `AiQuotaService`、`AiChatCompletionClient` | 已實作（0.9.109）| 0.9.136 |
| AI 提示詞管理 | [AI 提示詞管理](AI提示詞管理-prd.md) | 管理員修改 AI 日誌分析與例外分析的提示詞，版本保留可切回；固定規則自動附加 | `PromptTemplateService`、`Web/Ai/AiPromptGuardrails.cs` | 已實作（0.9.108）| 0.9.135 |
| 通用匯出 | [通用匯出](通用匯出-prd.md) | 專案、分類、團隊、使用者頁「匯出 Excel」；診斷頁 CSV | `Web/Export/*` | 已實作（0.9.107）| 0.9.135 |
| 首頁儀表板 | [首頁儀表板](首頁儀表板-prd.md) | `/App` 的小工具區 | `Web/Dashboard/*`、`Views/Dashboard/*`、`HomeWelcomeView` | 已實作（0.9.106；0.9.122 起「我的通知」每 60 秒更新；0.9.123 起「上一次登入」顯示 IP）| 0.9.135 |
| 兩步驟驗證 | [兩步驟驗證](兩步驟驗證-prd.md) | `/TwoFactorSetup`（個人資料頁）、`/Auths/TwoFactor` | `TwoFactorService`、`TwoFactorLoginCookies`、`Views/Profiles/TwoFactorSetupView`、`Auths/TwoFactor` | 已實作（0.9.104）| 0.9.135 |
| 站內通知與公告 | [站內通知與公告](站內通知與公告-prd.md) | 頂欄鈴鐺、頁面上方公告橫幅、`/announcements` | `INotificationSender`、`NotificationSignal`、`Layout/NotificationBell`、`AnnouncementBanner`、`Pages/Admins/AnnouncementPage.razor`、`NotificationRetentionJob` | 已實作（0.9.100；0.9.125 起鈴鐺可刪除單則與清除已讀，已讀通知另依保留天數清除）| 0.9.136 |
| 系統參數 | [系統參數](系統參數-prd.md) | `/system-parameters` | `Pages/Admins/SystemParameterPage.razor`、`Web/Configuration/Parameters/*`、`SystemParameterService`、`SystemIdentity` | 已實作（0.9.98）| 0.9.135 |
| 排程作業 | [排程作業](排程作業-prd.md) | `/scheduled-jobs` | `Pages/Admins/ScheduledJobPage.razor`、`Web/Scheduling/*`、`ScheduledJobRunService`、`CrossProcessFileLock`、`SoftDeletePurgeService` | 已實作（0.9.96；0.9.97 起含已刪除資料清理）| 0.9.135 |
| 紀錄分類與團隊權控 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md) | 跨功能（所有清單查詢／檔案）| `PermissionChecker`、`EffectiveTeamResolver`、`RecordAccessScopeProvider`、`TagStringHelper` | 已實作 | 0.9.135 |
| 稽核紀錄 | [稽核紀錄](稽核紀錄-prd.md) | `/audit-logs` | `Pages/Admins/AuditLogPage.razor`、`AuditLogQueryService`、`AuditLogService`（寫入）| 已實作（0.9.42 補上查詢畫面；0.9.117 起顯示來源 IP，0.9.123 起可依來源 IP 篩選）| 0.9.135 |

## 二、無選單入口的核心能力

| 能力 | PRD 歸屬 | 現況 |
|------|----------|------|
| 動作級授權（`[HasPermission("resource:action")]`）與管理員短路 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[角色管理](角色管理-prd.md) | 已實作，UI 與 API 共用單一 RBAC 權威 |
| 稽核事件的**寫入點**（登入、使用者/角色/權限異動；0.9.78 起擴及分類、團隊、專案與附件、登出、SSO、頁面權限拒絕、匯出與清除，代碼集中在 `AuditActions`；0.9.117 起每筆由 `AuditLogService` 自動記來源 IP）| [稽核紀錄](稽核紀錄-prd.md)、[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[分類清單](分類清單-prd.md)、[團隊清單](團隊清單-prd.md)、[專案項目](專案項目-prd.md) | 已實作；0.9.42 起查詢畫面見 `/audit-logs`，本列只涵蓋散落各流程的寫入點 |
| 帳號安全（PBKDF2、帳號鎖定、忘記密碼、兩步驟驗證）| [登入與帳號流程](登入與帳號流程-prd.md)、[兩步驟驗證](兩步驟驗證-prd.md) | 已實作；忘記密碼需啟用寄信（0.9.60）；兩步驟驗證 0.9.104 起由使用者自選開啟，可依角色或系統參數強制 |
| 檔案上傳（專案附件）| [專案項目](專案項目-prd.md) | 已實作 |
| 寄信服務（None／Pickup／Smtp、背景佇列、健康監控項與測試寄信）| [寄信服務](寄信服務-prd.md) | 已實作（0.9.59）；設定在 `EmailSettings`（Gmail 設定步驟見 [Gmail 寄信設定指南](../operations/Gmail寄信設定指南.md)），畫面在「系統健康監控」|
| 日誌與例外處理（全系統共用規範：等級規則、例外分類、未捕捉例外涵蓋、稽核範圍、保存期限、告警）| [日誌與例外處理](日誌與例外處理-prd.md) | 已實作（0.9.76 立案；0.9.77 完成 P0、0.9.78 完成 P1、0.9.79 完成 P2）；§三、§四 為所有功能的開發與驗收依據 |
| LLM 呼叫費用估算（單價／匯率設定、四種計費單位、單價快照）| [LLM 呼叫費用估算](LLM呼叫費用估算-prd.md) | 已實作（0.9.17）；表現在「Token 用量」頁，維護入口是 `appsettings.json` |

## 三、規劃中產品藍圖

| 藍圖 | 現況界線 |
|------|----------|
| 二階段驗證（TOTP）強制啟用流程 | 0.9.104 起已實作（綁定 UI、登入第二步、依角色 `RequireTwoFactor` 與系統參數 `TwoFactorSettings:RequireForAdmins` 強制），見 [兩步驟驗證](兩步驟驗證-prd.md)；已不屬規劃中 |
| 各能力後續構想 | 見各 PRD 的「規劃中需求」章節，不屬於目前的驗收範圍 |

## 四、PRD 維護規則

1. 新增選單頁時，PRD、此覆蓋矩陣、`Menu.json` 與 `SidebarMenuService.MenuPermissionMap` 權限鍵必須同步。
2. 新增無頁面核心能力時，仍須指定一份產品能力 PRD，不得只留實作文件。
3. 現行需求以程式碼、設定與測試為準；文件衝突時校正 PRD 並留下 changelog。
4. 每份 PRD 必須標示文件版本、狀態、現行系統版本、首次實作版本及最後核對日期。
5. 未實作內容只能放在獨立「規劃中需求」章節；部分實作須逐項列出完成／未完成。
6. 文件使用 UTF-8 繁體中文含 BOM；提交前執行 `scripts/Test-DocsEncoding.ps1`。

> 返回 [文件總索引](../README.md)
