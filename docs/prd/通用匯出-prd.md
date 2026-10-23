# 通用匯出 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.9.107
- 最後核對日期：2026/10/23

## 一、目標與範圍

業務頁（專案、分類、團隊、使用者）原本沒有任何匯出；診斷頁的 CSV 匯出各自手寫。0.9.107 起由共用的 `TabularExport` 產生檔案：業務頁匯出 Excel，診斷頁維持 CSV（路線圖 E-16）。

使用者決定（2026/10/04）：

| 題目 | 決定 |
|---|---|
| 哪些頁面 | 專案項目、分類清單、團隊清單、使用者管理 |
| 誰可以匯出 | 看得到清單就能匯出（沿用檢視權限；0.9.37 下架的 export 動作不恢復） |
| 上限 | 一次 10000 筆 |
| 既有 CSV | 收斂到共用服務，格式維持不變 |

- **非範圍**：匯入、排程匯出、多工作表報表、診斷頁改成 Excel。

## 二、使用者與入口

| 頁面 | 按鈕 | 資料 | 誰看得到按鈕 |
|---|---|---|---|
| `/projects` | 匯出 Excel | 目前的搜尋、分類／團隊過濾、排序、「顯示已刪除」 | 能進這一頁的人 |
| `/categories` | 匯出 Excel | 目前的搜尋、排序、「顯示已刪除」 | 同上 |
| `/teams` | 匯出 Excel | 樹狀模式匯出全部；搜尋或「顯示已刪除」時依目前清單 | 同上 |
| `/myusers` | 匯出 Excel | 目前的搜尋、排序、「顯示已刪除」 | 管理員 |
| 稽核紀錄、系統例外紀錄、Token 用量、AI 對話紀錄 | 匯出 CSV（不變） | 各頁既有規則 | 管理員 |

## 三、畫面與欄位

- 工具列「匯出 Excel」（圖示 `file_download`），下載 `CustomerHelpAgent.Web-{projects|categories|teams|users}-{yyyyMMdd-HHmmss}.xlsx`。
- 工作表：標題列粗體、凍結、自動篩選；日期與時間是 Excel 原生日期（`yyyy-mm-dd`／`yyyy-mm-dd hh:mm`），數字是數字，是／否以文字表示。
- 欄位：
  - 專案：標題、描述、開始日期、結束日期、狀態、優先級、完成百分比、負責人、分類、團隊、建立時間、更新時間、刪除時間、刪除者。
  - 分類：名稱、描述、適用團隊、啟用、建立時間、更新時間、刪除時間、刪除者。
  - 團隊：名稱、上層部門（已刪除的上層顯示「（已刪除的部門）」）、代號、描述、啟用、建立時間、更新時間、刪除時間、刪除者。
  - 使用者：帳號、名稱、Email、角色（主要角色）、狀態、管理員、兩步驟驗證、鎖定至、建立時間、更新時間、刪除時間、刪除者。⚠️ 不含密碼、鹽、兩步驟驗證密鑰、Google 識別碼；額外角色與直接綁定的團隊不在清單查詢裡，也不匯出。
- 超過上限：「符合條件的資料有 N 筆，超過一次匯出的上限 10000 筆；請縮小搜尋或篩選條件後再匯出。」，不產生檔案。

## 四、內部系統運作

- `Web/Export/TabularExport.cs`：`ExportColumn<T>(Header, Value, ExcelFormat, Width)`；`ToXlsx`（ClosedXML，固定欄寬 —— 自動欄寬要量字型，伺服器上不可靠）、`ToCsvText`／`ToCsv`（標題列不加引號、每一格以雙引號包住並把雙引號加倍、`AppendLine` 換行，經 `TextDownloadPayload.Utf8WithBom`）、`DownloadAsync`（`appFileDownload.downloadFromStream`，與 PDF 同一條路）。
  字串一律寫成文字值，以「=」開頭的內容不會變成公式。
- `Web/Export/BusinessExports.cs`：`BuildAsync(query, 目前條件, …)` 以第一頁、`PageSize = 10000`、`Take = 1` 呼叫畫面同一個服務方法（`GetAsync`／`GetDeletedAsync`），服務回的總筆數超過上限就不產生檔案；團隊範圍由服務內的 `RecordTeamScope` 套用，與畫面一致。四頁的欄位定義與 `WriteAuditAsync` 也在這裡。
- 稽核：`Project.Export`／`Category.Export`／`Team.Export`／`User.Export`，目標 `*`，detail `format=xlsx; rows=N`（顯示已刪除時加 `; deleted=true`），不記內容。
- 診斷頁：四頁的欄位改成各自的 `CsvColumns`（`internal static`），輸出與 0.9.106 之前逐字相同（`ExportTests` 以舊寫法當作對照）。唯一的欄位異動是 0.9.117 起稽核紀錄 CSV 在**最後一欄**加「來源IP」（不打亂依欄位位置讀檔的下游；0.9.123 起「來源 IP」篩選也套用到匯出）。日誌檢視的 `.log` 匯出不變。
- 套件：`ClosedXML` 0.105.1（MIT）；傳遞相依 `SixLabors.Fonts` 解析到 1.0.0（Apache-2.0；2.x 起改為 Split License，升級前要確認授權）。

## 五、權限與安全

- 匯出不另外授權：能看到清單就能匯出，內容與畫面完全相同（同一個查詢、同一套團隊範圍）。
- 使用者匯出含 Email（個資），只有管理員進得去使用者管理；不含任何密碼或金鑰欄位（測試守門）。
- 每次匯出寫稽核。

## 六、錯誤與邊界

- 超過 10000 筆：不匯出並提示縮小條件。
- 0 筆：產生只有標題列的檔案。
- 匯出失敗（例如瀏覽器斷線）：「匯出失敗：例外類型。」，寫錯誤日誌。

## 七、驗收與測試

- `CustomerHelpAgent.Tests/ExportTests.cs`：四個診斷頁 CSV 與舊寫法逐字相同（含引號、逗號、換行、空值；稽核紀錄的對照含最後一欄「來源IP」）、BOM、xlsx 讀回（標題、粗體、凍結、篩選、原生日期與數字、是／否、空值、「=」開頭不是公式）、⭐ 非管理員只匯出團隊範圍內的專案、超量拒絕且以目前條件查詢、使用者匯出不含機密、稽核內容；`ProjectApiOwnerTests`：API 建立專案不帶負責人回 400。

## 八、相關程式與文件

- `src/CustomerHelpAgent/CustomerHelpAgent.Web/Export/TabularExport.cs`、`BusinessExports.cs`
- 四個業務頁 `ProjectViewView`、`CategoryViewView`、`TeamViewView`、`MyUserView`；四個診斷頁 `AuditLogView`、`ExceptionLogView`、`TokenUsageView`、`AiCallLogView`
- 交叉連結：[專案項目](專案項目-prd.md)、[分類清單](分類清單-prd.md)、[團隊清單](團隊清單-prd.md)、[使用者管理](使用者管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
