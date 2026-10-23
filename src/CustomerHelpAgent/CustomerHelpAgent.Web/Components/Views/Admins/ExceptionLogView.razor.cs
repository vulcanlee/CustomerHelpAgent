using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    public partial class ExceptionLogView
    {
        /// <summary>
        /// 「清除很久沒再發生的紀錄」門檻。刻意是程式常數而非設定鍵 ——
        /// 這是管理員按一下才會發生的動作，不需要每個部署各自調整。
        /// </summary>
        private int PurgeDays => RetentionOptions.CurrentValue.ManualExceptionLogDays;

        /// <summary>
        /// 「複製目前查詢結果」超過這個筆數先確認。每筆都含完整堆疊（數 KB～數十 KB），
        /// 比日誌檢視的單行內容大得多，所以門檻也低得多；大量資料建議改用匯出。
        /// </summary>
        private const int CopyAllConfirmThreshold = 100;

        private readonly ILogger<ExceptionLogView> logger;
        private readonly ExceptionLogService exceptionLogService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;
        private readonly IAiExceptionAnalysisService aiExceptionAnalysisService;

        private ITable? table;

        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedSource = string.Empty;
        private string accountFilter = string.Empty;
        private string keyword = string.Empty;

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private List<ExceptionLogAdapterModel> exceptionLogAdapterModels = [];
        private IEnumerable<ExceptionLogAdapterModel> selectedRows = [];
        private bool batchDeleting;

        // 明細窗：清單放不下的欄位與完整堆疊都在這裡看。
        // 一次只會開一筆，所以不需要快取字典 —— 開窗當下才讀檔。
        private bool detailVisible;
        private ExceptionLogAdapterModel? detailItem;
        private string detailStackTrace = string.Empty;

        // 原始堆疊（null 代表堆疊檔不存在）。detailStackTrace 是給人看的文字，
        // 堆疊缺失時是一段說明，不能拿去當堆疊送給 AI。
        private string? detailRawStackTrace;
        private bool isStackTraceLoading;

        private ExceptionAiAnalysisModal? aiAnalysisModal;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        /// <summary>例外紀錄的維護動作本身也要留稽核（LOG-14）；0.9.78 之前刪除、清除、清空、匯出都不留痕跡。</summary>
        private Task WriteAuditAsync(string action, string targetId, string detail)
            => ViewAudit.WriteAsync(AuditLogService, CurrentUserService, action, "ExceptionLog", targetId, detail);

        public ExceptionLogView(
            ILogger<ExceptionLogView> logger,
            ExceptionLogService exceptionLogService,
            ModalService modalService,
            NotificationService notificationService,
            IAiExceptionAnalysisService aiExceptionAnalysisService)
        {
            this.logger = logger;
            this.exceptionLogService = exceptionLogService;
            this.modalService = modalService;
            this.notificationService = notificationService;
            this.aiExceptionAnalysisService = aiExceptionAnalysisService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何例外內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/system-exceptions");
                logger.LogWarning("Exception log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            // 換頁、排序、查詢與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = new ExceptionLogQuery
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    Source = selectedSource,
                    Account = accountFilter,
                    Keyword = keyword,
                    CurrentPage = _pageIndex,
                    PageSize = _pageSize,
                    SortField = sortField,
                    SortDescending = sortDirection switch
                    {
                        "descend" => true,
                        "ascend" => false,
                        _ => null,
                    },
                };

                var result = await exceptionLogService.GetAsync(query);
                exceptionLogAdapterModels = result.Result.ToList();
                _total = result.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load exception logs.");
                ViewNotification.UnexpectedError(notificationService, $"載入例外紀錄失敗：{ex.GetType().Name}。", ex);
                exceptionLogAdapterModels = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnTableChange(QueryModel<ExceptionLogAdapterModel> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;

            if (args.SortModel?.Any() == true)
            {
                var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
                sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
                sortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
            }
            else
            {
                sortField = string.Empty;
                sortDirection = "None";
            }

            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Exception log search triggered.");
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.ExceptionLog.Search, "ExceptionLog",
                AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime), AuditSearchCondition.Of("source", selectedSource),
                AuditSearchCondition.Text("account", accountFilter), AuditSearchCondition.Text("keyword", keyword));
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Exception log refresh triggered.");
            await ReloadAsync();
        }

        /// <summary>
        /// 開啟明細窗。堆疊檔在開窗當下才讀 —— 清單查詢不應該一次把所有檔案讀進記憶體。
        /// </summary>
        private async Task OnViewAsync(ExceptionLogAdapterModel item)
        {
            detailItem = item;
            detailStackTrace = "讀取中…";
            detailRawStackTrace = null;
            isStackTraceLoading = true;
            detailVisible = true;
            StateHasChanged();

            var stackTrace = await exceptionLogService.GetStackTraceAsync(item.Id);
            detailRawStackTrace = string.IsNullOrWhiteSpace(stackTrace) ? null : stackTrace;
            detailStackTrace = string.IsNullOrWhiteSpace(stackTrace)
                ? "堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。完整日誌請改由「日誌檢視」查詢。"
                : stackTrace;
            isStackTraceLoading = false;

            StateHasChanged();
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.ExceptionLog.View, "ExceptionLog",
                item.Id.ToString(), $"type={item.ExceptionType}");
        }

        /// <summary>AI 按鈕的 Tooltip。未設定時直接用服務給的原因當標題，讓人知道少填了什麼。</summary>
        private string AiButtonTitle
            => aiExceptionAnalysisService.IsAvailable
                ? "AI 分析（產生分析報告，可再追問）"
                : aiExceptionAnalysisService.UnavailableReason;

        private async Task OnAiAnalyzeAsync()
        {
            if (detailItem is null || aiAnalysisModal is null || isStackTraceLoading)
            {
                return;
            }

            await aiAnalysisModal.OpenAsync(detailItem, detailRawStackTrace);
        }

        /// <summary>明細窗的「使用者」顯示文字；與複製文字共用同一個格式，只有帳號與 UserId，不含姓名／Email。</summary>
        private string DetailAccountText
            => detailItem is null ? "—" : ExceptionLogClipboardText.FormatAccount(detailItem);

        /// <summary>
        /// 點一下任一列即複製該筆（明細欄位＋完整堆疊），不開明細窗。
        /// 「查看」「刪除」按鈕外層在 razor 裡擋掉冒泡，不會進到這裡。
        /// </summary>
        private async Task OnRowClickAsync(RowData<ExceptionLogAdapterModel> row)
        {
            try
            {
                var stackTrace = await exceptionLogService.GetStackTraceAsync(row.Data.Id);
                await CopyToClipboardAsync(ExceptionLogClipboardText.Build(row.Data, stackTrace), 1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy exception log to clipboard. ExceptionLogId={ExceptionLogId}", row.Data.Id);
                ViewNotification.UnexpectedError(notificationService, "複製失敗，請改按「查看」手動選取複製。", ex);
            }
        }

        /// <summary>複製整次查詢結果（與 CSV 匯出同範圍），每筆與點列複製同格式。</summary>
        private async Task OnCopyAllAsync()
        {
            try
            {
                var result = await exceptionLogService.GetAsync(BuildFullQuery());
                var items = result.Result.ToList();
                if (items.Count == 0)
                {
                    ViewNotification.Warning(notificationService, "目前沒有可複製的例外紀錄。");
                    return;
                }

                if (items.Count > CopyAllConfirmThreshold)
                {
                    var confirmed = await ConfirmDialog.AskAsync(
                        modalService,
                        "確認複製",
                        $"本次查詢共 {items.Count:N0} 筆（每筆含完整堆疊），內容較大，貼上時可能較慢；大量資料建議改用匯出。",
                        "仍要複製");
                    if (confirmed == false)
                    {
                        return;
                    }
                }

                var stackTraces = await exceptionLogService.GetStackTracesAsync(items.Select(x => x.Id).ToList());
                var text = ExceptionLogClipboardText.BuildMany(items.Select(x => (x, stackTraces[x.Id])));
                await CopyToClipboardAsync(text, items.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy exception logs to clipboard.");
                ViewNotification.UnexpectedError(notificationService, $"複製失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>
        /// 走 appClipboard.copyText：https 用 navigator.clipboard，http 內網退回 execCommand，兩種部署都能用。
        /// </summary>
        private async Task CopyToClipboardAsync(string text, int rows)
        {
            try
            {
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", text);
                if (copied)
                {
                    logger.LogInformation("Exception logs copied to clipboard. Rows={Rows}, Characters={Characters}", rows, text.Length);
                    ViewNotification.Info(notificationService, $"已複製 {rows:N0} 筆例外紀錄到剪貼簿。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy exception logs to clipboard. Rows={Rows}", rows);
                ViewNotification.UnexpectedError(notificationService, rows == 1 ? "複製失敗，請改按「查看」手動選取複製。" : "複製失敗，請手動選取複製。", ex);
            }
        }

        private async Task OnDeleteAsync(ExceptionLogAdapterModel item)
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "刪除例外紀錄",
                "確定要刪除這一列嗎？相同的例外若再次發生，會重新出現並從 1 次開始計算。",
                "刪除");

            if (confirmed == false)
            {
                return;
            }

            var result = await DeleteOneAsync(item);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "刪除成功");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>刪除一列並記稽核；單筆刪除與「刪除選取」共用。</summary>
        private async Task<VerifyRecordResult> DeleteOneAsync(ExceptionLogAdapterModel item)
        {
            var result = await exceptionLogService.DeleteAsync(item.Id);
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.ExceptionLog.Delete, item.Id.ToString(), $"type={item.ShortExceptionType}");
            }

            return result;
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆走 <see cref="DeleteOneAsync"/>。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(Describe).ToList(), restorable: false,
                        note: "相同的例外若再次發生，會重新出現並從 1 次開始計算。"))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), Describe, DeleteOneAsync, logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting exception logs.");
                ViewNotification.UnexpectedError(notificationService, "刪除例外紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }

            static string Describe(ExceptionLogAdapterModel x) => $"{x.LastOccurredAt:yyyy-MM-dd HH:mm:ss} {x.ShortExceptionType}";
        }

        private async Task OnPurgeAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                $"清除 {PurgeDays} 天未再發生的紀錄",
                $"將刪除「最後發生」早於 {PurgeDays} 天前的所有紀錄與其堆疊檔案。此動作無法復原。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await exceptionLogService.PurgeAsync(PurgeDays);
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.ExceptionLog.Purge, "*", $"清除 {PurgeDays} 天未再發生的例外紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnClearAllAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "清空全部例外紀錄",
                "將刪除所有紀錄與整個堆疊檔案目錄。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await exceptionLogService.ClearAllAsync();
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.ExceptionLog.ClearAll, "*", $"清空全部例外紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, "已清空全部例外紀錄");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        internal static readonly IReadOnlyList<ExportColumn<ExceptionLogAdapterModel>> CsvColumns =
        [
            new("最後發生", x => x.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("次數", x => x.OccurrenceCount.ToString()),
            new("例外類型", x => x.ExceptionType),
            new("訊息", x => x.Message),
            new("來源", x => x.Source),
            new("頁面", x => x.Page),
            new("操作", x => x.Operation),
            new("記錄器", x => x.LoggerName),
            new("使用者", x => x.Account),
            new("首次發生", x => x.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("最後追蹤碼", x => x.LastTraceId),
        ];

        private async Task OnExportAsync()
        {
            try
            {
                var result = await exceptionLogService.GetAsync(BuildFullQuery());

                // 0.9.107 起經共用的 TabularExport（BOM、跳脫、換行與之前手寫的相同，ExportCsvCompatibilityTests 守門）。
                var bytes = TabularExport.ToCsv(CsvColumns, result.Result);
                var fileName = $"CustomerHelpAgent.Web-exceptions-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.DownloadAsync(fileName, bytes, TabularExport.CsvContentType);

                logger.LogInformation("Exception log export downloaded. Rows={Rows}", result.Count);
                await WriteAuditAsync(AuditActions.ExceptionLog.Export, "*", $"format=csv; rows={result.Count}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Exception log export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>
        /// 匯出與「複製目前查詢結果」共用：目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照。
        /// </summary>
        private ExceptionLogQuery BuildFullQuery() => new()
        {
            StartTime = startTime,
            EndTime = endTime,
            Source = selectedSource,
            Account = accountFilter,
            Keyword = keyword,
            CurrentPage = 1,
            PageSize = ExceptionLogService.MaxRows,
            SortField = sortField,
            SortDescending = sortDirection == "ascend" ? false : true,
        };

        /// <summary>次數越多顏色越重，讓「重複數百次」的噪音一眼可辨。</summary>
        private static string GetCountColor(long count) => count switch
        {
            >= 100 => "red",
            >= 10 => "orange",
            _ => "default",
        };

        private static string GetSourceColor(string source) => source switch
        {
            ExceptionSources.Ui => "blue",
            ExceptionSources.WebApi => "purple",
            ExceptionSources.Startup => "gold",
            ExceptionSources.Background => "cyan",
            ExceptionSources.Process => "volcano",
            ExceptionSources.Browser => "geekblue",
            _ => "default",
        };
    }
}
