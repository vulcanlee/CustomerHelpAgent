using System.Globalization;
using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Components.Auths;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 展示控制台（/demo-console，1.0.5 起）：劇本一鍵準備、故障模擬開關、一鍵重置、即時／回放模式、錄製清單（金劇本、刪除）、
    /// 展示身分切換、幕後面板預設。管理員專屬（權限鍵不進角色矩陣，以 CheckIsAdmin 判斷）；
    /// 示範模式關閉（<c>DemoSettings:Enabled = false</c>）時只顯示提示，所有操作停用。
    ///
    /// <list type="bullet">
    /// <item>⚠️ 稽核在這裡寫：<see cref="DemoControlService"/> 是 Singleton、拿不到登入者，不寫稽核；
    /// 故障、模式、金劇本、刪除錄製都由本頁以登入的管理員寫 <see cref="AuditActions.Demo"/>。重置的稽核由 <see cref="DemoResetService"/> 自己寫。</item>
    /// <item>「準備此劇本」依序：確認 → 重置 → 只開這個劇本的故障 → 模式（選了回放且劇本有金劇本才回放，否則即時，確認窗先講清楚）
    /// → 取一張切換成劇本帳號的票券（切換後到線上客服）→ 整頁導向 <c>/Auths/DemoSwitch</c>。任何一步失敗就停下、不切換身分。</item>
    /// <item><see cref="DemoControlService.Changed"/> 在別的執行緒觸發，一律 <c>InvokeAsync</c>；Dispose 時取消訂閱。</item>
    /// </list>
    /// </summary>
    public partial class DemoConsoleView : IDisposable
    {
        /// <summary>顧客劇本切換後直接到線上客服。</summary>
        internal const string ShopAssistantUrl = "/shop/assistant";

        /// <summary>回放下拉選單最多列出的錄製筆數（新到舊）。</summary>
        private const int RecordingOptionLimit = 100;

        private const string ControlTargetType = "DemoControl";
        private const string RecordingTargetType = "AgentRecording";

        /// <summary>重置的確認文字：清掉的是全部業務資料、會中斷進行中的對話；紀錄與錄製保留。</summary>
        internal const string ResetConfirmText =
            "會刪除所有業務資料（訂單、售後申請、Agent 對話與確認卡、上傳的照片），再以初始的示範資料重新建立，簽收、出貨等日期以現在重新計算。\n"
            + "正在進行中的顧客對話會中斷，開著的顧客頁要重新整理。\n"
            + "流程度量、Agent 執行紀錄、Token 用量、AI 對話紀錄、錄製與稽核紀錄都會保留。\n"
            + "此動作無法復原。";

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public DemoControlService DemoControlService { get; set; } = default!;

        [Inject]
        public DemoResetService DemoResetService { get; set; } = default!;

        [Inject]
        public AgentRecordingService AgentRecordingService { get; set; } = default!;

        [Inject]
        public DemoSwitchTicketService TicketService { get; set; } = default!;

        [Inject]
        public AgentChatClientFactory AgentChatClientFactory { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<DemoConsoleView> Logger { get; set; } = default!;

        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        private bool subscribed;
        private bool disposed;

        /// <summary>有操作進行中：所有按鈕停用，避免連點或同時做兩件事。</summary>
        private bool busy;
        private bool batchDeleting;

        /// <summary>AI 模型的設定問題（null＝已設定）；有問題時只能用回放。</summary>
        private string? aiError;
        private DateTime? lastResetAt;

        private List<AgentRecordingSummary> recordings = [];
        private IEnumerable<AgentRecordingSummary> selectedRows = [];
        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;

        /// <summary>回放下拉選單的選項與目前選取的錄製。</summary>
        private List<AgentRecordingSummary> recordingOptions = [];
        private int? selectedRecordingId;

        /// <summary>正在挑劇本的「設為金劇本」對象（null＝挑選面板關閉）。</summary>
        private AgentRecordingSummary? goldenTarget;

        /// <summary>示範模式關閉或有操作進行中：一律停用。</summary>
        private bool Locked => !DemoControlService.IsEnabled || busy;

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
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/demo-console");
                Logger.LogWarning("Demo console denied because the current user is not an administrator.");
                return;
            }

            DemoControlService.Changed += OnDemoControlChanged;
            subscribed = true;
            aiError = AgentChatClientFactory.Validate();
            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            try
            {
                lastResetAt = await DemoResetService.GetLastResetAtAsync();
                await ReloadRecordingsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load the demo console.");
                ViewNotification.UnexpectedError(NotificationService, "讀取展示控制台資料時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task ReloadRecordingsAsync()
        {
            // 勾選只在目前這一頁有效（速查 §6.28）。
            selectedRows = [];
            var page = await AgentRecordingService.GetRecordingsAsync(new DataRequest { CurrentPage = _pageIndex, PageSize = _pageSize, Take = _pageSize });
            recordings = page.Result.ToList();
            _total = page.Count;

            var options = await AgentRecordingService.GetRecordingsAsync(new DataRequest { CurrentPage = 1, PageSize = RecordingOptionLimit, Take = RecordingOptionLimit });
            recordingOptions = options.Result.ToList();
            if (selectedRecordingId is not { } selected || recordingOptions.All(x => x.Id != selected))
            {
                selectedRecordingId = DemoControlService.ActiveRecordingId
                    ?? recordingOptions.FirstOrDefault(x => x.IsGolden)?.Id
                    ?? recordingOptions.FirstOrDefault()?.Id;
            }
        }

        private async Task OnTableChange(QueryModel<AgentRecordingSummary> args)
        {
            _pageIndex = args.PageIndex;
            await RunAsync(ReloadRecordingsAsync, "讀取錄製清單");
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(NotificationService, "已更新最新資料");
        }

        // ── 劇本 ─────────────────────────────────────────────

        private async Task OnPrepareScenarioAsync(DemoScenario scenario)
        {
            if (Locked)
            {
                return;
            }

            await RunAsync(async () =>
            {
                var replayRequested = DemoControlService.Mode == DemoMode.Replay;
                var golden = replayRequested ? await AgentRecordingService.GetGoldenAsync(scenario.Code) : null;
                if (!await ConfirmDialog.AskDestructiveAsync(
                        ModalService, $"準備劇本 {scenario.Code}：{scenario.Title}", BuildPrepareConfirmText(scenario, replayRequested, golden, aiError), "重置並開始"))
                {
                    return;
                }

                busy = true;
                var reset = await DemoResetService.ResetAsync();
                if (!reset.Success)
                {
                    ViewNotification.Error(NotificationService, reset.Message);
                    return;
                }

                await ApplyFaultsAsync(scenario.Faults.ToHashSet());
                if (golden is not null)
                {
                    await ApplyModeAsync(DemoMode.Replay, golden.Id);
                }
                else
                {
                    await ApplyModeAsync(DemoMode.Live, null);
                }

                var isCustomer = DemoSeedData.Accounts.Any(x => x.Account == scenario.Account && x.RoleName == MagicObjectHelper.顧客角色);
                var ticket = TicketService.Issue(CurrentUserService.CurrentUser.Id, scenario.Account, isCustomer ? ShopAssistantUrl : null);
                Logger.LogInformation(
                    "Demo scenario prepared. ScenarioCode={ScenarioCode}, TargetAccount={TargetAccount}, Mode={Mode}, ActiveRecordingId={ActiveRecordingId}",
                    scenario.Code, scenario.Account, DemoControlService.Mode, DemoControlService.ActiveRecordingId);
                NavigationManager.NavigateTo(DemoSwitch.UrlFor(ticket), forceLoad: true);
            }, "準備劇本");
        }

        /// <summary>確認窗：依序列出會做的事；選了回放卻沒有金劇本時講明改用即時（不默默失敗）。</summary>
        internal static string BuildPrepareConfirmText(DemoScenario scenario, bool replayRequested, AgentRecordingSummary? golden, string? aiError)
        {
            var sb = new StringBuilder();
            sb.Append("將依序執行：\n");
            sb.Append("1. 重置示範資料：").Append(ResetConfirmText.Split('\n')[0]).Append('\n');
            sb.Append("2. 故障模擬：")
                .Append(scenario.Faults.Count == 0 ? "全部關閉" : $"只開「{string.Join("、", scenario.Faults.Select(DemoFaultLabels.Name))}」，其他關閉")
                .Append('\n');
            sb.Append("3. 模式：");
            if (golden is not null)
            {
                sb.Append($"回放金劇本「{golden.Title}」（#{golden.Id}）");
            }
            else if (replayRequested)
            {
                sb.Append($"目前選的是回放，但劇本 {scenario.Code} 還沒有金劇本，這次改用即時（呼叫 AI 模型，並自動錄製）");
            }
            else
            {
                sb.Append("即時（呼叫 AI 模型，並自動錄製）");
            }

            sb.Append('\n');
            if (golden is null && aiError is not null)
            {
                sb.Append("　注意：AI 模型尚未設定，即時模式無法對話。\n");
            }

            sb.Append($"4. 切換成示範帳號 {scenario.Account}，開啟線上客服。\n");
            sb.Append("\n正在進行中的顧客對話會中斷；紀錄與錄製會保留。此動作無法復原。");
            return sb.ToString();
        }

        // ── 故障模擬 ─────────────────────────────────────────

        private async Task OnFaultToggleAsync(DemoFault fault, bool on)
        {
            if (Locked)
            {
                return;
            }

            await RunAsync(async () =>
            {
                busy = true;
                var target = DemoControlService.ActiveFaults.ToHashSet();
                if (on)
                {
                    target.Add(fault);
                }
                else
                {
                    target.Remove(fault);
                }

                await ApplyFaultsAsync(target);
                ViewNotification.Warning(NotificationService, $"已{(on ? "開啟" : "關閉")}故障「{DemoFaultLabels.Name(fault)}」。");
            }, "變更故障模擬");
        }

        private async Task OnClearFaultsAsync()
        {
            if (Locked || DemoControlService.ActiveFaults.Count == 0)
            {
                return;
            }

            await RunAsync(async () =>
            {
                busy = true;
                await ApplyFaultsAsync(new HashSet<DemoFault>());
                ViewNotification.Warning(NotificationService, "已關閉全部故障模擬。");
            }, "關閉故障模擬");
        }

        /// <summary>把故障設成剛好 <paramref name="target"/>：只改有差異的，每一個變更寫一筆稽核。</summary>
        private async Task ApplyFaultsAsync(IReadOnlySet<DemoFault> target)
        {
            var before = DemoControlService.ActiveFaults.ToHashSet();
            foreach (var fault in DemoFaultLabels.Ordered)
            {
                var on = target.Contains(fault);
                if (before.Contains(fault) == on)
                {
                    continue;
                }

                await DemoControlService.SetFaultAsync(fault, on);
                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Demo.FaultChanged, ControlTargetType, fault.ToString(),
                    $"fault={fault}; state={(on ? "on" : "off")}");
            }
        }

        // ── 示範資料 ─────────────────────────────────────────

        private async Task OnResetAsync()
        {
            if (Locked)
            {
                return;
            }

            await RunAsync(async () =>
            {
                if (!await ConfirmDialog.AskDestructiveAsync(ModalService, "一鍵重置示範資料", ResetConfirmText, "重置"))
                {
                    return;
                }

                busy = true;
                var result = await DemoResetService.ResetAsync();
                if (!result.Success)
                {
                    ViewNotification.Error(NotificationService, result.Message);
                    return;
                }

                ViewNotification.Warning(NotificationService, $"{result.Message}（刪除 {result.DeletedRequests} 筆售後申請、{result.DeletedSessions} 段 Agent 對話）");
                lastResetAt = await DemoResetService.GetLastResetAtAsync();
            }, "重置示範資料");
        }

        // ── 模式 ─────────────────────────────────────────────

        private async Task OnModeChangedAsync(DemoMode mode)
        {
            if (Locked || mode == DemoControlService.Mode)
            {
                return;
            }

            await RunAsync(async () =>
            {
                if (mode == DemoMode.Replay && selectedRecordingId is null)
                {
                    ViewNotification.Caution(NotificationService, "還沒有錄製，無法切到回放。請先用即時模式跑一次劇本（會自動錄製）。");
                    return;
                }

                busy = true;
                await ApplyModeAsync(mode, mode == DemoMode.Replay ? selectedRecordingId : null);
                ViewNotification.Warning(NotificationService, mode == DemoMode.Replay ? $"已切到回放（#{selectedRecordingId}）。" : "已切到即時。");
            }, "切換模式");
        }

        private async Task OnReplayRecordingChangedAsync(int? recordingId)
        {
            selectedRecordingId = recordingId;
            if (Locked || recordingId is null || DemoControlService.Mode != DemoMode.Replay || recordingId == DemoControlService.ActiveRecordingId)
            {
                return;
            }

            await RunAsync(async () =>
            {
                busy = true;
                await ApplyModeAsync(DemoMode.Replay, recordingId);
                ViewNotification.Warning(NotificationService, $"回放改用 #{recordingId}。");
            }, "切換回放的錄製");
        }

        /// <summary>切換模式（與目前相同就不動），並寫 <see cref="AuditActions.Demo.ModeChanged"/>。</summary>
        private async Task ApplyModeAsync(DemoMode mode, int? recordingId)
        {
            if (mode == DemoControlService.Mode && recordingId == DemoControlService.ActiveRecordingId)
            {
                return;
            }

            await DemoControlService.SetModeAsync(mode, recordingId);
            if (recordingId is { } id)
            {
                selectedRecordingId = id;
            }

            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Demo.ModeChanged, ControlTargetType, mode.ToString(),
                mode == DemoMode.Replay ? $"mode=Replay; recordingId={recordingId}" : "mode=Live");
        }

        // ── 錄製清單 ─────────────────────────────────────────

        private void OnPickGolden(AgentRecordingSummary recording) => goldenTarget = recording;

        private void OnCancelGolden() => goldenTarget = null;

        private async Task OnSetGoldenAsync(string scenarioCode)
        {
            if (Locked || goldenTarget is not { } target)
            {
                return;
            }

            await RunAsync(async () =>
            {
                busy = true;
                if (!await AgentRecordingService.SetGoldenAsync(target.Id, scenarioCode))
                {
                    ViewNotification.Caution(NotificationService, "找不到這段錄製，可能已被刪除。");
                }
                else
                {
                    await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Demo.RecordingGolden, RecordingTargetType,
                        target.Id.ToString(CultureInfo.InvariantCulture), $"recordingId={target.Id}; scenario={scenarioCode}");
                    ViewNotification.Warning(NotificationService, $"已把 #{target.Id} 設為劇本 {scenarioCode} 的金劇本（原本的金劇本改為一般錄製）。");
                }

                goldenTarget = null;
                await ReloadRecordingsAsync();
            }, "設定金劇本");
        }

        private async Task OnDeleteRecordingAsync(AgentRecordingSummary recording)
        {
            if (Locked)
            {
                return;
            }

            await RunAsync(async () =>
            {
                if (!await ConfirmDialog.AskDestructiveAsync(ModalService, "刪除錄製", BuildDeleteConfirmText([recording]), "刪除"))
                {
                    return;
                }

                busy = true;
                var result = await DeleteOneAsync(recording);
                if (!result.Success)
                {
                    ViewNotification.Caution(NotificationService, result.Message);
                }
                else
                {
                    ViewNotification.Warning(NotificationService, $"已刪除錄製 #{recording.Id}。");
                }

                await AfterDeleteAsync();
            }, "刪除錄製");
        }

        /// <summary>「刪除選取」（速查 §6.28）：逐筆走單筆刪除（各自寫稽核）。</summary>
        private async Task OnBatchDeleteAsync()
        {
            if (Locked)
            {
                return;
            }

            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(ModalService, selected.Select(Label).ToList(), restorable: false, note: DeleteNote(selected)))
                {
                    return;
                }

                batchDeleting = true;
                busy = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.Id.ToString(CultureInfo.InvariantCulture), Label, DeleteOneAsync, Logger);
                BatchDelete.Notify(NotificationService, outcome);
                await AfterDeleteAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while batch deleting agent recordings.");
                ViewNotification.UnexpectedError(NotificationService, "刪除錄製時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
                busy = false;
            }
        }

        private async Task<VerifyRecordResult> DeleteOneAsync(AgentRecordingSummary recording)
        {
            if (!await AgentRecordingService.DeleteAsync(recording.Id))
            {
                return VerifyRecordResultFactory.Build(false, "找不到這段錄製，可能已被刪除。");
            }

            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Demo.RecordingDelete, RecordingTargetType,
                recording.Id.ToString(CultureInfo.InvariantCulture), $"recordingId={recording.Id}");
            return VerifyRecordResultFactory.Build(true);
        }

        /// <summary>刪掉的若是回放中的錄製，改回即時（否則顧客頁會回放一段不存在的錄製）。</summary>
        private async Task AfterDeleteAsync()
        {
            if (DemoControlService.Mode == DemoMode.Replay && DemoControlService.ActiveRecordingId is { } active
                && await AgentRecordingService.GetAsync(active) is null)
            {
                await ApplyModeAsync(DemoMode.Live, null);
                ViewNotification.Caution(NotificationService, "回放中的錄製已被刪除，已改回即時模式。");
            }

            goldenTarget = null;
            await ReloadRecordingsAsync();
        }

        private string BuildDeleteConfirmText(IReadOnlyList<AgentRecordingSummary> items)
            => $"要刪除錄製「{Label(items[0])}」嗎？\n{DeleteNote(items)}\n此動作無法復原。";

        private string DeleteNote(IReadOnlyList<AgentRecordingSummary> items)
        {
            var notes = new List<string> { "錄製的顧客動作與模型回應會一併刪除；Agent 執行紀錄與 Token 用量不受影響。" };
            if (items.Any(x => x.IsGolden))
            {
                notes.Add("其中有金劇本，刪除後該劇本就沒有金劇本，回放時會改用即時。");
            }

            if (DemoControlService.Mode == DemoMode.Replay && items.Any(x => x.Id == DemoControlService.ActiveRecordingId))
            {
                notes.Add("其中有目前回放中的錄製，刪除後會改回即時模式。");
            }

            return string.Join("\n", notes);
        }

        /// <summary>確認窗與結果通知用的名稱：編號＋標題（標題是顧客說的第一句話，不含姓名）。</summary>
        private static string Label(AgentRecordingSummary recording)
            => string.IsNullOrWhiteSpace(recording.Title) ? $"#{recording.Id}" : $"#{recording.Id} {recording.Title}";

        // ── 身分切換、幕後面板 ───────────────────────────────

        private void OnSwitchIdentity(DemoSeedData.DemoAccount account)
        {
            if (Locked)
            {
                return;
            }

            var ticket = TicketService.Issue(CurrentUserService.CurrentUser.Id, account.Account);
            Logger.LogInformation("Demo identity switch requested from the console. TargetAccount={TargetAccount}", account.Account);
            NavigationManager.NavigateTo(DemoSwitch.UrlFor(ticket), forceLoad: true);
        }

        private async Task OnBackstageDefaultChangedAsync(bool on)
        {
            if (Locked)
            {
                return;
            }

            await RunAsync(async () =>
            {
                busy = true;
                await DemoControlService.SetBackstageDefaultAsync(on);
                ViewNotification.Warning(NotificationService, $"顧客頁的幕後面板預設改為{(on ? "開啟" : "關閉")}。");
            }, "變更幕後面板預設");
        }

        // ── 共用 ─────────────────────────────────────────────

        /// <summary>執行一個操作：未預期的例外記錄並通知；結束時解除 busy。</summary>
        private async Task RunAsync(Func<Task> action, string what)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Demo console operation failed. Operation={Operation}", what);
                ViewNotification.UnexpectedError(NotificationService, $"{what}時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                busy = false;
            }
        }

        private static string RecordingOptionLabel(AgentRecordingSummary recording)
        {
            var scenario = recording.ScenarioCode is { } code ? $"［{code}{(recording.IsGolden ? " 金劇本" : string.Empty)}］" : string.Empty;
            return $"{scenario}#{recording.Id} {recording.Title}（{recording.CreatedAt:MM/dd HH:mm}）";
        }

        private static string CostText(decimal? cost) => cost is { } value ? $"NT$ {value:N2}" : "—";

        private void OnDemoControlChanged()
        {
            if (disposed)
            {
                return;
            }

            _ = InvokeAsync(StateHasChanged);
        }

        public void Dispose()
        {
            disposed = true;
            if (subscribed)
            {
                DemoControlService.Changed -= OnDemoControlChanged;
                subscribed = false;
            }
        }
    }
}
