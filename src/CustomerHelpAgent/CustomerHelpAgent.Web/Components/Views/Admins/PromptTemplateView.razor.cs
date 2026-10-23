using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// AI 提示詞（0.9.108 起）：日誌分析與例外分析各一個範本，修改分析指示存成新版本、檢視與切換歷史版本。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。固定規則（<see cref="AiPromptGuardrails"/>）只顯示、不能改。
    /// </summary>
    public partial class PromptTemplateView
    {
        private readonly ILogger<PromptTemplateView> logger;
        private readonly PromptTemplateService promptTemplateService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private readonly Dictionary<string, List<PromptTemplateVersion>> histories = [];
        private string activeKey = PromptTemplateKeys.LogAnalysis;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool modalVisible;
        private string editingKey = PromptTemplateKeys.LogAnalysis;
        private int? editingBaseVersion;
        private PromptEditModel editing = new();
        private string openedSnapshot = string.Empty;
        private string errorMessage = string.Empty;

        private bool viewVisible;
        private string viewTitle = string.Empty;
        private string viewContent = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        public PromptTemplateView(
            ILogger<PromptTemplateView> logger,
            PromptTemplateService promptTemplateService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.promptTemplateService = promptTemplateService;
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

            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/prompt-templates");
                logger.LogWarning("Prompt template view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        internal static string TitleOf(string key) => key switch
        {
            PromptTemplateKeys.LogAnalysis => "日誌分析",
            PromptTemplateKeys.ExceptionAnalysis => "例外分析",
            PromptTemplateKeys.AfterSalesAgent => "AI 售後客服 Agent",
            _ => key,
        };

        private async Task ReloadAsync()
        {
            isLoading = true;
            try
            {
                foreach (var key in PromptTemplateKeys.All)
                {
                    histories[key] = await promptTemplateService.GetHistoryAsync(key);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load prompt templates.");
                ViewNotification.UnexpectedError(notificationService, "讀取 AI 提示詞時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(notificationService, "已更新最新資料");
        }

        /// <summary>版本紀錄：所有版本（新到舊），最後一列是內建預設。</summary>
        private List<PromptHistoryRow> RowsOf(string key)
        {
            var history = histories.GetValueOrDefault(key) ?? [];
            var rows = history.Select(x => new PromptHistoryRow(x.Version, x.Content, x.IsActive, x.Note, x.CreatedByAccount, x.CreatedAt)).ToList();
            rows.Add(new PromptHistoryRow(null, AiPromptGuardrails.DefaultInstructions(key), !history.Any(x => x.IsActive), "程式內建", null, null));
            return rows;
        }

        /// <summary>「第 N 版 · 建立者 · 時間 · 備註」，沒有的部分省略（升級匯入的版本沒有建立者）。</summary>
        private static string ActiveSummary(PromptTemplateVersion version)
            => string.Join(" · ", new[] { $"第 {version.Version} 版", version.CreatedByAccount, version.CreatedAt.ToString("yyyy-MM-dd HH:mm"), version.Note }
                .Where(x => !string.IsNullOrEmpty(x)));

        private void OnEdit(string key)
        {
            var active = (histories.GetValueOrDefault(key) ?? []).FirstOrDefault(x => x.IsActive);
            editingKey = key;
            editingBaseVersion = active?.Version;
            editing = new PromptEditModel { Content = active?.Content ?? AiPromptGuardrails.DefaultInstructions(key).ReplaceLineEndings("\n") };
            openedSnapshot = Snapshot(editing);
            errorMessage = string.Empty;
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存 AI 提示詞時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, Snapshot(editing) != openedSnapshot);
        }

        private async Task<bool> SaveAsync()
        {
            errorMessage = string.Empty;
            var (result, version) = await promptTemplateService.SaveNewVersionAsync(
                editingKey, editing.Content ?? string.Empty, editing.Note, editingBaseVersion, CurrentUserService.CurrentUser.Account);
            if (!result.Success)
            {
                errorMessage = result.Message;
                return false;
            }

            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Prompt.Update, "PromptTemplate", editingKey,
                $"version={version}");
            ViewNotification.Warning(notificationService, $"已存成第 {version} 版，下一次 AI 分析就會使用。");
            await ReloadAsync();
            return true;
        }

        private void OnView(string key, PromptHistoryRow row)
        {
            viewTitle = $"{TitleOf(key)}：{(row.Version is { } v ? $"第 {v} 版" : "內建預設")}";
            viewContent = row.Content;
            viewVisible = true;
        }

        private async Task OnActivateAsync(string key, PromptHistoryRow row)
        {
            try
            {
                var label = row.Version is { } v ? $"第 {v} 版" : "內建預設";
                var ok = await ConfirmDialog.AskAsync(modalService, "切換提示詞", $"要讓「{TitleOf(key)}」改用{label}嗎？下一次 AI 分析就會使用。", "切換");
                if (!ok)
                {
                    return;
                }

                var result = await promptTemplateService.ActivateAsync(key, row.Version);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Prompt.Activate, "PromptTemplate", key,
                    $"version={(row.Version is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "default")}");
                ViewNotification.Warning(notificationService, $"「{TitleOf(key)}」已改用{label}。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while activating a prompt template.");
                ViewNotification.UnexpectedError(notificationService, "切換 AI 提示詞時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private static string Snapshot(PromptEditModel x) => string.Join('|', x.Content, x.Note);
    }

    /// <summary>編輯窗的欄位。</summary>
    public sealed class PromptEditModel
    {
        public string? Content { get; set; }

        public string? Note { get; set; }
    }

    /// <summary>版本紀錄的一列；<see cref="Version"/> 為 null 代表程式內建的預設。</summary>
    public sealed record PromptHistoryRow(int? Version, string Content, bool IsActive, string? Note, string? CreatedByAccount, DateTime? CreatedAt);
}
