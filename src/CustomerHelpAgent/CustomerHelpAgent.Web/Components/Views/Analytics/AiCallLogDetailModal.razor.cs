using System.Globalization;
using System.Text;
using System.Text.Json;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Components.Views.Analytics
{
    /// <summary>
    /// AI 對話紀錄的明細視窗（0.9.72 起）：中繼資料、用量、同一對話，以及五種檢視 ——
    /// 完整對話、送出 Prompt、取得 Response、原始 Request、原始 Response。
    ///
    /// ⚠️ AI 產出一律經 <see cref="AiMarkdownRenderer.ToSafeHtml"/>（不可改用 HelpMarkdownRenderer）。
    /// 內容可能數 MB：只渲染目前的檢視，每段畫面只顯示前 <see cref="MaxDisplayCharacters"/> 字元。
    /// </summary>
    public partial class AiCallLogDetailModal
    {
        /// <summary>單段畫面顯示上限（完整內容用複製或下載 JSON 取得）。</summary>
        private const int MaxDisplayCharacters = 200_000;

        /// <summary>Markdown 渲染後的 HTML 上限；超過就退回純文字（與例外 AI 分析窗相同）。</summary>
        private const int MaxRenderedHtmlLength = 512 * 1024;

        private const int DefaultFontScalePercent = 100;

        private const string ViewConversation = "conversation";
        private const string ViewPrompt = "prompt";
        private const string ViewResponse = "response";
        private const string ViewRawRequest = "raw-request";
        private const string ViewRawResponse = "raw-response";

        private static readonly IReadOnlyList<(string Key, string Label)> Views =
        [
            (ViewConversation, "完整對話"),
            (ViewPrompt, "送出 Prompt"),
            (ViewResponse, "取得 Response"),
            (ViewRawRequest, "原始 Request"),
            (ViewRawResponse, "原始 Response"),
        ];

        /// <summary>與日誌檢視、例外 AI 分析兩個視窗相同的三段字級。</summary>
        private static readonly (int Percent, string Label)[] FontScaleOptions =
        [
            (100, "一般"),
            (125, "中"),
            (150, "大"),
        ];

        private static readonly JsonSerializerOptions PrettyJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly ILogger<AiCallLogDetailModal> logger;
        private readonly AiCallLogService aiCallLogService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;
        private readonly NotificationService notificationService;
        private readonly ISystemIdentity systemIdentity;
        private readonly IJSRuntime jsRuntime;

        private readonly Dictionary<string, string> htmlCache = new(StringComparer.Ordinal);

        private bool visible;
        private bool isLoading;
        private bool isExporting;
        private bool renderMarkdown;
        private int fontScalePercent = DefaultFontScalePercent;
        private string activeView = ViewConversation;
        private AiCallLogDetail? detail;
        private IReadOnlyList<DisplayBlock> currentBlocks = [];

        public AiCallLogDetailModal(
            ILogger<AiCallLogDetailModal> logger,
            AiCallLogService aiCallLogService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            NotificationService notificationService,
            ISystemIdentity systemIdentity,
            IJSRuntime jsRuntime)
        {
            this.logger = logger;
            this.aiCallLogService = aiCallLogService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.notificationService = notificationService;
            this.systemIdentity = systemIdentity;
            this.jsRuntime = jsRuntime;
        }

        private IReadOnlyList<DisplayBlock> CurrentBlocks => currentBlocks;

        private bool IsTextView => activeView is ViewConversation or ViewPrompt or ViewResponse;

        private string FontScaleCss
            => (fontScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

        private string ModelText
        {
            get
            {
                var requested = detail?.Content?.RequestedModel;
                var actual = detail?.Item.Model ?? string.Empty;
                return string.IsNullOrWhiteSpace(requested) || string.Equals(requested, actual, StringComparison.Ordinal)
                    ? actual
                    : $"{requested} → {actual}";
            }
        }

        private string UsageText
        {
            get
            {
                if (detail?.Usage is not { } usage)
                {
                    // 取消與非預期錯誤這兩支，Token 用量本來就不記（兩頁筆數可能不同，是刻意的）。
                    return detail?.Item.IsCanceled == true || detail?.Item.FailureReason == nameof(AiAnalysisFailureReason.Unexpected)
                        ? "沒有對應的用量紀錄（使用者取消與非預期錯誤的呼叫不記入 Token 用量）"
                        : "沒有對應的用量紀錄（可能已被清除）";
                }

                var cost = usage.IsUnpriced
                    ? "未定價"
                    : $"NT$ {TokenUsageFormat.CostTwdCell(usage.CostTwd)}（US$ {TokenUsageFormat.CostUsdCell(usage.CostUsd)}）";
                return $"輸入 {Format(usage.InputCount)}／輸出 {Format(usage.OutputCount)}／合計 {Format(usage.TotalCount)}，{cost}";
            }
        }

        /// <summary>開啟（或在同一對話間切換到）指定紀錄。</summary>
        public async Task OpenAsync(int id)
        {
            visible = true;
            isLoading = true;
            htmlCache.Clear();
            StateHasChanged();

            try
            {
                detail = await aiCallLogService.GetDetailAsync(id);
                if (detail is null)
                {
                    visible = false;
                    ViewNotification.Caution(notificationService, "找不到這筆對話紀錄（可能已被刪除）。");
                    return;
                }

                RebuildBlocks();
                await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.AiCallLog.View, "AiCallLog", id.ToString(), null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load AI call log detail. CallLogId={CallLogId}", id);
                visible = false;
                ViewNotification.UnexpectedError(notificationService, $"讀取對話紀錄失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private void Close()
        {
            visible = false;
            detail = null;
            currentBlocks = [];
            htmlCache.Clear();
            fontScalePercent = DefaultFontScalePercent;
        }

        private void SwitchView(string key)
        {
            activeView = key;
            RebuildBlocks();
        }

        private void RebuildBlocks()
            => currentBlocks = detail?.Content is { } content ? BuildBlocks(activeView, content, detail.Messages, detail.Item) : [];

        /// <summary>依檢視組出要顯示的區塊。每段都標示角色與方向（送出／取得）。</summary>
        private static List<DisplayBlock> BuildBlocks(
            string view,
            AiCallLogEntry content,
            IReadOnlyList<AiCallLogMessage>? messages,
            AiCallLogAdapterModel item)
        {
            var blocks = new List<DisplayBlock>();

            if (view is ViewConversation or ViewPrompt && messages is not null)
            {
                for (var index = 0; index < messages.Count; index++)
                {
                    var message = messages[index];
                    var isHistory = message.Role == AiChatRoles.Assistant;
                    blocks.Add(new DisplayBlock(
                        $"message-{index}",
                        $"#{index + 1} {AiCallLogPdfBuilder.RoleLabel(message.Role)}",
                        isHistory ? "送出（對話歷史）" : "送出",
                        isHistory ? StatusTone.Muted : StatusTone.Accent,
                        RoleClass(message.Role),
                        message.Content,
                        AllowMarkdown: true,
                        Note: null));
                }
            }

            if (view is ViewConversation or ViewResponse)
            {
                blocks.Add(new DisplayBlock(
                    "response",
                    "AI 回應（Assistant）",
                    "取得",
                    StatusTone.Positive,
                    "response",
                    content.ResponseText,
                    AllowMarkdown: true,
                    content.ResponseText.Length == 0 ? $"未取得內容（{AiCallLogPdfBuilder.DescribeOutcome(item)}）。" : null));
            }

            if (view == ViewRawRequest)
            {
                blocks.Add(new DisplayBlock(
                    "raw-request",
                    "原始 Request（JSON，已排版；不含 HTTP 標頭與金鑰）",
                    "送出",
                    StatusTone.Accent,
                    "raw",
                    PrettyJson(content.RequestBody),
                    AllowMarkdown: false,
                    Note: null));
            }

            if (view == ViewRawResponse)
            {
                blocks.Add(new DisplayBlock(
                    "raw-response",
                    "原始 Response（JSON，已排版）",
                    "取得",
                    StatusTone.Positive,
                    "raw",
                    content.ResponseBody is null ? string.Empty : PrettyJson(content.ResponseBody),
                    AllowMarkdown: false,
                    content.ResponseBody is null ? "沒有收到回應 body（逾時、連線失敗或取消）。" : null));
            }

            return blocks;
        }

        private static string RoleClass(string role) => role switch
        {
            AiChatRoles.System => "system",
            AiChatRoles.User => "user",
            AiChatRoles.Assistant => "assistant",
            _ => "other",
        };

        /// <summary>排版 JSON 以便閱讀；不是合法 JSON 時原樣顯示。</summary>
        private static string PrettyJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        private static string DisplayText(DisplayBlock block)
            => block.Text.Length > MaxDisplayCharacters ? block.Text[..MaxDisplayCharacters] : block.Text;

        private string RenderHtml(DisplayBlock block)
        {
            if (htmlCache.TryGetValue(block.Key, out var cached))
            {
                return cached;
            }

            var html = AiMarkdownRenderer.ToSafeHtml(DisplayText(block));
            if (html.Length > MaxRenderedHtmlLength)
            {
                // 渲染結果異常龐大就不注入，退回純文字（仍經同一條安全管線）。
                html = AiMarkdownRenderer.ToSafeHtml("（內容過大，已改以純文字顯示；請切回「純文字」或用「複製」取得原文。）");
            }

            htmlCache[block.Key] = html;
            return html;
        }

        private async Task OnCopyAsync(DisplayBlock block)
        {
            try
            {
                var copied = await jsRuntime.InvokeAsync<bool>("appClipboard.copyText", block.Text);
                if (copied)
                {
                    ViewNotification.Info(notificationService, $"已複製「{block.Heading}」（{block.Text.Length:N0} 字元）。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕寫入剪貼簿，請改用下載 JSON。");
                }
            }
            catch (JSDisconnectedException)
            {
                // 連線已中斷，沒有畫面可以回報。
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy AI call log block.");
                ViewNotification.UnexpectedError(notificationService, $"複製失敗：{ex.GetType().Name}。", ex);
            }
        }

        private async Task OnDownloadJsonAsync()
        {
            if (detail is null)
            {
                return;
            }

            isExporting = true;
            try
            {
                var json = await aiCallLogService.ReadContentFileAsync(detail.Item.Id);
                if (json is null)
                {
                    ViewNotification.Caution(notificationService, "內容檔不存在，無法下載。");
                    return;
                }

                // JSON 刻意不加 BOM（RFC 8259 不允許）；CSV 等給 Excel 開的才走 TextDownloadPayload.Utf8WithBom。
                var bytes = Encoding.UTF8.GetBytes(json);
                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"CustomerHelpAgent.Web-ai-call-{detail.Item.CallId:N}.json";
                await jsRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "application/json");

                await WriteExportAuditAsync("json");
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "AI call log download aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log download failed. CallLogId={CallLogId}", detail.Item.Id);
                ViewNotification.UnexpectedError(notificationService, $"下載失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
            }
        }

        private async Task OnExportPdfAsync()
        {
            if (detail?.Content is not { } content)
            {
                return;
            }

            isExporting = true;
            StateHasChanged();

            try
            {
                var information = systemIdentity;
                var request = new AiCallLogPdfRequest
                {
                    SystemName = information.Name,
                    SystemVersion = information.Version,
                    OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                    GeneratedAt = DateTime.Now,
                    Item = detail.Item,
                    Messages = detail.Messages ?? [],
                    ResponseText = content.ResponseText,
                    Usage = detail.Usage,
                };

                // 排版是 CPU 密集的同步工作，放到背景執行緒，畫面才不會卡住。
                var bytes = await Task.Run(() => AiCallLogPdfBuilder.Build(request));

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"CustomerHelpAgent.Web-ai-call-{detail.Item.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
                await jsRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "application/pdf");

                logger.LogInformation("AI call log PDF exported. CallLogId={CallLogId}, Bytes={Bytes}", detail.Item.Id, bytes.Length);
                await WriteExportAuditAsync("pdf");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "AI call log PDF export failed because the embedded font is missing.");
                ViewNotification.UnexpectedError(notificationService, "PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。", ex);
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "AI call log PDF export aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
                StateHasChanged();
            }
        }

        /// <summary>匯出內容本身留下一筆稽核紀錄（只記紀錄 Id 與格式，絕不記內容）。</summary>
        private async Task WriteExportAuditAsync(string format)
        {
            if (detail is null)
            {
                return;
            }

            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    AuditActions.AiCallLog.Export,
                    success: true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "AiCallLog",
                    targetId: detail.Item.Id.ToString(),
                    detail: $"匯出 AI 對話紀錄（{format}）：{detail.Item.Operation}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for AI call log export.");
            }
        }

        private static string AccountText(AiCallLogAdapterModel item)
        {
            var account = string.IsNullOrWhiteSpace(item.Account) ? "（系統自動）" : item.Account;
            return item.UserId is null ? account : $"{account}（Id={item.UserId}）";
        }

        private static StatusTone ResolveTone(AiCallLogAdapterModel item)
            => item.Success ? StatusTone.Positive : item.IsCanceled ? StatusTone.Muted : StatusTone.Warning;

        private static string Format(int? value) => value?.ToString("N0") ?? "—";

        /// <summary>畫面上的一段內容。</summary>
        private sealed record DisplayBlock(
            string Key,
            string Heading,
            string Direction,
            StatusTone DirectionTone,
            string RoleClass,
            string Text,
            bool AllowMarkdown,
            string? Note);
    }
}
