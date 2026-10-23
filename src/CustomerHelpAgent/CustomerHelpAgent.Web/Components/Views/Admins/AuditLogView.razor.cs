using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Web.Export;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    public partial class AuditLogView
    {
        /// <summary>
        /// 「清除很久以前的紀錄」門檻，讀 <see cref="LogRetentionSettings.AuditLogDays"/>（0.9.78 起與自動清理同一個設定，LOG-13）。
        /// 停用自動清理（0）時退回預設 365 天。
        ///
        /// 預設 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，
        /// 稽核軌跡清的是責任證據，保存期預期以「年」為單位。
        /// </summary>
        private int PurgeDays => RetentionOptions.CurrentValue.ManualAuditLogDays;

        [Inject]
        public Microsoft.Extensions.Options.IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

        private readonly ILogger<AuditLogView> logger;
        private readonly AuditLogQueryService auditLogQueryService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private ITable? table;

        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedAction = string.Empty;
        private string selectedSuccess = string.Empty;
        private string accountFilter = string.Empty;
        private string ipFilter = string.Empty;
        private string keyword = string.Empty;

        private List<string> actionOptions = [];

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private List<AuditLogAdapterModel> auditLogAdapterModels = [];

        private bool detailVisible;
        private AuditLogAdapterModel? detailItem;
        /// <summary>明細窗的「詳細內容」（修改差異、查詢條件；0.9.131 起）。null 表示這筆沒有內容檔。</summary>
        private string? detailContent;
        private bool isContentLoading;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        public AuditLogView(
            ILogger<AuditLogView> logger,
            AuditLogQueryService auditLogQueryService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.auditLogQueryService = auditLogQueryService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.modalService = modalService;
            this.notificationService = notificationService;
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
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何稽核內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/audit-logs");
                logger.LogWarning("Audit log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>
        /// 重新載入動作代碼下拉選項。清除／清空之後也要重跑：
        /// 某個代碼的最後一筆被刪掉時，選項就該跟著消失，否則會留下「選了查無資料」的死選項。
        /// </summary>
        private async Task ReloadActionOptionsAsync()
        {
            try
            {
                actionOptions = await auditLogQueryService.GetDistinctActionsAsync();

                // 目前選取的代碼若已不存在，退回「不限」，避免畫面停在查不到東西的狀態。
                if (string.IsNullOrEmpty(selectedAction) == false
                    && actionOptions.Contains(selectedAction) == false)
                {
                    selectedAction = string.Empty;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load audit log action options.");
                actionOptions = [];
            }
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = BuildQuery(_pageIndex, _pageSize);

                var result = await auditLogQueryService.GetAsync(query);
                auditLogAdapterModels = result.Result.ToList();
                _total = result.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load audit logs.");
                ViewNotification.UnexpectedError(notificationService, $"載入稽核紀錄失敗：{ex.GetType().Name}。", ex);
                auditLogAdapterModels = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 由目前的工具列狀態組出查詢條件。清單與匯出共用，避免兩邊條件默默分歧
        /// （匯出的是「目前畫面條件下的全部資料」，只有分頁不同）。
        /// </summary>
        private AuditLogQuery BuildQuery(int currentPage, int pageSize) => new()
        {
            StartTime = startTime,
            EndTime = endTime,
            Account = accountFilter,
            ClientIp = ipFilter,
            Action = selectedAction,
            Success = selectedSuccess switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Keyword = keyword,
            CurrentPage = currentPage,
            PageSize = pageSize,
            SortField = sortField,
            SortDescending = sortDirection switch
            {
                "descend" => true,
                "ascend" => false,
                _ => null,
            },
        };

        private async Task OnTableChange(QueryModel<AuditLogAdapterModel> args)
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
            logger.LogInformation("Audit log search triggered.");
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Audit.Search, "AuditLog",
                AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
                AuditSearchCondition.Of("action", selectedAction), AuditSearchCondition.Of("success", selectedSuccess),
                AuditSearchCondition.Text("account", accountFilter), AuditSearchCondition.Text("ip", ipFilter),
                AuditSearchCondition.Text("keyword", keyword));
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Audit log refresh triggered.");
            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>
        /// 先開窗、再讀詳細內容檔（0.9.131 起）：清單查詢不讀檔，檔案可能很大，只在看明細時才讀。
        /// </summary>
        private async Task OnViewAsync(AuditLogAdapterModel item)
        {
            detailItem = item;
            detailContent = null;
            isContentLoading = item.ContentFile is not null;
            detailVisible = true;
            StateHasChanged();

            if (item.ContentFile is not null)
            {
                try
                {
                    detailContent = await auditLogQueryService.GetContentAsync(item.Id)
                        ?? "內容檔不存在（可能已被清除，或當初寫檔失敗）。";
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", item.Id);
                    detailContent = "讀取詳細內容失敗。" + TraceCode.Suffix(TraceCode.Current);
                }
                finally
                {
                    isContentLoading = false;
                }
            }

            await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Audit.View, "AuditLog", item.Id.ToString(), $"action={item.Action}");
        }

        private async Task OnPurgeAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                $"清除 {PurgeDays} 天前的紀錄",
                $"將刪除發生時間早於 {PurgeDays} 天前的所有稽核紀錄。此動作無法復原，"
                    + "且本次清除會另外留下一筆稽核紀錄。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await auditLogQueryService.PurgeAsync(PurgeDays);
            if (result.Success)
            {
                await WriteSelfAuditAsync(AuditActions.Audit.Purge, $"清除 {PurgeDays} 天前的稽核紀錄：{result.Message}");
                ViewNotification.Warning(
                    notificationService,
                    string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadActionOptionsAsync();
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
                "清空全部稽核紀錄",
                "將刪除所有稽核紀錄。此動作無法復原，且本次清空會另外留下一筆稽核紀錄。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await auditLogQueryService.ClearAllAsync();
            if (result.Success)
            {
                await WriteSelfAuditAsync(AuditActions.Audit.ClearAll, $"清空全部稽核紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, "已清空全部稽核紀錄");
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>
        /// 把「清除／清空稽核紀錄」這件事本身寫成一筆稽核紀錄。
        ///
        /// ⚠️ 這是刻意的：稽核軌跡可被管理員一鍵抹除，會讓「誰抹除了稽核紀錄」成為
        /// 系統裡唯一查不到的事。寫入排在刪除之後，所以這一筆會留在清空後的資料表裡。
        ///
        /// 稽核寫入失敗不應推翻「已經刪除」這個事實，因此只記錯誤、不向使用者報錯。
        /// </summary>
        private async Task WriteSelfAuditAsync(string action, string detail)
        {
            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "AuditLog",
                    targetId: "*",
                    detail: detail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for audit maintenance. Action={Action}", action);
            }
        }

        private async Task OnExportAsync()
        {
            try
            {
                // 匯出目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照。
                var query = BuildQuery(1, AuditLogQueryService.MaxExportRows);
                var result = await auditLogQueryService.GetAsync(query);

                // 0.9.107 起經共用的 TabularExport（BOM、跳脫、換行與之前手寫的相同，ExportCsvCompatibilityTests 守門）。
                var bytes = TabularExport.ToCsv(CsvColumns, result.Result);
                var fileName = $"CustomerHelpAgent.Web-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.DownloadAsync(fileName, bytes, TabularExport.CsvContentType);

                logger.LogInformation("Audit log export downloaded. Rows={Rows}", result.Count);
                await WriteSelfAuditAsync(AuditActions.Audit.Export, $"format=csv; rows={result.Count}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit log export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>CSV 欄位。標頭明講「本地」：稽核檔常被帶出系統比對，讀檔的人不會知道資料庫存的是 UTC。</summary>
        internal static readonly IReadOnlyList<ExportColumn<AuditLogAdapterModel>> CsvColumns =
        [
            new("發生時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("結果", x => x.Success ? "成功" : "失敗"),
            new("動作", x => x.Action),
            new("操作者帳號", x => x.ActorAccount),
            new("操作者Id", x => x.ActorUserId?.ToString()),
            new("目標類型", x => x.TargetType),
            new("目標識別", x => x.TargetId),
            new("摘要", x => x.Detail),
            // 0.9.117 起：放在最後，不打亂既有依欄位位置讀檔的下游。
            new("來源IP", x => x.ClientIp),
        ];

        /// <summary>操作者顯示文字；系統或匿名事件（例如帳號不存在的登入失敗）沒有 Id。</summary>
        private static string ActorText(AuditLogAdapterModel item)
        {
            var account = string.IsNullOrWhiteSpace(item.ActorAccount) ? "（系統／匿名）" : item.ActorAccount;
            return item.ActorUserId is null ? account : $"{account}（Id={item.ActorUserId}）";
        }

        /// <summary>目標顯示文字，型別＋識別；兩者皆空顯示破折號。</summary>
        private static string TargetText(AuditLogAdapterModel item)
        {
            if (string.IsNullOrWhiteSpace(item.TargetType) && string.IsNullOrWhiteSpace(item.TargetId))
            {
                return "—";
            }

            var type = string.IsNullOrWhiteSpace(item.TargetType) ? "—" : item.TargetType;
            return string.IsNullOrWhiteSpace(item.TargetId) ? type : $"{type}#{item.TargetId}";
        }

        /// <summary>
        /// 依動作的第一節上色，讓「登入」「帳號異動」「權限拒絕」三類一眼分得開。
        /// 用 AntDesign Tag 的具名顏色，不寫色碼字面值（ThemeConventionTests 守門）。
        /// </summary>
        private static string GetActionColor(string category) => category switch
        {
            "Login" => "blue",
            "User" => "green",
            "Role" => "purple",
            "Permission" => "red",
            "Audit" => "volcano",
            "Logout" => "blue",
            "Token" => "red",
            "Project" => "geekblue",
            "Category" => "lime",
            "Team" => "gold",
            "ExceptionLog" => "magenta",
            "AiCallLog" => "magenta",
            "TokenUsage" => "magenta",
            "LogLevel" => "cyan",
            "LogViewer" => "cyan",
            "Email" => "cyan",
            "Password" => "orange",
            "Job" => "cyan",
            "SystemParameter" => "cyan",
            "Backup" => "orange",
            "Announcement" => "cyan",
            "Notification" => "cyan",
            // 開頁（0.9.131 起）筆數最多，刻意用灰色，不搶增刪改的視線。
            "Page" => "default",
            _ => "default",
        };
    }
}
