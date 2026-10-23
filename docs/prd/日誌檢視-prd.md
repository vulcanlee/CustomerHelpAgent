# 日誌檢視 PRD

- 文件版本：1.18
- 文件狀態：已實作
- 現行系統版本：1.0.0
- 首次實作版本：0.4.26
- 最後核對日期：2026/10/23

## 一、目標與範圍

提供管理員一個可篩選的日誌查詢頁面（`/logs`），能依時間區段、筆數、最低等級、關鍵字與錯誤追蹤碼檢視 NLog 寫出的檔案日誌，並可將查詢結果以原始 nlog 文字格式匯出下載到本機。

在此頁面之前，要看日誌只能登入伺服器直接開檔案；`/system-health` 雖會顯示最後 100 筆，但只讀「今天」的檔案，且是一整塊無法篩選的純文字。

- 範圍：`/logs` 查詢頁、五項篩選條件、可展開檢視原始文字的結果表格（含全部展開／收合）、原始格式匯出下載、複製到剪貼簿（單筆與整批）、AI 分析。
- 非範圍：告警通知、日誌統計圖表、跨機器彙整、日誌保存政策調整（日誌檔保存由 `nlog.config` 的 `maxArchiveDays`＝30 天決定；0.9.78 的自動清理只管資料庫裡的例外與稽核紀錄）、修改日誌寫入等級門檻（見 [日誌等級設定](日誌等級設定-prd.md)）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/logs` | 系統管理 › 監控與診斷 › 日誌檢視 | 已登入且為管理員（`IsAdmin`） | 系統管理員／維運 |

權限採**管理員專屬**設計：`MagicObjectHelper.角色_監控與診斷`（0.9.113 前為 `角色_統計與分析`）與 `角色_日誌檢視` 有定義並登記於 `SidebarMenuService.MenuPermissionMap`，但**刻意未列入** `RolePermissionService.GetRoleListPermissionAllName()`。因此不會種出 `Permission` 資料列、角色權限矩陣不會顯示這兩項、任何角色都無法被授予，只有 `AuthenticationStateHelper.CheckAccessPage` 的管理員短路能通過。非管理員在側邊欄看不到整個「監控與診斷」群組（0.9.113 前名為「統計與分析」）；直接輸入網址會看到無權限訊息，並留下一筆 `Permission.Denied` 稽核（目標 `Page`／`/logs`，0.9.78 起）。

頂欄頁名旁有「操作說明」按鈕（0.9.66 起，`PageHelpDialog`），內容為 `Datas/Help/logs.md`，於 `Datas/HelpTopics.json` 登記 `/logs`。

## 三、畫面與欄位

**工具列篩選條件**

| 條件 | 型態 | 預設值 | 說明 |
| --- | --- | --- | --- |
| 起始／結束時間 | 兩個 `DatePicker`（含時間） | 最近 1 小時 | 區間上限 3 天，超過時自動夾住起始時間並提示；起始晚於結束時自動對調並提示 |
| 最低等級 | 下拉 | 不限 | 不限／TRACE／DEBUG／INFO／WARN／ERROR／FATAL |
| 筆數 | 數字輸入 | 100 | 語意為「取最新 N 筆」，範圍 1～10000（`LogQueryRequest.MaxTake`），超出時自動調整並提示 |
| 關鍵字 | 文字 | 空 | 比對整筆原始文字，不分大小寫 |
| 錯誤追蹤碼 | 文字 | 空 | 0.9.78 起。只留 TraceId 欄位**完全相同**的紀錄（不分大小寫）；使用者回報的 8 碼追蹤碼貼這裡，就能看到該次請求或互動的所有日誌行 |

條件變更**不會**自動重新查詢，需按「查詢」按鈕；避免每改一個條件就重讀一次檔案。

版面（0.9.112 起）：第一列為起訖時間、最低等級、筆數與右側圖示按鈕；其下是快速區間按鈕；再下一列是「關鍵字｜錯誤追蹤碼」各佔一半（寬度 ≤ 640px 改為上下排列）。
兩個文字框的寬度由本元件的格子（`.log-view-text-filters`）決定 —— AntDesign 渲染的 `<input>` 不帶本元件的 scope 屬性，直接對它下 width 不會生效。

**快速區間**（0.9.111 起）：工具列下方一列小按鈕「最近 5 分」「最近 15 分」「最近 1 小時」「今天」。按下即把起訖時間設為
`now − 5 分／15 分／1 小時 ～ now` 或 `今天 00:00:00 ～ now`（`LogTimeRanges.Last`／`Today`）並**立即查詢**；
最低等級、筆數、關鍵字、錯誤追蹤碼維持原值。這是快速區間唯一「改條件即查詢」的例外。

**查前後 1 分鐘**（0.9.111 起）：展開列的原文區塊上方有「查前後 1 分鐘」按鈕。按下即把區間設為該筆時間 ±1 分鐘
（`LogTimeRanges.Around`），並為了看到這段期間**所有**事件，同時把最低等級改回「不限」、清空關鍵字與錯誤追蹤碼、
筆數改成上限 10,000（服務只保留最新 N 筆，筆數不夠時較早的紀錄——可能就是該筆本身——會被切掉），然後立即查詢。
展開列是另一個 `tr`，按鈕不會觸發整列的複製。

工具列按鈕由左至右：查詢（`search`）、全部展開（`unfold_more`）、全部收合（`unfold_less`）、複製本次查詢結果（`content_copy`）、匯出本次查詢結果（`file_download`）、AI 分析（`insights`）。
查詢失敗時狀態列顯示「查詢日誌失敗：{例外型別}。」並附上錯誤追蹤碼（0.9.78 起）。

**結果表格**：欄位為時間、等級、記錄器、訊息、TraceId（0.9.78 起＝錯誤追蹤碼，HTTP 請求與 Blazor 每次互動各一個 8 碼短碼）；等級以顏色標籤呈現（ERROR/FATAL 紅、WARN 橘、INFO 藍、DEBUG 青）。畫面依時間**倒序**（最新在上），每頁 20 筆。每列可展開，顯示未經任何處理的完整原始文字，含多行例外堆疊追蹤。0.9.70 起展開內容以深梅黑底、淡粉白等寬字呈現（`theme.css` 的 `--app-code-*`，與系統健康監控「最近 24 小時警告以上日誌」展開列、系統例外紀錄堆疊同一組），超出 320px 高度時捲軸也是深色。

**全部展開／全部收合**（0.9.69 起）：工具列「查詢」右邊兩顆按鈕（`unfold_more`／`unfold_less`）。
按「全部展開」後**換頁仍維持展開**（`DefaultExpandAllRows` 綁在 `isExpandAll` 上），直到按「全部收合」或重新查詢。
重新查詢會同時呼叫 `Table.CollapseAll()`：AntDesign 依 `RowKey` 記住每列展開狀態，新結果的 `Sequence`
與舊結果重疊時，只把旗標設回 false 那些列仍會維持展開。查無資料時兩顆按鈕停用。

**複製到剪貼簿**（0.9.69 起）：

- **點一下任一列** → 複製該筆 `Raw`（與匯出每筆相同，含多行堆疊），右下角提示「已複製 1 筆日誌到剪貼簿。」。
  只複製、不切換展開。點左邊的展開圖示、點展開後的原文區塊都**不**觸發複製（實測：展開圖示不冒泡到列的
  `OnRowClick`，原文區塊是另一個 `tr`）。可點的列游標為手指，原文區塊為文字游標。
- **「複製本次查詢結果（原始 nlog 格式）」**（`content_copy`）→ 複製本次查詢**全部 N 筆**（不受目前頁碼影響），
  依時間正序，內容與匯出 `.log` 完全一致（兩者共用 `BuildRawText()`）。超過 **1,000 筆**先跳確認窗
  （「仍要複製」／「取消」），建議改用匯出。查無資料時停用。
- 一律走 `wwwroot/js/clipboard.js` 的 `appClipboard.copyText`：https 或 localhost 用 `navigator.clipboard`，
  內網以 http 部署時退回 `textarea + execCommand`，**http／https 都能用**。瀏覽器拒絕時提示
  「瀏覽器拒絕存取剪貼簿，請手動選取複製。」。

**匯出**：按下匯出後下載 `CustomerHelpAgent.Web-logs-{yyyyMMdd-HHmmss}.log`，內容為**本次查詢結果的全部 N 筆**（不受目前翻到第幾頁影響），**依時間正序（舊→新）**排列，與真實 nlog 檔案的閱讀習慣一致。編碼為 UTF-8 含 BOM，確保以記事本或 Excel 開啟時繁體中文不亂碼。查無資料時匯出按鈕為停用狀態。

**AI 分析**（0.9.4 起）：工具列最右邊的按鈕（`insights`），把**目前查詢結果**送給 AI 整理，結果顯示在
唯讀對話窗。刻意不重新查詢，行為完全可預測；對話窗頁首會標明分析的時間區間、最低等級、
關鍵字、實際分析筆數與是否被上限截斷，所以不會與畫面上的條件搞混。

**0.9.8 起對話窗在按下按鈕的當下就打開**，先顯示等待畫面，AI 回來之後把結果渲染進同一個窗。

- 送出的是每筆的原始 nlog 行，預設上限 100 筆（`AiSettings:MaxEntries` 可調）。
  超過時取**最新**的 N 筆，並額外發一則 toast 說明只分析了最新幾筆。
  **筆數是唯一的界線**：0.9.7 起沒有任何字元上限，每筆內容原封不動送出。
- 呼叫前後都以**右下角通知**告知階段：送出中、完成（含實際分析筆數）、失敗原因。
  失敗時的訊息會盡量指名要改哪個設定，而不是只丟一個錯誤代碼。
- **等待畫面**（0.9.8）：大字「AI 正在分析 N 筆日誌…」與**每秒更新的「已等待 N 秒」**。
  轉圈圈只證明瀏覽器還活著，跳動的秒數才證明這次呼叫還在進行中 ——
  逾時上限是 600 秒，這是使用者願意繼續等下去的唯一依據。
- **等待中關窗等於放棄**（0.9.8）：按 X 或 Esc 會真的取消 HTTP 請求，並發一則 toast。
  等待中點遮罩不會關窗（誤點一次就白花一次 AI 費用）。稽核仍會留一筆，
  因為請求已送出、上游很可能照樣計費。
- **失敗留在窗內**（0.9.8）：錯誤訊息由使用者自己關閉，不再隨 toast 一起消失 ——
  那些訊息常常是「請把某個設定改成 null」這種要照著做的內容。
- 對話窗內有「一般／中／大」三段字級（100%／125%／150%，預設一般，作用於整個窗內容）、
  「複製結果」（複製 Markdown 原文，方便貼進工單或通訊軟體）與
  「匯出 PDF 報告並下載」，頁首另列出模型名稱、token 用量明細
  （輸入／輸出／合計／快取輸入／推論，API 有回才顯示）與耗時。
- 對話窗尺寸為 **96vw × 96vh**（接近滿版）。
- **未設定時按鈕停用**，游標停留會說明缺哪一項設定。沒有獨立的功能開關 ——
  有沒有填 `AiSettings:ApiKey` 就是開關，而範本出貨時它是空字串，
  所以拿到這份範本的人不會看到一個按下去就報錯的按鈕。
- 權限沿用本頁的管理員判斷，不另設權限鍵。
- 每次呼叫記入「Token 用量」（作業名稱「AI 日誌分析」），0.9.72 起同時寫一筆「AI 對話紀錄」（完整 Prompt／Response）。

完整機制（設定、截斷規則、安全管線、PDF 字型）見
[AI 日誌分析](../features/AI日誌分析.md)。

## 四、內部系統運作

1. `LogViewerView.OnInitializedAsync`：先 `AuthenticationStateHelper.Check` 驗證登入，再 `CheckIsAdmin`；非管理員設定 `RoleMessage`、寫 `Permission.Denied` 稽核並中止，**權限通過前不讀取任何日誌內容**。通過後將時間區段設為最近 1 小時並執行首次查詢。
2. `INLogFilePathResolver` 依 `NLog:BasePath` 組態與 Program 命名空間推導日誌目錄與檔名前綴，並列出區間內實際存在的日誌檔。
   - 以 `{前綴}-{日期}*.log` 萬用字元列舉而非精確檔名：`nlog.config` 設了 `archiveAboveSize` 卻未指定 `archiveFileName`，同一天可能另存在編號封存檔。
   - 排序依 `LastWriteTimeUtc` 而非檔名：封存序號 10 與 2 的字典序會錯。
3. `ILogQueryService.QueryAsync` 依時間升冪逐檔前向串流讀取：
   - 以「該行第 24 個字元是否為 `|` 且前 24 字元可解析為 `yyyy-MM-dd HH:mm:ss.ffff`」判斷是否為新紀錄開頭；不符者視為前一筆的續行（堆疊追蹤）。
   - 整筆（含所有續行）湊齊後才套用篩選 —— 關鍵字比對的是完整原始文字。
   - 篩選順序：時間區段 → 最低等級 → 關鍵字 → 錯誤追蹤碼（與 TraceId 完全相同、不分大小寫）；通過者推入容量為 N 的佇列，滿了就移除最舊的。讀完即為「最新 N 筆」且已是時間正序。
   - 提前結束只有一項：解析出的時間超過結束時間，即中斷該檔與其後所有檔案。
     ⚠️ **刻意不依 `File.GetLastWriteTime` 做「整檔跳過」**（0.4.39 移除）——
     判斷依據必須是**檔案內容的時間戳**，不能是檔案系統中繼資料。理由見下節。
4. 匯出時將結果的 `Raw` 以換行串接，加上 BOM 後透過 `DotNetStreamReference` 經 SignalR circuit 串流給瀏覽器，由 `wwwroot/js/file-download.js` 組成 Blob 觸發下載；並寫一筆 `LogViewer.Export` 稽核（0.9.78 起，只記筆數）。
5. AI 分析時 `AiLogPromptBuilder` 只做一道處理 —— 取最新 N 筆（`MaxEntries`），
   內容原封不動（0.9.7 起沒有任何字元上限，理由見 [AI 日誌分析](../features/AI日誌分析.md)），交由
   `IAiLogAnalysisService` 以 named `HttpClient` 呼叫 Chat Completions。回傳的 Markdown 經
   `AiMarkdownRenderer` 的安全管線轉成 HTML 後才以 `MarkupString` 呈現。PDF 由
   `AiReportPdfBuilder`（PDFsharp + MigraDoc）產生，走與日誌匯出相同的下載機制，
   只是多帶了 `application/pdf` 這個 content type。每次分析與每次 PDF 匯出各寫一筆
   `AuditLog`（`LogViewer.AiAnalyze` 與 `LogViewer.AiAnalyzeExportPdf`）。

## 五、限制與已知取捨

- **欄位切分的邊界**：`nlog.config` 的 layout 未對 `${message}` 逸出 `|`，因此訊息與例外的分界只能取最後一個 `|`。當例外文字本身含 `|` 時「訊息」欄可能顯示不完整 —— 這正是每列都可展開檢視原始文字的原因，`Raw` 永遠逐字正確。
- **前向讀取的成本**：`archiveAboveSize` 為 100MB，最壞情況（3 天區間且有多個大封存檔）需循序掃描數百 MB，約 1.5～4 秒，期間該元件無回應。典型情況遠低於 100ms。服務會記錄每次查詢的耗時、掃描檔數與行數；若耗時經常超過 1 秒，才值得評估改為反向讀取。
- **TRACE／DEBUG 選項需搭配「日誌等級設定」才有作用**：`nlog.config` 的規則預設只把 Info 以上寫入檔案。0.4.29 起可於 [日誌等級設定](日誌等級設定-prd.md)（`/log-level-setting`）在執行期把最低等級調到 DEBUG／TRACE，此處的篩選選項即開始有實際意義。
- **路由守衛只到「已登入」**：0.9.41 起本頁與所有 `Pages/` 頁面一樣繼承 `[Authorize]`，未登入者在 HTTP 層就被導去登入頁；「管理員專屬」仍由 `OnInitializedAsync` 內的 `CheckIsAdmin()` 判斷，判定完成前不渲染任何內容（`isAccessChecked`），權限檢查通過前也不會取得任何日誌內容。見 [開發慣例與限制速查 §5.1](../architecture/開發慣例與限制速查.md)。
- **點列複製不排除拖曳選字**（0.9.69）：在列上拖曳反白一段文字放開時也會整筆複製並覆蓋剪貼簿。
  刻意維持簡單（不多寫一段 JS 判斷選取範圍）；只想複製部分文字時，展開後在原文區塊裡選取即可，那裡不觸發複製。
- **整批複製的確認門檻是程式常數 1,000 筆**（`LogViewerView.CopyAllConfirmThreshold`，不進 appsettings）：
  上限 10,000 筆可能是數 MB 文字，貼上時目標程式會卡住；這種量匯出比較合適，但仍讓使用者決定。
  複製不寫稽核，只寫一筆應用程式日誌（`Rows`、`Characters`）；匯出 0.9.78 起寫一筆 `LogViewer.Export` 稽核（只記筆數）。
- **無法解析等級的紀錄一律保留**，不因等級篩選而隱藏，避免格式異常的資料在排查時憑空消失。
- 單一檔案讀取失敗（輪替、鎖定、權限）只會跳過該檔並顯示警告，不會讓整次查詢失敗。
- **AI 分析不支援追問、不做串流輸出**：一次分析就是一份報告。追問與逐字串流的複雜度
  換不到對應的價值，需要不同角度時改查詢條件重跑即可。
- **AI 分析不做自動重試、也不做頻率限制**：這是使用者主動觸發且會計費的動作，
  失敗讓他再按一次即可；自動重試只會讓成本加倍，還會掩蓋「設定錯誤」這種重試永遠
  不會好的問題。頻率與成本控管請在 Azure OpenAI 資源的配額層（TPM/RPM）設定，
  事後追查看 `AuditLog`。
- **AI 回傳內容過大時會中止顯示**（HTML 超過 512 KB）：避免單次 render diff 過肥
  讓對話窗開啟卡頓。0.9.7 起 `AiSettings:MaxOutputTokens` 預設不送，所以這是**主要**防線。
- **送出的日誌沒有字元上限**（0.9.7）：有多少字就送多少字，因為切掉例外堆疊的尾巴
  等於丟掉根因。代價是理論上可能撞到模型的內容視窗上限，屆時會收到 400
  `context_length_exceeded`，訊息會直接請使用者縮小時間區間或減少筆數。
- **取消不保證不計費**（0.9.8）：關窗會中止 HTTP 連線，但請求早已送達上游，
  對方是否照樣計費不在本系統的控制範圍。所以取消仍然寫一筆稽核。
- **字級選擇不保存**（0.9.8）：關窗重開回到「一般」。當時專案刻意沒有任何前端偏好保存
  機制（全庫零 `localStorage`），為了一個字級旋鈕引進一套新基礎建設不划算。
  （0.9.112 起側邊選單的收合狀態以 `localStorage` 保存〔`wwwroot/js/nav-state.js`〕，0.9.121 起「操作說明」窗的字體大小也保存〔`app.helpFontSize`〕；本頁 AI 對話窗的字級仍不保存。）
- **PDF 只有一個字重**：內嵌字型只有 Noto Sans TC Regular，而 PDFsharp 沒有粗體模擬，
  因此報告的層級靠字級、顏色與框線表達，Markdown 的 `**粗體**` 在 PDF 裡呈現為深色而非粗體。
  加 Bold 字面會讓 repo 再肥約 7 MB。
- **不以檔案最後寫入時間做整檔跳過**（0.4.39 移除該優化）：
  - 它幾乎沒有效益 —— `GetExistingFilesInRange` 已依**檔名日期**過濾，區間外的檔案本來就不會出現；
    只有「起始日當天、且已停止寫入」的檔案才省得到一次讀取。預設的「最近 1 小時」查詢完全享受不到
    （當天檔案的 mtime 就是現在，必定不跳過，仍會從檔頭完整串流）。
  - 它卻讓查詢結果取決於**檔案系統中繼資料**。`nlog.config` 設了 `keepFileOpen="true"`，
    NTFS 對持續開啟的檔案可能延遲更新目錄項的 last-write time；一旦落後於查詢起點，
    **正在寫入的當天日誌檔會被整個跳過**，畫面顯示「查無日誌紀錄」但檔案裡其實有資料。
  - 這段邏輯也曾讓當時的 CI（已於 2026/09/20 移除）紅燈：測試在 UTC 03:16 寫檔（mtime = 03:16）卻查詢當日 08:00～10:00，
    整檔被誤判為過舊。詳見 變更紀錄。

## 六、驗收與測試

對應測試檔 `CustomerHelpAgent.Tests/LogQueryServiceTests.cs`（18 支）：涵蓋等級／關鍵字／時間區間過濾、
錯誤追蹤碼過濾與 TraceId 解析（`Query_TraceCodeFilter_ShouldKeepOnlyExactTraceIdMatches`、`Query_TraceIdPopulated_ShouldParseTraceId`，0.9.78）、
`Take` 上限與預設、多行堆疊的合併解析、跨檔案查詢、區間上限 3 天的裁切，以及
**`Query_WhenFileTimestampIsStale_ShouldStillReadEntries`** —— 釘住 0.4.39 移除「以檔案 mtime 整檔跳過」
那個優化的原因（見第五節），該測試與執行時刻無關，修正前必紅。
快速區間與「查前後 1 分鐘」的區間計算（0.9.111）由 `CustomerHelpAgent.Tests/LogTimeRangesTests.cs`（3 支：`Last_ShouldEndAtNowAndStartSpanEarlier`、`Today_ShouldStartAtMidnightAndEndAtNow`、`Around_ShouldSpanOneMinuteEachSide`）守住。

AI 分析對應八個測試檔（0.9.4 起；部分與 AI 例外分析共用，現行共 118 個測試方法）：`AiSettingsTests`、`AiChatEndpointTests`、
`AiLogPromptBuilderTests`、`AiChatResponseParserTests`、`AiMarkdownRendererTests`、
`AiLogAnalysisServiceTests`、`AiReportPdfBuilderTests`、`AiModalStyleConventionTests`（0.9.8）。
其中五支是安全、成本與行為的守門測試，壞了不要改測試：

- `AiMarkdownRendererTests` 釘住 Markdig 管線不得加上會開放 HTML 注入的擴充。
- `AiLogAnalysisServiceTests.AnalyzeAsync_ShouldNeverEchoApiKeyOrUpstreamBody` 以哨兵字串
  斷言錯誤訊息不含金鑰與上游 body。
- `AiReportPdfBuilderTests.Font_ShouldBeEmbeddedInWebAssembly` 斷言內嵌字型長度超過一百萬
  位元組 —— 字型缺失時 PDF 不會報錯，只會整片變成空白方框，除了這支測試之外完全靜默。
- `AiLogAnalysisServiceTests.AnalyzeAsync_ShouldReportCanceled_WhenCallerCancels`（0.9.8）
  分辨「使用者放棄」與「逾時」。兩者都是 `TaskCanceledException`，filter 寫反會把使用者的
  決定記成系統故障。
- `AiModalStyleConventionTests`（0.9.8）確保對話窗內每一條字級都乘上 `--ai-font-scale`，
  並擋下在 `<Modal>` 標籤上重複設定 `Width`。漏掉的症狀是「按了放大只有這段沒變」，
  不會壞、不會紅。

---

## 七、相關文件

- 日誌檔位置、layout 與保存設定：`docs/operations/日誌與設定檔說明.md`
- 選單與權限機制：`docs/prd/首頁與導覽-prd.md`、`docs/security/認證授權與權限機制.md`
- 健康監控頁的最近 24 小時 WARN 以上日誌（沿用本頁的 `ILogQueryService`）：`docs/prd/系統健康監控-prd.md`
- AI 分析的設定、安全管線與 PDF 機制：`docs/features/AI日誌分析.md`

> 返回 [prd 索引](README.md)
