using System.Globalization;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Configuration.Parameters;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 系統參數（0.9.98 起）：在畫面上修改保留天數、監控門檻、系統名稱等非機密參數，存檔後不需重啟即生效。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
    /// </summary>
    public partial class SystemParameterView
    {
        private readonly ILogger<SystemParameterView> logger;
        private readonly SystemParameterManager manager;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private SystemParameterOverview? overview;
        private bool isLoading;

        /// <summary>要捲到的分類（例如「資料保留」）；「資料保留與清理」頁的「修改」帶 <c>?group=</c> 進來（0.9.133 起）。</summary>
        [Parameter]
        public string? FocusGroup { get; set; }

        private ElementReference focusGroupHeading;
        private bool focusDone;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool modalVisible;
        private SystemParameterOverviewItem? editing;
        private ParameterEditModel editForm = new();
        private ParameterEditModel openedForm = new();
        private string editErrorMessage = string.Empty;

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

        public SystemParameterView(
            ILogger<SystemParameterView> logger,
            SystemParameterManager manager,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.manager = manager;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        private IEnumerable<IGrouping<string, SystemParameterOverviewItem>> Groups
            => (overview?.Items ?? []).GroupBy(x => x.Definition.Group);

        private int NotAppliedCount => overview?.Items.Count(x => x.Source == SystemParameterSource.NotApplied) ?? 0;

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
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/system-parameters");
                logger.LogWarning("System parameter view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // 資料載入後分類標題才出現：等它渲染出來再聚焦一次（聚焦會把它捲進畫面），之後的重新整理不再搶捲動位置。
            if (focusDone || string.IsNullOrEmpty(FocusGroup) || focusGroupHeading.Context is null)
            {
                return;
            }

            focusDone = true;
            try
            {
                await focusGroupHeading.FocusAsync();
            }
            catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or TaskCanceledException)
            {
                logger.LogDebug(ex, "Failed to focus the system parameter group. Group={Group}", FocusGroup);
            }
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            try
            {
                overview = await manager.GetOverviewAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load system parameters.");
                ViewNotification.UnexpectedError(notificationService, "讀取系統參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

        private void OnEdit(SystemParameterOverviewItem item)
        {
            editing = item;
            editErrorMessage = string.Empty;
            var current = SystemParameterValueCodec.Canonical(item.Definition, item.EffectiveValue) ?? string.Empty;
            editForm = new ParameterEditModel
            {
                Text = current,
                Number = int.TryParse(current, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0,
                Flag = current == "true",
            };
            openedForm = editForm with { };
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存系統參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, editForm != openedForm);
        }

        private async Task<bool> SaveAsync()
        {
            if (editing is not { } item)
            {
                return true;
            }

            editErrorMessage = string.Empty;
            var definition = item.Definition;
            var raw = definition.Kind switch
            {
                SystemParameterKind.Int => editForm.Number.ToString(CultureInfo.InvariantCulture),
                SystemParameterKind.Bool => editForm.Flag ? "true" : "false",
                _ => editForm.Text,
            };

            if (definition.IsRetention && ShortensRetention(item.EffectiveValue, raw))
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "縮短保留期限",
                    $"「{definition.Label}」將從 {SystemParameterValueCodec.Display(definition, item.EffectiveValue)} 改為 {SystemParameterValueCodec.Display(definition, raw)}。" +
                    $"下一次執行時（{definition.EffectNote}）會永久刪除超出新設定的資料，無法復原。確定要儲存嗎？",
                    "確定縮短");
                if (!ok)
                {
                    return false;
                }
            }

            var result = await manager.SaveAsync(definition.Key, raw, item.Override?.ConcurrencyStamp, CurrentUserService.CurrentUser.Account);
            if (!result.Success)
            {
                editErrorMessage = result.Message;
                return false;
            }

            await WriteAuditAsync(definition.Key, result);
            ViewNotification.Warning(notificationService, result.Message);
            await ReloadAsync();
            return true;
        }

        private async Task OnResetAsync(SystemParameterOverviewItem item)
        {
            if (item.Override is not { } row)
            {
                return;
            }

            try
            {
                var definition = item.Definition;
                var content = $"「{definition.Label}」將改回設定檔的值 {SystemParameterValueCodec.Display(definition, item.BaseValue)}。";
                var ok = definition.IsRetention && ShortensRetention(item.EffectiveValue, item.BaseValue)
                    ? await ConfirmDialog.AskDestructiveAsync(modalService, "還原為設定檔值",
                        content + "這會縮短保留期限，下一次執行時會永久刪除超出設定的資料，無法復原。", "確定還原")
                    : await ConfirmDialog.AskAsync(modalService, "還原為設定檔值", content, "還原");
                if (!ok)
                {
                    return;
                }

                var result = await manager.ResetAsync(definition.Key, row.ConcurrencyStamp);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                await WriteAuditAsync(definition.Key, result);
                ViewNotification.Warning(notificationService, result.Message);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while resetting a system parameter.");
                ViewNotification.UnexpectedError(notificationService, "還原系統參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task OnRemoveOrphanAsync(SystemParameterAdapterModel orphan)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(modalService, "移除不認得的參數",
                    $"要從資料庫移除「{orphan.ParameterKey}」嗎？它目前沒有作用。", "移除");
                if (!ok)
                {
                    return;
                }

                var result = await manager.ResetAsync(orphan.ParameterKey, orphan.ConcurrencyStamp);
                if (result.Success)
                {
                    await WriteAuditAsync(orphan.ParameterKey, result);
                    ViewNotification.Warning(notificationService, result.Message);
                }
                else
                {
                    ViewNotification.Caution(notificationService, result.Message);
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while removing an unknown system parameter.");
                ViewNotification.UnexpectedError(notificationService, "移除參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private Task WriteAuditAsync(string key, SystemParameterChangeResult result)
            => ViewAudit.WriteAsync(
                AuditLogService, CurrentUserService,
                result.IsReset ? AuditActions.SystemParameter.Reset : AuditActions.SystemParameter.Update,
                "SystemParameter", key, result.AuditDetail);

        /// <summary>新的天數比目前短（0＝不清除，改成任何正數都算縮短）。</summary>
        internal static bool ShortensRetention(string? currentValue, string? newValue)
        {
            if (!int.TryParse(newValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var next) || next <= 0)
            {
                return false;
            }

            if (!int.TryParse(currentValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var current))
            {
                return false;
            }

            return current == 0 || next < current;
        }

        private static string? RangeText(SystemParameterDefinition definition)
        {
            if (definition.Kind == SystemParameterKind.Int && definition.Min is { } min && definition.Max is { } max)
            {
                var text = $"{min.ToString("N0", CultureInfo.InvariantCulture)}～{max.ToString("N0", CultureInfo.InvariantCulture)}{(definition.Unit is null ? string.Empty : " " + definition.Unit)}";
                return definition.ZeroMeaning is { } zero ? $"{text}（0＝{zero}）" : text;
            }

            if (definition.Kind == SystemParameterKind.String && definition.MaxLength is { } maxLength)
            {
                return $"最多 {maxLength} 個字{(definition.Required ? "，不可留空" : "，可以留空")}";
            }

            return null;
        }

        private static string SourceText(SystemParameterSource source) => source switch
        {
            SystemParameterSource.Override => "系統參數",
            SystemParameterSource.NotApplied => "未套用",
            _ => "設定檔",
        };

        private static StatusTone SourceTone(SystemParameterSource source) => source switch
        {
            SystemParameterSource.Override => StatusTone.Accent,
            SystemParameterSource.NotApplied => StatusTone.Warning,
            _ => StatusTone.Muted,
        };

        private static string ToLocalText(DateTime utc)
            => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        /// <summary>修改窗的輸入；依參數型別只用其中一個欄位。record 讓「有沒有改過」可以直接比較。</summary>
        private sealed record ParameterEditModel
        {
            public string Text { get; set; } = string.Empty;

            public int Number { get; set; }

            public bool Flag { get; set; }
        }
    }
}
