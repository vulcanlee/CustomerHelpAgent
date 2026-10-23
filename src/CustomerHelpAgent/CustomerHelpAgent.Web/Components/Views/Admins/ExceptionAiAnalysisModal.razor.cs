using System.Globalization;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// AI 例外分析對話窗：第一次送出例外明細取得分析報告，之後可在同一個視窗內多輪追問。
    ///
    /// 等待、取消、失敗與渲染安全的處理方式照抄日誌檢視頁的 AI 分析（見速查 §6.4、§6.5），
    /// 差別只在這裡是一段對話而不是單一份報告。
    /// </summary>
    public partial class ExceptionAiAnalysisModal : IDisposable
    {
        private enum ModalState
        {
            /// <summary>第一次分析進行中，顯示整窗等待畫面。</summary>
            Running,

            /// <summary>第一次分析失敗，原因留在窗內。</summary>
            Failed,

            /// <summary>已有報告，可追問（追問進行中也是這個狀態，由 isRunning 區分）。</summary>
            Chatting,
        }

        private enum EntryKind
        {
            User,
            Assistant,

            /// <summary>追問失敗的訊息。只顯示在畫面上，不進入送給 AI 的對話。</summary>
            Error,
        }

        /// <param name="Round">0 代表第一次分析報告；1 起為第 N 輪追問與其回覆。</param>
        private sealed record ChatEntry(EntryKind Kind, int Round, string Text, string Html);

        /// <summary>稽核只記數量，不記內容（見 <see cref="WriteAuditAsync"/>）。</summary>
        private sealed record AuditTarget(int? ExceptionLogId, int Round, int MessageCount, int CharacterCount);

        private static readonly (int Percent, string Label)[] FontScaleOptions =
        [
            (100, "一般"),
            (125, "中"),
            (150, "大"),
        ];

        /// <summary>
        /// 單則 AI 回覆轉成 HTML 之後的顯示上限（同日誌檢視頁）。
        /// MaxOutputTokens 預設不送，所以這裡是防止異常龐大回應拖垮畫面的主要防線。
        /// </summary>
        private const int MaxRenderedHtmlLength = 512 * 1024;

        /// <summary>單則追問的字數上限。追問是給人打的問題，不是貼整份檔案的地方。</summary>
        private const int MaxFollowUpLength = 2000;

        private const int DefaultFontScalePercent = 100;

        private readonly ILogger<ExceptionAiAnalysisModal> logger;
        private readonly IAiExceptionAnalysisService aiExceptionAnalysisService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;

        // ⚠️ 本窗的提示一律走 ViewNotification（右下角卡片），理由同日誌檢視頁：訊息偏長。
        private readonly NotificationService notificationService;

        private bool visible;
        private ModalState modalState = ModalState.Chatting;
        private bool isRunning;
        private bool isExporting;
        private int elapsedSeconds;
        private int fontScalePercent = DefaultFontScalePercent;
        private string errorMessage = string.Empty;
        private string followUpText = string.Empty;

        private ExceptionLogAdapterModel? item;

        /// <summary>
        /// 這段對話在 AI 對話紀錄裡的識別碼（0.9.72 起）：開窗時產生，第一次分析與每輪追問共用，
        /// 對話紀錄頁才能把它們串成同一段。服務本身無狀態，所以由持有對話的這個窗來產生。
        /// </summary>
        private Guid conversationId;

        /// <summary>原始堆疊。null 代表堆疊檔不存在（與明細窗上的提示文字區分開）。</summary>
        private string? stackTrace;

        /// <summary>送給 AI 的對話（不含 system）。第一則是例外明細。</summary>
        private readonly List<AiChatMessage> conversation = [];

        /// <summary>畫面上的對話泡泡（不含第一則例外明細，含追問失敗訊息）。</summary>
        private readonly List<ChatEntry> entries = [];

        private readonly List<AiTokenUsage?> usages = [];
        private string modelName = string.Empty;
        private TimeSpan totalElapsed;

        private ElementReference latestAnchor;
        private bool scrollToLatestPending;

        /// <summary>本次呼叫的取消來源。使用者在等待中關窗即取消，等待計時也綁在同一個 token 上。</summary>
        private CancellationTokenSource? cts;

        /// <summary>元件已釋放（使用者導航離開）。之後不可再碰任何 scoped 服務。</summary>
        private bool isDisposed;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ISystemIdentity SystemIdentity { get; set; } = default!;

        public ExceptionAiAnalysisModal(
            ILogger<ExceptionAiAnalysisModal> logger,
            IAiExceptionAnalysisService aiExceptionAnalysisService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.aiExceptionAnalysisService = aiExceptionAnalysisService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.notificationService = notificationService;
        }

        private string FontScaleCss
            => (fontScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

        private int MaxFollowUpRounds => aiExceptionAnalysisService.MaxFollowUpRounds;

        /// <summary>已成功送出的追問輪數（失敗的追問已從對話移除，不算）。</summary>
        private int FollowUpCount => conversation.Count(message => message.Role == AiChatRoles.User) - 1;

        private bool IsFollowUpLimitReached => FollowUpCount >= MaxFollowUpRounds;

        private List<KeyValuePair<string, string>> MetaItems
        {
            get
            {
                var items = new List<KeyValuePair<string, string>>();
                if (item is not null)
                {
                    items.Add(new KeyValuePair<string, string>("例外類型", item.ExceptionType));
                }

                if (string.IsNullOrWhiteSpace(modelName) == false)
                {
                    items.Add(new KeyValuePair<string, string>("使用模型", modelName));
                }

                var usage = AiReportPdfBuilder.BuildUsageText(AiTokenUsage.Sum(usages));
                if (string.IsNullOrEmpty(usage) == false)
                {
                    items.Add(new KeyValuePair<string, string>("累計用量", usage));
                }

                items.Add(new KeyValuePair<string, string>("累計耗時", $"{totalElapsed.TotalSeconds:F1} 秒"));
                return items;
            }
        }

        /// <summary>
        /// 開窗並立即送出第一次分析。
        ///
        /// ⚠️ 對話窗在<b>送出之前</b>就開（同日誌檢視頁 0.9.8 的教訓）：
        /// 推論模型可能要等好幾分鐘，使用者盯著沒變化的畫面會以為頁面沒在做事。
        /// </summary>
        /// <param name="stackTraceOrNull">原始堆疊；堆疊檔不存在時傳 null。</param>
        public async Task OpenAsync(ExceptionLogAdapterModel exceptionItem, string? stackTraceOrNull)
        {
            if (isRunning)
            {
                return;
            }

            Reset();
            item = exceptionItem;
            conversationId = Guid.NewGuid();
            stackTrace = stackTraceOrNull;
            conversation.Add(new AiChatMessage(
                AiChatRoles.User, AiExceptionPromptBuilder.Build(exceptionItem, stackTraceOrNull)));

            modalState = ModalState.Running;
            visible = true;

            ViewNotification.Info(notificationService, "正在將這筆例外送給 AI 分析，可能需要數十秒…");
            await RunAsync(round: 0);
        }

        private async Task OnSendFollowUpAsync()
        {
            var question = followUpText.Trim();
            if (isRunning || question.Length == 0 || IsFollowUpLimitReached)
            {
                return;
            }

            var round = FollowUpCount + 1;
            conversation.Add(new AiChatMessage(AiChatRoles.User, question));
            entries.Add(new ChatEntry(EntryKind.User, round, question, string.Empty));
            followUpText = string.Empty;

            await RunAsync(round);
        }

        /// <summary>送出目前的對話並處理結果。第一次分析與追問共用。</summary>
        private async Task RunAsync(int round)
        {
            isRunning = true;
            elapsedSeconds = 0;
            scrollToLatestPending = round > 0;
            StateHasChanged();

            // 關窗會清空對話，稽核需要的數字要在送出前先記下來。
            var audit = new AuditTarget(item?.Id, round, conversation.Count, conversation.Sum(message => message.Content.Length));

            try
            {
                cts = new CancellationTokenSource();
                var token = cts.Token;
                _ = RunElapsedTickerAsync(token);

                var callContext = item is null
                    ? null
                    : new AiExceptionCallContext(item.Id, item.ExceptionType, conversationId);
                var result = await aiExceptionAnalysisService.AskAsync(conversation.ToList(), callContext, token);

                // ⚠️ 使用者導航離開時 circuit 已經收掉，連 DbContext 都被釋放了 —— 什麼都不要再碰。
                if (isDisposed)
                {
                    return;
                }

                await WriteAuditAsync(round == 0 ? AuditActions.ExceptionLog.AiAnalyze : AuditActions.ExceptionLog.AiFollowUp, audit, result);

                // 使用者關窗放棄。窗已經關了、對話也清掉了，這裡不要再改任何畫面狀態 ——
                // 即使回應恰好在取消的同時成功回來，也不能把它塞進已經清空的對話。
                if (result.Reason == AiAnalysisFailureReason.Canceled || token.IsCancellationRequested)
                {
                    return;
                }

                if (result.Success == false)
                {
                    logger.LogWarning(
                        "AI exception analysis was not successful. Reason={Reason}, Round={Round}, ExceptionLogId={ExceptionLogId}",
                        result.Reason,
                        round,
                        item?.Id);
                    ShowFailure(round, result.ErrorMessage, result.ErrorDetail);
                    return;
                }

                var html = AiMarkdownRenderer.ToSafeHtml(result.Markdown);
                if (html.Length > MaxRenderedHtmlLength)
                {
                    logger.LogWarning(
                        "AI exception analysis response was too large to render. Characters={Characters}", html.Length);
                    ShowFailure(round, "AI 回傳內容異常龐大，已中止顯示。");
                    return;
                }

                conversation.Add(new AiChatMessage(AiChatRoles.Assistant, result.Markdown));
                entries.Add(new ChatEntry(EntryKind.Assistant, round, result.Markdown, html));
                usages.Add(result.Usage);
                modelName = result.ModelName;
                totalElapsed += result.Elapsed;
                modalState = ModalState.Chatting;
                scrollToLatestPending = round > 0;

                // 一份看起來完整、實際被切掉結論的報告，比明講「可能不完整」更危險。
                if (result.IsTruncatedByLength)
                {
                    ViewNotification.Caution(notificationService,
                        "回應已達長度上限，結尾可能不完整。請調高 AiSettings:MaxOutputTokens 或將它設為 null。");
                }

                // 追問不發完成通知：回覆已直接出現在對話裡，而右下角的通知卡片正好蓋住「送出」鍵，
                // 使用者想接著再問時會點不到。
                if (round == 0)
                {
                    ViewNotification.Info(notificationService, "AI 例外分析完成。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while running AI exception analysis.");

                // 不收拾的話，對話窗會永遠停在「分析中」。
                ShowFailure(round, "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex.ToString());
            }
            finally
            {
                // 取消 token 會順便讓等待計時收工。
                cts?.Cancel();
                cts?.Dispose();
                cts = null;

                isRunning = false;
                if (isDisposed == false)
                {
                    StateHasChanged();
                }
            }
        }

        /// <summary>
        /// 失敗時把原因留在窗內，同時照舊發一則通知。
        ///
        /// 追問失敗不毀掉整段對話：那一則提問從對話移除並放回輸入框，使用者可以直接重送，
        /// 失敗原因則以一則錯誤泡泡留在畫面上（不送給 AI）。
        /// </summary>
        private void ShowFailure(int round, string message, string? detail = null)
        {
            // ⚠️ 使用者已經放棄的話，什麼都不要做。取消在傳輸層可能先變成 HttpRequestException
            // 而不是 OperationCanceledException —— 少了這道閘門，關掉的視窗會自己跳回來。
            if (cts?.IsCancellationRequested == true)
            {
                return;
            }

            if (round == 0)
            {
                errorMessage = message;
                modalState = ModalState.Failed;
            }
            else
            {
                var last = conversation[^1];
                if (last.Role == AiChatRoles.User)
                {
                    conversation.RemoveAt(conversation.Count - 1);
                    entries.RemoveAt(entries.Count - 1);
                    followUpText = last.Content;
                }

                entries.Add(new ChatEntry(EntryKind.Error, round, message, string.Empty));
                scrollToLatestPending = true;
            }

            ViewNotification.Error(notificationService, message, detail);
        }

        private async Task OnCopyAsync()
        {
            try
            {
                // 複製 Markdown 而非 HTML：使用者會貼進工單或通訊軟體，Markdown 才是有用的格式。
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", BuildConversationMarkdown());
                if (copied)
                {
                    ViewNotification.Info(notificationService, "已複製整段對話。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy AI exception analysis to clipboard.");
                ViewNotification.UnexpectedError(notificationService, "複製失敗，請手動選取複製。", ex);
            }
        }

        private string BuildConversationMarkdown()
        {
            var builder = new StringBuilder();
            builder.Append("# 例外 AI 分析：").Append(item?.ExceptionType).Append("\n\n");

            foreach (var entry in entries.Where(entry => entry.Kind != EntryKind.Error))
            {
                if (entry.Kind == EntryKind.User)
                {
                    builder.Append("---\n\n## 追問 ").Append(entry.Round).Append("\n\n> ")
                        .Append(entry.Text.Replace("\n", "\n> ")).Append("\n\n");
                }
                else
                {
                    builder.Append(entry.Text.TrimEnd()).Append("\n\n");
                }
            }

            return builder.ToString();
        }

        private async Task OnExportPdfAsync()
        {
            if (item is null || entries.Count == 0)
            {
                return;
            }

            isExporting = true;
            StateHasChanged();

            try
            {
                ViewNotification.Info(notificationService, "正在產生 PDF…");

                var information = SystemIdentity;
                var bytes = AiExceptionReportPdfBuilder.Build(new AiExceptionReportPdfRequest
                {
                    SystemName = information.Name,
                    SystemVersion = information.Version,
                    OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                    GeneratedAt = DateTime.Now,
                    DetailLines = AiExceptionPromptBuilder.BuildDetailLines(item),
                    StackTrace = stackTrace,
                    Exchanges = conversation.Skip(1).ToList(),
                    ModelName = modelName,
                    Usage = AiTokenUsage.Sum(usages),
                });

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"CustomerHelpAgent.Web-ai-exception-report-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
                await JSRuntime.InvokeVoidAsync(
                    "appFileDownload.downloadFromStream", fileName, streamReference, "application/pdf");

                logger.LogInformation(
                    "AI exception analysis PDF exported. Bytes={Bytes}, FollowUps={FollowUps}", bytes.Length, FollowUpCount);

                await WriteAuditAsync(
                    AuditActions.ExceptionLog.AiExportPdf,
                    new AuditTarget(item.Id, FollowUpCount, conversation.Count, conversation.Sum(message => message.Content.Length)),
                    result: null);

                ViewNotification.Info(notificationService, "PDF 已產生並開始下載。");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "AI exception analysis PDF export failed because the embedded font is missing.");
                ViewNotification.UnexpectedError(notificationService, "PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。", ex);
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "AI exception analysis PDF export aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI exception analysis PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 使用者關閉對話窗。
        ///
        /// ⚠️ 等待中關窗<b>等於放棄這次呼叫</b>：真的取消 HTTP 請求，不留在背景。
        /// 不論是否等待中，關窗都會丟棄窗內的整段對話 —— 窗內不保存報告；
        /// 每一次送出的呼叫另記於「AI 對話紀錄」（0.9.72 起，管理員專屬、依保留天數自動過期）。
        /// </summary>
        private void OnModalCancel()
        {
            if (isRunning)
            {
                cts?.Cancel();
                ViewNotification.Info(notificationService, "已取消本次 AI 分析。");
            }

            visible = false;
            Reset();
        }

        private void Reset()
        {
            item = null;
            conversationId = Guid.Empty;
            stackTrace = null;
            conversation.Clear();
            entries.Clear();
            usages.Clear();
            modelName = string.Empty;
            totalElapsed = TimeSpan.Zero;
            errorMessage = string.Empty;
            followUpText = string.Empty;
            fontScalePercent = DefaultFontScalePercent;
            elapsedSeconds = 0;
            scrollToLatestPending = false;
            modalState = ModalState.Chatting;
        }

        /// <summary>
        /// 新的一則回覆（或等待泡泡）出現時，把畫面帶到它的開頭。
        /// 用 focus 一個 tabindex=-1 的錨點達成，瀏覽器會自動捲動到可見範圍，不必另寫 JS。
        /// </summary>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (scrollToLatestPending == false || latestAnchor.Context is null)
            {
                return;
            }

            scrollToLatestPending = false;
            try
            {
                await latestAnchor.FocusAsync();
            }
            catch (JSException)
            {
                // 錨點在這次 render 之後就消失了（例如回覆剛好到達），不影響任何功能。
            }
            catch (JSDisconnectedException)
            {
                // circuit 已經消失，沒有畫面可以捲動了。
            }
        }

        /// <summary>
        /// 等待中每秒更新一次「已等待 N 秒」。跳動的秒數才證明這次呼叫還在進行中。
        /// ⚠️ 一定要用 <c>InvokeAsync(StateHasChanged)</c>：計時器回呼不在 renderer 的同步內容上。
        /// </summary>
        private async Task RunElapsedTickerAsync(CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(token))
                {
                    elapsedSeconds++;
                    await InvokeAsync(StateHasChanged);
                }
            }
            catch (OperationCanceledException)
            {
                // 呼叫結束或使用者放棄，正常收工。
            }
            catch (ObjectDisposedException)
            {
                // circuit 已經消失，沒有畫面可以更新了。
            }
        }

        /// <summary>
        /// 稽核寫入失敗不應推翻「分析已經完成」這個事實，因此只記錯誤不向使用者報錯。
        ///
        /// ⚠️ detail 只寫輪次、數量、模型、用量與耗時，<b>絕不</b>寫入例外內容、追問或 AI 輸出：
        /// AuditLog 的可視範圍比本頁更廣（見速查 §6.5）。
        /// </summary>
        private async Task WriteAuditAsync(string action, AuditTarget target, AiAnalysisResult? result)
        {
            if (target.ExceptionLogId is null)
            {
                return;
            }

            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: result?.Success ?? true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "ExceptionLog",
                    targetId: target.ExceptionLogId.Value.ToString(CultureInfo.InvariantCulture),
                    detail: BuildAuditDetail(target, result));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for AI exception analysis. Action={Action}", action);
            }
        }

        private static string BuildAuditDetail(AuditTarget target, AiAnalysisResult? result)
        {
            var builder = new StringBuilder();
            builder.Append(target.Round == 0 ? "初始分析" : $"追問第 {target.Round} 輪")
                .Append("；對話 ").Append(target.MessageCount).Append(" 則、")
                .Append(target.CharacterCount).Append(" 字元");

            if (result is null)
            {
                return builder.ToString();
            }

            if (string.IsNullOrWhiteSpace(result.ModelName) == false)
            {
                builder.Append("；模型 ").Append(result.ModelName);
            }

            var usage = AiReportPdfBuilder.BuildUsageText(result.Usage);
            if (string.IsNullOrEmpty(usage) == false)
            {
                builder.Append("；用量 ").Append(usage);
            }

            builder.Append("；耗時 ").Append(result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms");

            if (result.Success == false)
            {
                builder.Append("；失敗原因 ").Append(result.Reason);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 使用者在等待中直接離開頁面時，取消還在飛的請求。
        /// 沒有這一段，一條沒人要的呼叫會繼續佔著連線直到 TimeoutSeconds（預設 600 秒）。
        /// </summary>
        public void Dispose()
        {
            isDisposed = true;
            cts?.Cancel();
        }
    }
}
