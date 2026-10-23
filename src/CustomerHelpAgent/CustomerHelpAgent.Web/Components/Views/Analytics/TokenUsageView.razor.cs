using System.Globalization;
using System.Text.Json;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Components.Views.Analytics
{
    public partial class TokenUsageView
    {
        private const string TabByAccount = "account";
        private const string TabByOperation = "operation";
        private const string TabByModel = "model";
        private const string TabByCallKind = "callkind";
        private const string TabDetail = "detail";
        private const string TabDaily = "daily";
        private const string TabTrend = "trend";

        /// <summary>
        /// 趨勢清單沒有分頁，區間太長會讓頁面長到不可用，超過就只保留最後這麼多天。
        /// </summary>
        private const int MaxTrendDays = 180;

        /// <summary>
        /// 三張摘要卡。標題上的天數與 <see cref="TokenUsageRanges"/> 是同一組常數，
        /// 不可以在這裡另外寫死數字 —— 標題說「近 7 天」就必須真的查 7 天。
        /// </summary>
        private static readonly (int Days, string Label)[] RangeCards =
        [
            (TokenUsageRanges.RecentDay, "近 1 天（今天）"),
            (TokenUsageRanges.RecentWeek, "近 7 天"),
            (TokenUsageRanges.RecentMonth, "近 30 天"),
        ];

        private readonly ILogger<TokenUsageView> logger;
        private readonly TokenUsageLogService tokenUsageLogService;
        private readonly AiCallLogService aiCallLogService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private ITable? table;

        private string accountFilter = string.Empty;
        private DateTime? startDate;
        private DateTime? endDate;
        private string selectedOperation = string.Empty;
        private string selectedCallKind = string.Empty;
        private string selectedModel = string.Empty;
        private DateTime? purgeBeforeDate;

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private bool isExportingPdf;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        // 本頁的主題是費用走勢，所以預設停在「趨勢圖」。
        private string activeTabKey = TabTrend;

        private List<TokenUsageLogAdapterModel> rows = [];
        private IEnumerable<TokenUsageLogAdapterModel> selectedRows = [];
        private bool batchDeleting;
        private TokenUsageSummary summary = new();
        private TokenUsageFilterOptions filterOptions = new();

        private List<TokenUsageGroupRow> groupedByAccount = [];
        private List<TokenUsageGroupRow> groupedByOperation = [];
        private List<TokenUsageGroupRow> groupedByModel = [];
        private List<TokenUsageGroupRow> groupedByCallKind = [];

        /// <summary>「每日趨勢」的資料：已依有效區間補零，由舊到新，可直接照順序畫。</summary>
        private List<TokenUsageDailyRow> dailyRows = [];

        /// <summary>「趨勢圖」的點：由 <see cref="dailyRows"/> 分桶而來，不另查資料庫。</summary>
        private List<TokenUsageTrendPoint> trendPoints = [];
        private TokenUsageTrendGrain trendGrain;

        /// <summary>
        /// 近 30 天的逐日資料。近 1／7／30 天彼此是巢狀區間，所以三張摘要卡
        /// 全部由這一份切片加總得出，不必為了卡片各查一次資料庫。
        /// </summary>
        private List<TokenUsageDailyRow> recentDays = [];

        private double maxDailyCostTwd;
        private long maxDailyTotalCount;
        private bool isTrendTruncated;

        private bool detailVisible;

        /// <summary>這筆用量是否還有對應的 AI 對話紀錄（保留天數不同，舊的用量通常已沒有對話可看）。</summary>
        private bool detailHasConversation;
        private TokenUsageLogAdapterModel? detailItem;
        private List<KeyValuePair<string, string>> detailRawRows = [];
        private string detailRawMessage = string.Empty;
        private List<KeyValuePair<string, string>> detailCostRateRows = [];

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ISystemIdentity SystemIdentity { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public IAiQuotaService QuotaService { get; set; } = default!;

        [Inject]
        public Microsoft.Extensions.Options.IOptionsMonitor<AiQuotaSettings> QuotaOptions { get; set; } = default!;

        private IReadOnlyList<AiQuotaLimitStatus> quotaStatus = [];

        /// <summary>「全系統每日：今日 NT$ 12.5／NT$ 100」；沒有上限時寫「不限制」。</summary>
        private static string QuotaText(AiQuotaLimitStatus status)
            => $"{status.Label}：{status.PeriodLabel} NT$ {AiQuotaService.Money(status.UsedTwd)}／{LimitText(status.LimitTwd)}";

        private static string LimitText(int limitTwd) => limitTwd > 0 ? $"NT$ {AiQuotaService.Money(limitTwd)}" : "不限制";

        /// <summary>用量紀錄的維護與匯出要留稽核（LOG-14），只記筆數與格式。</summary>
        private Task WriteAuditAsync(string action, string targetId, string detail)
            => ViewAudit.WriteAsync(AuditLogService, CurrentUserService, action, "TokenUsage", targetId, detail);

        public TokenUsageView(
            ILogger<TokenUsageView> logger,
            TokenUsageLogService tokenUsageLogService,
            AiCallLogService aiCallLogService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.tokenUsageLogService = tokenUsageLogService;
            this.aiCallLogService = aiCallLogService;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        private static string FormatCompact(long value) => TokenUsageFormat.Compact(value);

        private static string FormatCell(int? value) => TokenUsageFormat.Cell(value);

        private static string FormatCostTwdCell(double? value) => TokenUsageFormat.CostTwdCell(value);

        private static string FormatCostUsdCell(double? value) => TokenUsageFormat.CostUsdCell(value);

        private static string FormatCostTwdTotal(double value) => TokenUsageFormat.CostTwdTotal(value);

        private static string FormatCostUsdTotal(double value) => TokenUsageFormat.CostUsdTotal(value);

        private static string FormatExchangeRate(double? value) => TokenUsageFormat.ExchangeRate(value);

        /// <summary>
        /// 明細列「費用」欄的 Tooltip：美金原價、當時匯率、實際套用的費率鍵。
        ///
        /// ⚠️ 標示「前綴比對」是刻意的，而且是對「套錯兄弟模型費率」唯一的實務防線 ——
        /// 費率若是從別的模型名稱推來的，使用者必須看得見。
        /// </summary>
        private static string BuildCostTooltip(TokenUsageLogAdapterModel item)
        {
            var parts = new List<string>
            {
                $"US$ {FormatCostUsdCell(item.CostUsd)}",
            };

            if (item.CostExchangeRate.HasValue)
            {
                parts.Add($"匯率 {FormatExchangeRate(item.CostExchangeRate)}");
            }

            if (string.IsNullOrEmpty(item.CostPriceKey) == false)
            {
                parts.Add(item.IsPrefixMatched
                    ? $"費率 {item.CostPriceKey}（前綴比對）"
                    : $"費率 {item.CostPriceKey}");
            }

            if (item.CostLongContext)
            {
                parts.Add("長脈絡費率");
            }

            return string.Join("　", parts);
        }

        private string DetailAccountText
        {
            get
            {
                if (detailItem is null)
                {
                    return "—";
                }

                var account = string.IsNullOrWhiteSpace(detailItem.Account) ? "（系統自動）" : detailItem.Account;
                return detailItem.UserId is null ? account : $"{account}（UserId={detailItem.UserId}）";
            }
        }

        /// <summary>計價依據。費率若是前綴比對來的，必須在這裡說清楚是從哪個鍵套來的。</summary>
        private string DetailPriceKeyText
        {
            get
            {
                if (detailItem is null || string.IsNullOrEmpty(detailItem.CostPriceKey))
                {
                    return "—";
                }

                return detailItem.IsPrefixMatched
                    ? $"{detailItem.CostPriceKey}（以前綴比對自 {detailItem.Model}）"
                    : detailItem.CostPriceKey;
            }
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
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何用量資訊。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/token-usage");
                logger.LogWarning("Usage page denied because the current user is not an administrator.");
                return;
            }

            filterOptions = await tokenUsageLogService.GetFilterOptionsAsync();
            await ReloadAsync();
        }

        private TokenUsageQuery BuildQuery(int pageSize) => new()
        {
            StartDate = startDate,
            EndDate = endDate,
            Account = accountFilter,
            Operation = selectedOperation,
            CallKind = selectedCallKind,
            Model = selectedModel,
            CurrentPage = _pageIndex,
            PageSize = pageSize,
            SortField = sortField,
            SortDescending = sortDirection switch
            {
                "descend" => true,
                "ascend" => false,
                _ => null,
            },
        };

        private async Task ReloadAsync()
        {
            // 換頁、排序、查詢與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = BuildQuery(_pageSize);

                var page = await tokenUsageLogService.GetAsync(query);
                rows = page.Result.ToList();
                _total = page.Count;

                summary = await tokenUsageLogService.GetSummaryAsync(query);
                quotaStatus = await QuotaService.GetStatusAsync(null);

                groupedByAccount = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Account);
                groupedByOperation = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Operation);
                groupedByModel = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Model);
                groupedByCallKind = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.CallKind);

                await LoadRangeCardsAsync();
                await LoadTrendAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load LLM usage records.");
                ViewNotification.UnexpectedError(notificationService, $"載入用量紀錄失敗：{ex.GetType().Name}。", ex);
                rows = [];
                _total = 0;
                summary = new TokenUsageSummary();
                recentDays = [];
                ClearTrend();
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 沿用目前的帳號／作業／型別／模型篩選，只把日期換成指定的窗。
        /// 「條件照舊、只換日期」是摘要卡與主查詢唯一的差別，所以這裡刻意不碰其他欄位。
        /// </summary>
        private TokenUsageQuery BuildQueryForRange(DateTime start, DateTime end) => new()
        {
            StartDate = start,
            EndDate = end,
            Account = accountFilter,
            Operation = selectedOperation,
            CallKind = selectedCallKind,
            Model = selectedModel,
        };

        /// <summary>
        /// 近 1／7／30 天彼此是巢狀區間，因此只查一次「近 30 天」，
        /// 三張卡再從同一份日列切片加總。這樣一次載入只多一次資料庫往返。
        /// </summary>
        private async Task LoadRangeCardsAsync()
        {
            var (start, end) = TokenUsageRanges.Recent(DateTime.Today, TokenUsageRanges.RecentMonth);
            recentDays = await tokenUsageLogService.GetDailyAsync(BuildQueryForRange(start, end));
        }

        /// <summary>
        /// 「每日趨勢」。區間跟隨篩選；起訖日都沒填時退回近 30 天 ——
        /// 不設限的話，資料放久了會一次畫出上百格。
        /// </summary>
        private async Task LoadTrendAsync()
        {
            var isUnbounded = startDate is null && endDate is null;
            var (windowStart, windowEnd) = TokenUsageRanges.Recent(DateTime.Today, TokenUsageRanges.RecentMonth);

            var actual = await tokenUsageLogService.GetDailyAsync(
                isUnbounded ? BuildQueryForRange(windowStart, windowEnd) : BuildQuery(_pageSize));

            DateTime start;
            DateTime end;

            if (isUnbounded)
            {
                (start, end) = (windowStart, windowEnd);
            }
            else
            {
                // 只填了一端時，另一端交給資料自己決定：沒有資料的日子沒有格子可畫。
                end = endDate?.Date ?? DateTime.Today;
                start = startDate?.Date ?? (actual.Count > 0 ? actual[0].Date : end);
            }

            // 使用者可以把結束日選在起始日之前，這裡夾一下，否則下面的迴圈永遠不會執行。
            if (end < start)
            {
                end = start;
            }

            isTrendTruncated = (end - start).Days + 1 > MaxTrendDays;
            if (isTrendTruncated)
            {
                start = end.AddDays(-(MaxTrendDays - 1));
            }

            dailyRows = FillMissingDays(actual, start, end);
            maxDailyCostTwd = dailyRows.Count == 0 ? 0 : dailyRows.Max(x => x.CostTwd);
            maxDailyTotalCount = dailyRows.Count == 0 ? 0 : dailyRows.Max(x => x.TotalCount);

            trendGrain = TokenUsageTrendChart.GrainFor(dailyRows.Count);
            trendPoints = TokenUsageTrendChart.Bucket(dailyRows);
        }

        private void ClearTrend()
        {
            dailyRows = [];
            trendPoints = [];
            maxDailyCostTwd = 0;
            maxDailyTotalCount = 0;
            isTrendTruncated = false;
        }

        /// <summary>
        /// 補上沒有資料的日期。缺口不補的話，長條圖會把「那天沒花錢」畫成「那天不存在」，
        /// 相鄰的兩根柱子看起來就成了連續的兩天。
        /// </summary>
        private static List<TokenUsageDailyRow> FillMissingDays(
            List<TokenUsageDailyRow> actual,
            DateTime start,
            DateTime end)
        {
            var byDate = actual.ToDictionary(x => x.Date);
            var result = new List<TokenUsageDailyRow>();

            for (var day = start; day <= end; day = day.AddDays(1))
            {
                result.Add(byDate.TryGetValue(day, out var row) ? row : new TokenUsageDailyRow { Date = day });
            }

            return result;
        }

        /// <summary>近 N 天的摘要卡。純記憶體切片，不再查資料庫。</summary>
        private TokenUsageDailyRow CardOf(int days)
        {
            var (start, _) = TokenUsageRanges.Recent(DateTime.Today, days);
            var slice = recentDays.Where(x => x.Date >= start).ToList();

            return new TokenUsageDailyRow
            {
                Date = start,
                TotalCount = slice.Sum(x => x.TotalCount),
                CostUsd = slice.Sum(x => x.CostUsd),
                CostTwd = slice.Sum(x => x.CostTwd),
                UnpricedCount = slice.Sum(x => x.UnpricedCount),
                CallCount = slice.Sum(x => x.CallCount),
            };
        }

        /// <summary>點卡片＝把該區間套進起訖日再重查，其餘篩選條件維持不動。</summary>
        private async Task OnApplyRangeAsync(int days)
        {
            var (start, end) = TokenUsageRanges.Recent(DateTime.Today, days);
            startDate = start;
            endDate = end;
            _pageIndex = 1;
            await ReloadAsync();
        }

        private bool IsRangeActive(int days)
        {
            var (start, end) = TokenUsageRanges.Recent(DateTime.Today, days);
            return startDate?.Date == start && endDate?.Date == end;
        }

        /// <summary>
        /// 上排「篩選範圍合計」那行字：<b>含日期</b>。
        ///
        /// ⚠️ 與 <see cref="ActiveFilterText"/> 的差別就在日期。上排統計的是完整篩選範圍，
        /// 下排的近 N 天卡則會把日期換成自己的窗 —— 兩排都寫「套用條件」卻指不同範圍會誤導，
        /// 所以兩份字串必須看得出差別。
        /// </summary>
        private string FilterScopeText => BuildFilterText(includeDates: true);

        /// <summary>下排卡片上方那行字：目前<b>除了日期以外</b>還套了哪些條件。</summary>
        private string ActiveFilterText => BuildFilterText(includeDates: false);

        /// <summary>
        /// 兩排共用的條件字串組法。抽出來是為了不讓上下排各留一份、日後各自漂移。
        /// </summary>
        private string BuildFilterText(bool includeDates)
        {
            var parts = new List<string>();

            if (includeDates)
            {
                // 兩端都沒設時也要寫出來，否則看不出這排「含日期」而下排不含。
                var start = startDate?.ToString("yyyy-MM-dd") ?? "不限";
                var end = endDate?.ToString("yyyy-MM-dd") ?? "不限";
                parts.Add($"日期 {start} ～ {end}");
            }

            if (string.IsNullOrWhiteSpace(accountFilter) == false)
            {
                parts.Add($"帳號含「{accountFilter}」");
            }

            if (string.IsNullOrEmpty(selectedOperation) == false)
            {
                parts.Add($"作業：{selectedOperation}");
            }

            if (string.IsNullOrEmpty(selectedCallKind) == false)
            {
                parts.Add($"型別：{selectedCallKind}");
            }

            if (string.IsNullOrEmpty(selectedModel) == false)
            {
                parts.Add($"模型：{selectedModel}");
            }

            return parts.Count == 0 ? "全部條件" : string.Join("、", parts);
        }

        /// <summary>
        /// 長條寬度。
        ///
        /// ⚠️ 一定要 <see cref="CultureInfo.InvariantCulture"/> —— CSS 的百分比不接受
        /// 逗號小數點，在以逗號為小數點的地區整條長條會消失（而且不會有任何錯誤訊息）。
        /// </summary>
        private static string BarWidth(double value, double max)
            => max <= 0 ? "0%" : string.Create(CultureInfo.InvariantCulture, $"{value / max * 100:F1}%");

        // 趨勢圖的 viewBox 與繪圖區（SVG 使用者座標）。左邊留給 Y 軸刻度、下面留給日期。
        private const int ChartWidth = 1000;
        private const int ChartHeight = 200;
        private const double PlotLeft = 72;
        private const double PlotRight = ChartWidth - 16;
        private const double PlotTop = 12;
        private const double PlotHeight = ChartHeight - PlotTop - 30;
        private const double PlotWidth = PlotRight - PlotLeft;

        /// <summary>
        /// 上下兩張圖：費用與合計 token。兩者單位不同，所以各自一張、各自一個 Y 軸，
        /// 不做雙 Y 軸 —— 雙軸圖上兩條線的交叉與相對高低沒有意義，只會誤導。
        /// </summary>
        private IEnumerable<(string Title, string CssClass, IReadOnlyList<double> Values, Func<double, string> FormatTick)> TrendCharts =>
        [
            ("費用（NT$）", "token-usage-chart-cost", trendPoints.Select(x => x.CostTwd).ToList(), FormatCostTwdTotal),
            ("合計 token", "token-usage-chart-count", trendPoints.Select(x => (double)x.TotalCount).ToList(), v => FormatCompact((long)v)),
        ];

        private static string TrendTooltip(TokenUsageTrendPoint point)
        {
            var text = $"{TokenUsageTrendChart.PeriodLabel(point)}\n"
                + $"費用 NT$ {FormatCostTwdTotal(point.CostTwd)}（US$ {FormatCostUsdTotal(point.CostUsd)}）\n"
                + $"合計 {FormatCompact(point.TotalCount)} token\n"
                + $"呼叫 {point.CallCount:N0} 次";

            return point.UnpricedCount > 0 ? $"{text}\n其中 {point.UnpricedCount:N0} 筆未定價，未計入費用" : text;
        }

        private Task OnTabChangedAsync(string key)
        {
            activeTabKey = key;
            return Task.CompletedTask;
        }

        private async Task OnTableChange(QueryModel<TokenUsageLogAdapterModel> args)
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
            logger.LogInformation("Usage search triggered.");
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.TokenUsage.Search, "TokenUsage",
                AuditSearchCondition.Of("start", startDate), AuditSearchCondition.Of("end", endDate),
                AuditSearchCondition.Text("account", accountFilter), AuditSearchCondition.Of("operation", selectedOperation),
                AuditSearchCondition.Of("callKind", selectedCallKind), AuditSearchCondition.Of("model", selectedModel));
        }

        private async Task OnRefreshAsync()
        {
            filterOptions = await tokenUsageLogService.GetFilterOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>開窗當下才讀原始 JSON 檔，清單查詢不會一次把所有檔案讀進記憶體。</summary>
        private async Task OnViewAsync(TokenUsageLogAdapterModel item)
        {
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.TokenUsage.View, "TokenUsage", item.Id.ToString(), null);
            detailItem = item;
            detailRawRows = [];
            detailRawMessage = "讀取中…";
            // 費率快照就在資料列上，不必再讀檔；沿用同一支平攤器讓呈現與原始明細一致。
            detailCostRateRows = string.IsNullOrWhiteSpace(item.CostRateSnapshot)
                ? []
                : FlattenJson(item.CostRateSnapshot);
            detailHasConversation = false;
            detailVisible = true;
            StateHasChanged();

            try
            {
                detailHasConversation = item.CallId is { } callId && await aiCallLogService.ExistsAsync(callId);
            }
            catch (Exception ex)
            {
                // 查不到對話只是少一顆按鈕，不可以讓整個用量明細打不開。
                logger.LogError(ex, "Failed to check AI call log for LLM usage record. UsageId={UsageId}", item.Id);
                detailHasConversation = false;
            }

            var raw = await tokenUsageLogService.GetRawUsageAsync(item.Id);
            if (string.IsNullOrWhiteSpace(raw))
            {
                detailRawMessage = "原始明細不存在（可能已被清除，或當初這次呼叫沒有回傳用量）。";
            }
            else
            {
                detailRawRows = FlattenJson(raw);
                detailRawMessage = detailRawRows.Count == 0 ? "原始明細無法解析。" : string.Empty;
            }

            StateHasChanged();
        }

        /// <summary>到 AI 對話紀錄頁開啟這次呼叫的完整對話（0.9.72 起，以 CallId 關聯）。</summary>
        private void OnViewConversation()
        {
            if (detailItem?.CallId is { } callId)
            {
                NavigationManager.NavigateTo($"/ai-call-logs?callId={callId:D}");
            }
        }

        /// <summary>
        /// 把 usage JSON 平攤成「路徑 → 值」逐列顯示，例如
        /// <c>prompt_tokens_details.cached_tokens</c>。
        /// 供應商日後新增欄位時不用改程式就看得到。
        /// </summary>
        private static List<KeyValuePair<string, string>> FlattenJson(string json)
        {
            var result = new List<KeyValuePair<string, string>>();

            try
            {
                using var document = JsonDocument.Parse(json);
                Walk(document.RootElement, string.Empty, result);
            }
            catch (JsonException)
            {
                // 檔案內容不是合法 JSON（理論上不會發生），交給呼叫端顯示友善訊息。
            }

            return result;

            static void Walk(JsonElement element, string prefix, List<KeyValuePair<string, string>> output)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        var name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                        Walk(property.Value, name, output);
                    }

                    return;
                }

                var text = element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt64(out var number)
                        ? TokenUsageFormat.Compact(number)
                        : element.ToString(),
                    JsonValueKind.Null => "—",
                    _ => element.ToString(),
                };

                output.Add(new KeyValuePair<string, string>(prefix, text));
            }
        }

        private async Task OnDeleteAsync(TokenUsageLogAdapterModel item)
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "刪除用量紀錄",
                "確定要刪除這一筆嗎？原始明細檔也會一併移除。",
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

        /// <summary>刪除一筆並記稽核；單筆刪除與「刪除選取」共用。</summary>
        private async Task<VerifyRecordResult> DeleteOneAsync(TokenUsageLogAdapterModel item)
        {
            var result = await tokenUsageLogService.DeleteAsync(item.Id);
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.TokenUsage.Delete, item.Id.ToString(), "刪除一筆用量紀錄");
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
                        note: "原始明細檔也會一併移除。"))
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
                logger.LogError(ex, "Unhandled exception while batch deleting token usage logs.");
                ViewNotification.UnexpectedError(notificationService, "刪除用量紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }

            static string Describe(TokenUsageLogAdapterModel x) => $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss} {x.Operation}";
        }

        private async Task OnPurgeAsync()
        {
            if (purgeBeforeDate is null)
            {
                return;
            }

            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "批次清除",
                $"將刪除 {purgeBeforeDate.Value:yyyy-MM-dd} 之前的所有用量紀錄與原始明細檔。此動作無法復原。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await tokenUsageLogService.PurgeBeforeAsync(purgeBeforeDate.Value);
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.TokenUsage.Purge, "*", $"清除 {purgeBeforeDate.Value:yyyy-MM-dd} 之前的用量紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, result.Message);
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
                "清空全部用量紀錄",
                "將刪除所有紀錄與整個原始明細目錄。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await tokenUsageLogService.ClearAllAsync();
            if (result.Success)
            {
                await WriteAuditAsync(AuditActions.TokenUsage.ClearAll, "*", $"清空全部用量紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, result.Message);
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        internal static readonly IReadOnlyList<ExportColumn<TokenUsageLogAdapterModel>> CsvColumns =
        [
            new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("使用者", x => x.Account ?? "（系統自動）"),
            new("作業", x => x.Operation),
            new("型別", x => x.CallKind),
            new("供應商", x => x.Provider),
            new("模型", x => x.Model),
            new("輸入", x => x.InputCount?.ToString()),
            new("輸出", x => x.OutputCount?.ToString()),
            new("推理", x => x.ReasoningCount?.ToString()),
            new("快取", x => x.CachedInputCount?.ToString()),
            new("圖片輸入", x => x.ImageInputCount?.ToString()),
            new("圖片快取", x => x.ImageCachedInputCount?.ToString()),
            new("圖片輸出", x => x.ImageOutputCount?.ToString()),
            new("合計", x => x.TotalCount?.ToString()),
            new("費用USD", x => CsvCost(x.CostUsd, "F6")),
            new("費用TWD", x => CsvCost(x.CostTwd, "F4")),
            new("匯率", x => CsvCost(x.CostExchangeRate, "F4")),
            new("計價依據", x => x.CostPriceKey),
            new("長脈絡", x => x.IsUnpriced ? null : (x.CostLongContext ? "是" : "否")),
            new("字元數", x => x.CharacterCount?.ToString()),
            new("音訊時長秒", x => x.DurationSeconds?.ToString()),
            new("耗時ms", x => x.ElapsedMilliseconds.ToString()),
            new("結果", x => x.Success ? "成功" : "失敗"),
            new("失敗原因", x => x.FailureReason),
        ];

        private async Task OnExportCsvAsync()
        {
            try
            {
                // 匯出目前篩選條件下的全部資料，而非只有當頁。
                var query = BuildQuery(int.MaxValue);
                query.CurrentPage = 1;
                var all = await tokenUsageLogService.GetAsync(query);

                // 0.9.107 起經共用的 TabularExport（BOM、跳脫、換行與之前手寫的相同，ExportCsvCompatibilityTests 守門）。
                var bytes = TabularExport.ToCsv(CsvColumns, all.Result);
                var fileName = $"CustomerHelpAgent.Web-llm-usage-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.DownloadAsync(fileName, bytes, TabularExport.CsvContentType);

                logger.LogInformation("Usage export downloaded. Rows={Rows}", all.Count);
                await WriteAuditAsync(AuditActions.TokenUsage.Export, "*", $"format=csv; rows={all.Count}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage CSV export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>目前選中的頁籤對應的報表範圍。</summary>
        private TokenUsageReportScope ActiveScope => activeTabKey switch
        {
            TabTrend => TokenUsageReportScope.Trend,
            TabDaily => TokenUsageReportScope.Daily,
            TabByAccount => TokenUsageReportScope.Account,
            TabByOperation => TokenUsageReportScope.Operation,
            TabByModel => TokenUsageReportScope.Model,
            TabByCallKind => TokenUsageReportScope.CallKind,
            _ => TokenUsageReportScope.Detail,
        };

        /// <summary>按鈕提示用的頁籤名稱，與報表標題共用同一份對照表。</summary>
        private string ActiveTabLabel => TokenUsageReportPdfBuilder.DescribeScope(ActiveScope);

        /// <summary>
        /// 匯出 PDF。<paramref name="scope"/> 為 <see cref="TokenUsageReportScope.All"/> 時是整份報表，
        /// 否則只輸出目前這一個頁籤（標題、條件區與合計仍然保留）。
        ///
        /// 趨勢與四張分組表直接送畫面上現成的資料，<b>不重新查資料庫</b> ——
        /// 報表看到的就是畫面當下看到的。
        /// </summary>
        private async Task OnExportPdfAsync(TokenUsageReportScope scope)
        {
            isExportingPdf = true;
            StateHasChanged();

            try
            {
                // 明細是唯一要重查的區塊（畫面上是分頁的）。匯出「依模型」之類的頁籤時
                // 不該為了一份用不到的明細去掃全表。
                var needsDetails = scope is TokenUsageReportScope.All or TokenUsageReportScope.Detail;
                var details = new List<TokenUsageLogAdapterModel>();

                if (needsDetails)
                {
                    var query = BuildQuery(int.MaxValue);
                    query.CurrentPage = 1;
                    details = [.. (await tokenUsageLogService.GetAsync(query)).Result];
                }

                var information = SystemIdentity;
                var bytes = TokenUsageReportPdfBuilder.Build(new TokenUsageReportRequest
                {
                    SystemName = information.Name,
                    SystemVersion = information.Version,
                    OperatorAccount = CurrentUserService.CurrentUser.Account ?? string.Empty,
                    GeneratedAt = DateTime.Now,
                    StartDate = startDate,
                    EndDate = endDate,
                    Account = accountFilter,
                    Operation = selectedOperation,
                    CallKind = selectedCallKind,
                    Model = selectedModel,
                    Summary = summary,
                    ByAccount = groupedByAccount,
                    ByOperation = groupedByOperation,
                    ByModel = groupedByModel,
                    ByCallKind = groupedByCallKind,
                    Details = details,
                    // 畫面現成的趨勢（已補零、已套 180 天上限），PDF 不重算也不重查。
                    Daily = dailyRows,
                    Scope = scope,
                });

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                // 整份維持原本的檔名；單頁籤才加上頁籤代碼，免得一次匯好幾個頁籤時分不出誰是誰。
                var slug = scope is TokenUsageReportScope.All ? string.Empty : $"{activeTabKey}-";
                var fileName = $"CustomerHelpAgent.Web-llm-usage-{slug}{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
                await JSRuntime.InvokeVoidAsync(
                    "appFileDownload.downloadFromStream", fileName, streamReference, "application/pdf");

                logger.LogInformation(
                    "Usage PDF exported. Scope={Scope}, Bytes={Bytes}, Rows={Rows}",
                    scope,
                    bytes.Length,
                    details.Count);
                await WriteAuditAsync(AuditActions.TokenUsage.Export, "*", $"format=pdf; scope={scope}; rows={details.Count}");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Usage PDF export failed because the embedded font is missing.");
                ViewNotification.UnexpectedError(notificationService, "PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。", ex);
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "Usage PDF export aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExportingPdf = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// CSV 的金額欄。
        ///
        /// ⚠️ 一律用 F 格式 + InvariantCulture，<b>不能用 N</b>：N 會插千分位逗號，
        /// Excel 會把 "1,234.5678" 當文字匯入，使用者做 SUM 得到 0。
        ///
        /// ⚠️ 未定價輸出空字串而不是 0，否則試算表加總會靜默少算。
        /// </summary>
        private static string? CsvCost(double? value, string format)
            => value?.ToString(format, CultureInfo.InvariantCulture);
    }
}
