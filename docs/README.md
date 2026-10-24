# 文件目錄索引

- 文件版本：6.64
- 文件狀態：維護中
- 現行系統版本：1.0.8
- 首次實作版本：0.2.8
- 最後核對日期：2026/10/24

本目錄收錄 NET10-Blazor-Starter 的所有設計、規範與教學文件。文件依「特性」分類到下列子目錄；新增文件時請先依特性歸入既有分類，**若沒有任何分類適用，請自動新增一個語意明確的英文小寫分類目錄**，並同步更新本檔與主 [`readme.md`](../readme.md) 第 9 節「文件索引」。

所有 `.md` 一律 UTF-8 **含 BOM**，以 `scripts/Test-DocsEncoding.ps1`（遞迴）檢查。專案已無 CI（`.github/workflows/dotnet-ci.yml` 已於 2026/09/20 移除），commit 前請在本機執行品質關卡 `pwsh ./scripts/Invoke-QualityGate.ps1`（含文件編碼檢查，涵蓋 `docs/` 與根目錄的 `.md`）。

## 分類規則

| 目錄 | 收納特性 | 範例 |
|------|----------|------|
| [`architecture/`](architecture/) | 系統架構、資料模型、API / DTO 設計規範、開發慣例 | 開發慣例速查、架構總覽、資料模型、Web API 設計慣例、API Versioning |
| [`security/`](security/) | 認證、授權、登入、密碼與機密金鑰機制 | 認證授權、Google OAuth2、記住我、密碼儲存 |
| [`features/`](features/) | 個別功能機制說明 | 腳手架總覽（含 FAQ 與名詞附錄）、分散式快取、檔案上傳、健康監控、AI 日誌／例外分析、售後 Agent 示範系統 |
| [`guides/`](guides/) | 開發 / 操作教學、how-to、流程指南 | VS Code 上手指南、新 CRUD 頁面、新專案啟動、EFCore、測試 |
| [`operations/`](operations/) | 部署、維運、設定檔、上線檢查、CI/CD | 維護規範、部署安全清單、日誌與設定檔、CI-CD |
| [`prd/`](prd/) | 產品需求文件（功能 PRD）| PRD 主控台、各能力 PRD |
| [`changelog/`](changelog/) | 改版與變更紀錄 | 登入頁改版紀錄 |

> 每個子目錄都有自己的 `README.md` 作為該目錄索引；點上方分類連結即可進入並看到該目錄清單。

## 各分類文件

### architecture — 系統架構與設計規範
- [開發慣例與限制速查（AI/開發者必讀）](architecture/開發慣例與限制速查.md)
- [架構總覽](architecture/架構總覽.md)
- [資料模型與資料庫](architecture/資料模型與資料庫.md)
- [DTO 與模型邊界規範](architecture/DTO%20與模型邊界規範.md)
- [Web API 設計慣例](architecture/Web%20API%20設計慣例.md)
- [Web API 端點目錄](architecture/Web%20API%20端點目錄.md)
- [對話窗 UI 設計規範](architecture/對話窗%20UI%20設計規範.md)
- [介面視覺設計規範](architecture/介面視覺設計規範.md)
- [API Versioning 策略](architecture/API%20Versioning%20策略.md)

### security — 認證、授權與安全
- [認證授權與權限機制](security/認證授權與權限機制.md)
- [Google OAuth2 第三方登入](security/Google%20OAuth2%20第三方登入.md)
- [記住我登入原理說明](security/記住我登入原理說明.md)
- [密碼種類與儲存機制](security/密碼種類與儲存機制.md)

### features — 功能機制
- [腳手架總覽與複刻上手](features/腳手架總覽與複刻上手.md)
- [分散式快取機制](features/分散式快取機制.md)
- [檔案上傳機制](features/檔案上傳機制.md)
- [系統健康監控](features/系統健康監控.md)
- [AI 日誌分析](features/AI日誌分析.md)
- [AI 例外分析](features/AI例外分析.md)
- [售後 Agent 示範系統（M0 計費接軌、M1 傳統流程、M2 單一售後 Agent、M3 展示控制、M4 客服後台 AI、M5 多 Agent 交接與長期記憶、M6 效益對照頁已實作）](features/售後Agent示範系統.md)
- [售後 Agent 示範系統：MAF 功能運用說明（用到與沒用到的 MAF 功能、角色總結）](features/售後Agent示範系統-MAF功能運用說明.md)

### guides — 開發與操作指南
- [VS Code 開發環境與新專案上手指南](guides/VS%20Code%20開發環境與新專案上手指南.md)
- [建立一個新 CRUD 操作網頁說明](guides/建立一個新%20CRUD%20操作網頁說明.md)
- [腳手架開發指引](guides/腳手架開發指引.md)
- [畫面與欄位字典](guides/畫面與欄位字典.md)
- [腳手架新專案啟動流程](guides/腳手架新專案啟動流程.md)
- [EFCore 指令備忘](guides/EFCore.md)
- [測試指南](guides/測試指南.md)

> 腳手架腳本：`scripts/New-StarterProject.ps1`（複製新專案並替換 namespace / project 名稱）、`scripts/New-CrudModule.ps1`（一行產生並登記新 CRUD 模組，0.9.110 起；改了樣板跑 `scripts/Test-CrudGenerator.ps1`）。

### operations — 維運與部署
- [維護規範](operations/維護規範.md)
- [正式部署與安全檢查清單](operations/正式部署與安全檢查清單.md)
- [日誌與設定檔說明](operations/日誌與設定檔說明.md)
- [備份與還原操作手冊](operations/備份與還原操作手冊.md)
- [Gmail 寄信設定指南](operations/Gmail寄信設定指南.md)
- [AI 模型計費更新指南](operations/AI模型計費更新指南.md)
- [CI-CD 與品質檢查](operations/CI-CD與品質檢查.md)

### prd — 產品需求文件
- [PRD 主控台（能力覆蓋矩陣）](prd/README.md)
- [首頁與導覽 PRD](prd/首頁與導覽-prd.md)
- [登入與帳號流程 PRD](prd/登入與帳號流程-prd.md)
- [專案項目 PRD](prd/專案項目-prd.md)
- [使用者管理 PRD](prd/使用者管理-prd.md)
- [角色管理 PRD](prd/角色管理-prd.md)
- [分類清單 PRD](prd/分類清單-prd.md)
- [團隊清單 PRD](prd/團隊清單-prd.md)
- [系統健康監控 PRD](prd/系統健康監控-prd.md)
- [日誌檢視 PRD](prd/日誌檢視-prd.md)
- [資料庫用量 PRD](prd/資料庫用量-prd.md)
- [日誌等級設定 PRD](prd/日誌等級設定-prd.md)
- [系統例外紀錄 PRD](prd/系統例外紀錄-prd.md)
- [Token 用量 PRD](prd/Token用量-prd.md)
- [AI 對話紀錄 PRD](prd/AI對話紀錄-prd.md)
- [排程作業 PRD](prd/排程作業-prd.md)
- [系統參數 PRD](prd/系統參數-prd.md)
- [系統備份與還原 PRD](prd/系統備份與還原-prd.md)
- [資料保留與清理 PRD](prd/資料保留與清理-prd.md)
- [站內通知與公告 PRD](prd/站內通知與公告-prd.md)
- [個人資料 PRD](prd/個人資料-prd.md)
- [兩步驟驗證 PRD](prd/兩步驟驗證-prd.md)
- [首頁儀表板 PRD](prd/首頁儀表板-prd.md)
- [通用匯出 PRD](prd/通用匯出-prd.md)
- [AI 提示詞管理 PRD](prd/AI提示詞管理-prd.md)
- [AI 用量配額 PRD](prd/AI用量配額-prd.md)
- [LLM 呼叫費用估算 PRD](prd/LLM呼叫費用估算-prd.md)
- [紀錄分類與團隊權控 PRD](prd/紀錄分類與團隊權控-prd.md)
- [稽核紀錄 PRD](prd/稽核紀錄-prd.md)
- [寄信服務 PRD](prd/寄信服務-prd.md)
- [日誌與例外處理 PRD（全系統共用規範）](prd/日誌與例外處理-prd.md)
- [售後 Agent 展示系統設計（已實作：M0–M6）](prd/售後Agent展示系統-設計.md)
- [售後 Agent 示範系統企劃書（對外版，已完成）](prd/售後Agent示範系統企劃書.md)
- [售後 Agent 示範系統企劃規劃說明書（內部版，M0–M6 已完成）](prd/售後Agent示範系統企劃規劃說明書.md)
- [售後 Agent 展示系統 M0＋M1 實作計畫（已完成）](prd/售後Agent展示系統-M0M1實作計畫.md)
- [售後 Agent 展示系統 M2 實作計畫（已完成）](prd/售後Agent展示系統-M2實作計畫.md)
- [售後 Agent 展示系統 M3 實作計畫（已完成）](prd/售後Agent展示系統-M3實作計畫.md)
- [售後 Agent 展示系統 M4 實作計畫（已完成）](prd/售後Agent展示系統-M4實作計畫.md)
- [售後 Agent 展示系統 M5 實作計畫（已完成）](prd/售後Agent展示系統-M5實作計畫.md)
- [售後 Agent 展示系統 M6 實作計畫（已完成）](prd/售後Agent展示系統-M6實作計畫.md)
- [售後 Agent 示範系統開發歷程紀錄（持續更新）](prd/售後Agent示範系統開發歷程紀錄.md)

### changelog — 變更紀錄
