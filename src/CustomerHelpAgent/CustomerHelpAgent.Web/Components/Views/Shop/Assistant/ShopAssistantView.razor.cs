using System.Globalization;
using System.Text;
using System.Text.Json;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Components.Auths;
using CustomerHelpAgent.Web.Components.Commons;
// MAF 1.25 也有同名的 Microsoft.Agents.AI.AgentSessionStore；這裡用的是本專案的資料服務。
using AgentSessionStore = CustomerHelpAgent.Business.Services.DataAccess.AgentSessionStore;
using CardState = CustomerHelpAgent.Web.Components.Views.Shop.Assistant.AgentConfirmCardView.CardState;

namespace CustomerHelpAgent.Web.Components.Views.Shop.Assistant;

/// <summary>
/// 顧客的「線上客服」（/shop/assistant，1.0.4 起）：與售後 Agent 對話。
/// <list type="bullet">
/// <item>對話：顧客泡泡、Agent 泡泡（串流時先以純文字預覽，完成後以 <see cref="AgentTurnResult.ReplyText"/> 重繪並經
/// <see cref="AiMarkdownRenderer.ToSafeHtml"/>）；程式產生的固定說明以系統訊息樣式呈現。</item>
/// <item>規則卡與確認卡由工具結果／程式重查渲染，不採模型文字；確認卡未處理前輸入框停用。</item>
/// <item>停止原因為等待上傳時出現照片上傳區（照片先放 <see cref="AgentPhotoInbox"/>，再續跑）。</item>
/// <item>幕後面板：事件時間軸，開關偏好存 localStorage（有存過就以它為準；沒有或讀不到時取 <see cref="DemoControlService.BackstageDefaultOn"/>）。</item>
/// <item>回放（1.0.5 起，<see cref="AfterSalesAgentRunner.GetReplayScriptAsync"/>）：標題下顯示「回放中（錄製於 …）」；不檢查 AI 設定（回放不呼叫模型、不需金鑰）；
/// 輸入框帶入錄製的下一句且唯讀，只能按送出；確認卡只開放錄製時按的那一個；上傳照片提示「請上傳任一張照片以繼續」（張數要與錄製時相同才能送出）。
/// 動作與錄製不同（<see cref="AgentStopReason.ReplayActionMismatch"/>）只補一則系統訊息、畫面回到送出前；回放結束或中止顯示固定說明，
/// 展示者（Cookie 帶 <see cref="DemoClaims.PresenterId"/>）另有「回到展示控制台」——經切回展示者的票券導過去（<c>/demo-console</c> 只限管理員）。</item>
/// <item>模式（M5）：標題列「單一 Agent ｜ 多 Agent」切換（先確認、再以新模式開新對話；執行中停用、回放中鎖定為錄製時的模式）；
/// 網址 <c>?mode=handoff</c>／<c>single</c>（<see cref="AgentModeLabels"/>，展示控制台用）與目前會話不同時以該模式開新對話，回放中忽略。
/// 多 Agent 模式每段回覆標示發言者（<see cref="AgentTurnResult.Segments"/>／<see cref="AgentTurnResult.Speaker"/>），
/// 交接以「已轉給品質專員」插在該專員的回覆之前；Runner 的回呼在背景執行緒觸發，一律經 <c>InvokeAsync</c> 回到渲染器再改狀態。</item>
/// </list>
/// 進頁時接續最新一個未關閉的會話並以執行軌跡還原對話與幕後面板；另一個分頁已先更新會話時，這一輪以系統訊息提示重新整理。
/// 每次送出、確認、取消、上傳各記一筆流程埋點（CaseKey＝會話識別碼；管道依目前會話的模式）。
/// </summary>
public partial class ShopAssistantView : IDisposable
{
    /// <summary>幕後面板開關存在 localStorage 的鍵；值為 "1"（開）或 "0"（關）。</summary>
    internal const string BackstageStorageKey = "shop.assistant.backstage";

    private const int MaxMessageLength = 500;

    /// <summary>按下確認後這一輪失敗（沒有結果）時，接在確認卡下方的程式說明。</summary>
    private const string NotSubmittedExplanation = "這次沒有完成送出，請稍後再試或到「我的申請」確認。";

    /// <summary>記憶卡按下「記住」後沒有記住、這一輪也沒有結果時，接在確認卡下方的程式說明。</summary>
    private const string MemoryNotSavedExplanation = "這次沒有完成記住，請稍後再試或到「我的 AI 記憶」確認。";

    /// <summary>切換模式的確認窗內文。</summary>
    private const string ModeSwitchConfirmText = "切換模式會開始新對話，目前的對話會保留在紀錄中。";

    /// <summary>串流增量最多每隔這麼久重繪一次，避免逐字觸發渲染。</summary>
    private static readonly TimeSpan DeltaRenderInterval = TimeSpan.FromMilliseconds(80);

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public AfterSalesAgentRunner Runner { get; set; } = default!;

    [Inject]
    public AgentChatClientFactory ChatClientFactory { get; set; } = default!;

    [Inject]
    public AgentSessionStore SessionStore { get; set; } = default!;

    [Inject]
    public AgentPhotoInbox PhotoInbox { get; set; } = default!;

    [Inject]
    public CustomerMemoryService MemoryService { get; set; } = default!;

    [Inject]
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

    [Inject]
    public DemoControlService DemoControlService { get; set; } = default!;

    [Inject]
    public DemoSwitchTicketService TicketService { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public IJSRuntime JS { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<ShopAssistantView> Logger { get; set; } = default!;

    private readonly CancellationTokenSource runCancellation = new();
    private readonly List<ChatEntry> entries = [];
    private readonly List<ShopPendingPhoto> pendingPhotos = [];
    private List<AgentTraceEvent> traceEvents = [];

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;
    private bool loaded;
    private bool disposed;

    /// <summary>AI 設定的錯誤訊息；非 null 時不送出任何訊息。</summary>
    private string? aiError;
    private Guid? sessionKey;

    /// <summary>
    /// 目前會話的模式（M5）：流程度量依此記在「AI 助理」或「AI 多 Agent」管道；切換鈕與多 Agent 提示也依它顯示。
    /// 每次換會話（進頁、網址參數、回放鎖定、切換模式、開新對話）都跟著實際會話的模式更新。
    /// </summary>
    private AgentMode sessionMode = AgentMode.Single;
    private string draft = string.Empty;
    private bool busy;
    private bool awaitingPhotos;
    private bool showBackstage = true;

    /// <summary>
    /// 串流中的預覽（依序的文字段與交接）；null 表示目前沒有在跑的回合。只在渲染器的同步內容裡修改（回呼一律經 <c>InvokeAsync</c>）。
    /// </summary>
    private List<LiveItem>? live;

    /// <summary>串流中目前的發言者（最近一次交接的接手者；這一輪還沒交接時不知道，為 null）。</summary>
    private string? liveSpeaker;
    private long lastDeltaRender;

    /// <summary>本輪 <see cref="AgentStepKind.ModelReply"/> 事件的來源：程式產生的回覆以系統訊息樣式呈現。</summary>
    private AgentDataSource? turnReplySource;

    /// <summary>上一輪因會話版本衝突（另一個分頁先存回）而沒有結果；說明已以系統訊息呈現。</summary>
    private bool turnConflicted;

    /// <summary>版本衝突的那一輪已建立的申請編號（確認送出後才衝突）；確認卡據此標成「已建立」。</summary>
    private string? conflictRequestNo;

    /// <summary>回放劇本：是否回放中、下一個錄製的動作；進頁、每輪結束與開新對話時重新取得。</summary>
    private AgentReplayScript replayScript = AgentReplayScript.NotReplay;

    /// <summary>這一輪回放中止或播完（<see cref="AgentStopReason.ReplayDiverged"/>／<see cref="AgentStopReason.ReplayFinished"/>）；開新對話時清除。</summary>
    private AgentStopReason? replayStopReason;

    /// <summary>展示者以示範帳號身分檢視中（示範模式開啟且 Cookie 帶展示者 claim）。</summary>
    private bool isPresenter;

    private enum EntryKind
    {
        User,
        Agent,
        System,
        PolicyCards,
        Confirmation,

        /// <summary>多 Agent 模式的交接（M5）：「已轉給品質專員」，<see cref="ChatEntry.Text"/> 是接手者的顯示名稱。</summary>
        Handoff,
    }

    /// <summary>串流預覽的一項：交接（<see cref="HandoffTo"/> 不為 null）或一段文字。</summary>
    private sealed class LiveItem
    {
        public string? HandoffTo { get; init; }

        public string? Speaker { get; init; }

        public StringBuilder Text { get; } = new();
    }

    private sealed class ChatEntry
    {
        public required EntryKind Kind { get; init; }

        public string Text { get; init; } = string.Empty;

        /// <summary>多 Agent 模式的發言者顯示名稱（例如「品質專員」）；單一模式為 null（不顯示標籤）。</summary>
        public string? Speaker { get; init; }

        /// <summary>Agent 泡泡的安全 HTML（<see cref="AiMarkdownRenderer.ToSafeHtml"/>），建立時算一次。</summary>
        public string Html { get; init; } = string.Empty;

        public IReadOnlyList<AgentPolicyCard> PolicyCards { get; init; } = [];

        public ConfirmationCard? Card { get; init; }

        public CardState CardState { get; set; }

        public string? RequestNo { get; set; }
    }

    private int CustomerId => CurrentUserService.CurrentUser.Id;

    /// <summary>還沒處理完的確認卡（申請卡或記憶卡）；未處理前輸入框停用。</summary>
    private ConfirmationCard? PendingCard
        => entries.LastOrDefault(x => x.Kind == EntryKind.Confirmation && x.CardState is CardState.Pending or CardState.Submitting)?.Card;

    private bool HasPendingCard => PendingCard is not null;

    /// <summary>多 Agent 模式標題下的提示：分流 Agent 與三位專員（名稱取自交接拓樸，不另寫一份）。</summary>
    private static string HandoffHint
    {
        get
        {
            var triage = HandoffTopologyFactory.DisplayNameOf(HandoffTopologyFactory.TriageId);
            var specialists = HandoffTopologyFactory.Participants
                .Where(x => x.Id != HandoffTopologyFactory.TriageId).Select(x => x.DisplayName).ToList();
            return $"多 Agent 模式：{triage}先了解您的問題，再轉給{string.Join("、", specialists[..^1])}或{specialists[^1]}處理；每則回覆都會標示是誰回答。";
        }
    }

    private bool CanType => loaded && sessionKey is not null && aiError is null && !busy && !HasPendingCard
        && (!IsReplay || (ReplayEnd is null && ReplayNext?.Kind == AgentRecordedTurnKind.Send));

    private bool IsReplay => replayScript.IsReplay;

    private AgentRecordedTurnView? ReplayNext => replayScript.NextTurn;

    /// <summary>回放已結束或中止：這一輪回報的停止原因，或錄製的動作已全部播完。</summary>
    private AgentStopReason? ReplayEnd => !IsReplay ? null
        : replayStopReason ?? (ReplayNext is null ? AgentStopReason.ReplayFinished : null);

    private string ReplayBadgeText => replayScript.RecordedAt is { } recordedAt
        ? $"回放中（錄製於 {recordedAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）"
        : "回放中";

    /// <summary>回放中送出照片的張數要與錄製時相同（顧客訊息「（已上傳 N 張照片）」會送進模型請求，張數不同就對不上錄製）。</summary>
    private bool ReplayPhotosReady => !IsReplay
        || (ReplayEnd is null && ReplayNext is { Kind: AgentRecordedTurnKind.PhotosUploaded } next && sessionKey is { } key
            && string.Equals(
                string.Format(CultureInfo.InvariantCulture, AfterSalesAgentRunner.PhotosUploadedMessageFormat, PhotoInbox.Count(key) + pendingPhotos.Count),
                next.CustomerText?.Trim(),
                StringComparison.Ordinal));

    private string ComposerPlaceholder
        => aiError is not null ? "AI 服務尚未設定"
        : busy ? "客服處理中，請稍候…"
        : PendingCard?.Kind == ConfirmationCardKind.Memory ? "請先按下確認卡上的「記住」或「不用記住」"
        : HasPendingCard ? "請先按下確認卡上的「確認送出」或「取消」"
        : IsReplay && ReplayEnd is not null ? "回放已結束"
        : IsReplay && ReplayNext?.Kind == AgentRecordedTurnKind.PhotosUploaded ? "請上傳任一張照片以繼續"
        : "輸入您的問題，例如：剛買的鞋尺碼不合適，想換大一碼";

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop assistant view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop assistant initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/shop/assistant");
            Logger.LogWarning("Shop assistant denied because current user has not this role permission.");
            return;
        }

        var user = (await authStateProvider.GetAuthenticationStateAsync()).User;
        isPresenter = DemoControlService.IsEnabled && user.HasClaim(x => x.Type == DemoClaims.PresenterId);
        showBackstage = DemoControlService.BackstageDefaultOn;

        try
        {
            var key = await Runner.StartOrResumeAsync();
            var mode = await Runner.GetModeAsync(key);
            // 回放中一律用錄製時的模式（忽略網址參數）；不是回放時，網址的 ?mode= 與目前會話不同就以該模式開新對話。
            var script = await Runner.GetReplayScriptAsync(key);
            var wanted = script.IsReplay ? script.Mode : QueryMode();
            if (wanted is { } target && target != mode)
            {
                key = await Runner.StartNewAsync(target);
                mode = target;
                Logger.LogInformation(
                    "Shop assistant started a new conversation in the requested mode. SessionKey={SessionKey}, Mode={Mode}, FromReplay={FromReplay}",
                    key, mode, script.IsReplay);
            }

            await RestoreAsync(key);
            sessionMode = mode;
            sessionKey = key;
            await RefreshReplayAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the shop assistant conversation.");
            ViewNotification.UnexpectedError(NotificationService, "載入線上客服失敗，請重新整理頁面。", ex);
        }

        // 回放不呼叫模型、不需要金鑰：只在不是回放時檢查 AI 設定。
        if (!IsReplay)
        {
            aiError = ChatClientFactory.Validate();
            if (aiError is not null)
            {
                Logger.LogWarning("Shop assistant opened while the AI service is not configured.");
            }
        }

        loaded = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            // 本機存過偏好就以它為準；沒有存過維持展示控制台的預設。
            var saved = await JS.InvokeAsync<string?>("localStorage.getItem", BackstageStorageKey);
            if (saved is "0" or "1" && showBackstage != (saved == "1"))
            {
                showBackstage = saved == "1";
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // 隱私模式或被封鎖時 localStorage 會拋例外：維持預設。
            Logger.LogDebug(ex, "Shop assistant backstage preference could not be read; using the default.");
        }
    }

    public void Dispose()
    {
        disposed = true;
        // 離開頁面時中止在途的 Agent 回合（Runner 以「已取消」收尾並存回會話）。
        runCancellation.Cancel();
        runCancellation.Dispose();
    }

    /// <summary>
    /// 以執行軌跡還原對話（顧客的話、每輪顧客當時看到的回覆／程式說明、停止說明、規則卡、最後一張未決的確認卡）與幕後面板；
    /// 呈現方式與即時回合的 <see cref="ApplyResult"/> 相同。最後停在等待上傳時重新顯示上傳區。
    /// </summary>
    private async Task RestoreAsync(Guid key)
    {
        // 多 Agent 模式：交接列在該輪 TurnResult 之前，先收起來，與即時回合一樣插在接手者的回覆之前。
        var handoffs = new List<string>();
        foreach (var item in await Runner.GetHistoryAsync(key))
        {
            switch (item.Kind)
            {
                case AgentStepKind.UserMessage:
                    AddHandoffEntries(handoffs);
                    handoffs.Clear();
                    entries.Add(new ChatEntry { Kind = EntryKind.User, Text = item.Text });
                    break;
                case AgentStepKind.Handoff when item.Speaker is { } to:
                    handoffs.Add(to);
                    break;
                case AgentStepKind.TurnResult:
                    AddReplyEntries(item.Text, item.IsProgramReply, item.StopExplanation, item.PolicyCards, item.Speaker, item.Segments, handoffs);
                    handoffs.Clear();
                    break;
                case AgentStepKind.ConfirmationShown when item.PendingCard is { } card:
                    AddHandoffEntries(handoffs);
                    handoffs.Clear();
                    entries.Add(new ChatEntry { Kind = EntryKind.Confirmation, Card = card, CardState = CardState.Pending });
                    break;
            }
        }

        AddHandoffEntries(handoffs);

        traceEvents = [.. await Runner.GetTraceEventsAsync(key)];
        var record = await SessionStore.GetAsync(key, CustomerId);
        awaitingPhotos = !HasPendingCard && record?.LastStopReason == AgentStopReason.AwaitingPhotoUpload;
    }

    private async Task OnSendAsync()
    {
        var text = draft.Trim();
        if (!CanType || text.Length == 0 || sessionKey is not { } key)
        {
            return;
        }

        if (text.Length > MaxMessageLength)
        {
            text = text[..MaxMessageLength];
        }

        draft = string.Empty;
        // 埋點的 await 期間也要擋住第二個動作（RunTurnAsync 會再設一次，並在結束時解除）。
        busy = true;
        await RecordAsync(key, ProcessMetricActions.AgentSendMessage);
        await RunTurnAsync(text, ct => Runner.SendAsync(key, text, OnEventAsync, OnTextDeltaAsync, ct));
    }

    private async Task OnConfirmCardAsync(ChatEntry entry, bool approve)
    {
        if (busy || aiError is not null || entry.Card is not { } card || entry.CardState != CardState.Pending || sessionKey is not { } key
            || (IsReplay && ReplayNext?.Kind != (approve ? AgentRecordedTurnKind.Confirm : AgentRecordedTurnKind.Reject)))
        {
            return;
        }

        entry.CardState = CardState.Submitting;
        busy = true;
        await RecordAsync(key, approve ? ProcessMetricActions.AgentConfirm : ProcessMetricActions.AgentReject);
        var result = await RunTurnAsync(null, ct => Runner.ConfirmAsync(key, card.ConfirmationId, approve, OnEventAsync, OnTextDeltaAsync, ct));

        // 回放中按了和錄製時不同的選擇：Runner 沒有處理這張卡，維持未決。
        if (result?.StopReason == AgentStopReason.ReplayActionMismatch)
        {
            entry.CardState = CardState.Pending;
            return;
        }

        // 版本衝突時沒有結果，但申請可能已建立：卡片一定要標成已建立，不可顯示成沒送出。
        // 記憶卡：以顧客的記憶裡是否確實有卡片上的種類與內容為準（工具執行後才失敗、或版本衝突時也一樣）。
        var isMemory = card.Kind == ConfirmationCardKind.Memory;
        entry.RequestNo = isMemory ? null : result?.CreatedRequestNo ?? conflictRequestNo;
        entry.CardState = !approve ? CardState.Canceled
            : isMemory ? (await IsRememberedAsync(card) ? CardState.Remembered : CardState.NotCreated)
            : entry.RequestNo is not null ? CardState.Created
            : CardState.NotCreated;

        // 這一輪沒有結果（例外已另行通知）：卡片寫「請看下方的說明」，對話裡要有那則說明（版本衝突時已有衝突說明，不重複）。
        if (approve && result is null && !disposed && !turnConflicted && entry.CardState == CardState.NotCreated)
        {
            entries.Add(SystemEntry(isMemory ? MemoryNotSavedExplanation : NotSubmittedExplanation));
        }
    }

    /// <summary>記憶卡上的偏好是否已在顧客的記憶裡（查不到時當作沒有，只記錄）。</summary>
    private async Task<bool> IsRememberedAsync(ConfirmationCard card)
    {
        try
        {
            return (await MemoryService.GetAsync(CustomerId)).Any(x => x.Kind == card.MemoryKind && x.Value == card.MemoryValue);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop assistant could not check whether the confirmed preference was saved.");
            return false;
        }
    }

    private Task OnPhotosSelectedAsync(InputFileChangeEventArgs args)
        => ShopPhotoPicker.AddAsync(args, pendingPhotos, NotificationService, Logger);

    private void RemovePhoto(Guid id) => pendingPhotos.RemoveAll(x => x.Id == id);

    /// <summary>把選好的照片放進暫存區，再請 Agent 續跑（申請送出時才隨申請寫入）。</summary>
    private async Task OnSendPhotosAsync()
    {
        if (busy || aiError is not null || pendingPhotos.Count == 0 || !ReplayPhotosReady || sessionKey is not { } key)
        {
            return;
        }

        var rejected = pendingPhotos.Where(photo => !PhotoInbox.Add(key, photo.Name, photo.Content)).Select(x => x.Name).ToList();
        pendingPhotos.Clear();
        if (rejected.Count > 0)
        {
            Logger.LogInformation("Shop assistant photos rejected by the inbox limit. RejectedCount={RejectedCount}", rejected.Count);
            ViewNotification.Caution(NotificationService,
                $"照片最多 {AfterSalesRequestService.MaxFileCount} 張、每張 10 MB 以內，{string.Join("、", rejected)} 沒有加入。");
        }

        var count = PhotoInbox.Count(key);
        if (count == 0)
        {
            return;
        }

        busy = true;
        await RecordAsync(key, ProcessMetricActions.AgentUploadPhoto);
        var text = string.Format(CultureInfo.InvariantCulture, AfterSalesAgentRunner.PhotosUploadedMessageFormat, count);
        await RunTurnAsync(text, ct => Runner.NotifyPhotosUploadedAsync(key, OnEventAsync, OnTextDeltaAsync, ct));
    }

    private async Task OnNewConversationAsync()
    {
        if (busy)
        {
            return;
        }

        if (entries.Count > 0
            && await ConfirmDialog.AskAsync(ModalService, "開始新對話", "目前的對話會結束，尚未確認的申請內容會作廢。確定要開始新對話嗎？", "開始新對話") == false)
        {
            return;
        }

        // 沿用目前的模式；回放中一律用錄製時的模式。
        await StartConversationAsync(IsReplay && replayScript.Mode is { } recorded ? recorded : sessionMode);
    }

    /// <summary>模式切換（M5）：先確認，再以新模式開新對話（舊對話關閉、保留在紀錄中）。執行中或回放中不可切換。</summary>
    private async Task OnSwitchModeAsync(AgentMode mode)
    {
        if (busy || IsReplay || sessionKey is null || mode == sessionMode)
        {
            return;
        }

        if (await ConfirmDialog.AskAsync(ModalService, "切換模式", ModeSwitchConfirmText, $"切換到{AgentModeLabels.Text(mode)}") == false)
        {
            return;
        }

        await StartConversationAsync(mode);

        // 網址還帶著 ?mode= 時拿掉，免得重新整理又被參數切回去。
        if (QueryMode() is not null)
        {
            NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter(AgentModeLabels.QueryName, (string?)null), replace: true);
        }
    }

    /// <summary>以指定模式開新對話並清空畫面；頁面的模式跟著新會話。</summary>
    private async Task StartConversationAsync(AgentMode mode)
    {
        try
        {
            sessionKey = await Runner.StartNewAsync(mode);
            sessionMode = mode;
            entries.Clear();
            traceEvents = [];
            pendingPhotos.Clear();
            awaitingPhotos = false;
            draft = string.Empty;
            replayStopReason = null;
            Logger.LogInformation("Shop assistant started a new conversation. SessionKey={SessionKey}, Mode={Mode}", sessionKey, mode);
            await RefreshReplayAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start a new shop assistant conversation.");
            ViewNotification.UnexpectedError(NotificationService, "開始新對話失敗，請重新整理頁面。", ex);
        }
    }

    private async Task ToggleBackstageAsync()
    {
        showBackstage = !showBackstage;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", BackstageStorageKey, showBackstage ? "1" : "0");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // 存不了只是下次回到預設（開），目前畫面照樣套用。
            Logger.LogDebug(ex, "Shop assistant backstage preference could not be saved.");
        }
    }

    /// <summary>
    /// 執行一輪：先放顧客泡泡與串流預覽，完成後依結果重繪。失敗回 null（已通知）。結束時重新取得回放劇本（帶入下一句）。
    /// 回放中動作與錄製不同時這一輪沒有執行：拿掉顧客泡泡、還原上傳區，只補一則系統訊息。
    /// </summary>
    private async Task<AgentTurnResult?> RunTurnAsync(string? userText, Func<CancellationToken, Task<AgentTurnResult>> run)
    {
        busy = true;
        var wasAwaitingPhotos = awaitingPhotos;
        awaitingPhotos = false;
        turnReplySource = null;
        turnConflicted = false;
        conflictRequestNo = null;
        live = [];
        liveSpeaker = null;
        lastDeltaRender = TimeProvider.GetTimestamp();
        ChatEntry? userEntry = null;
        if (userText is not null)
        {
            userEntry = new ChatEntry { Kind = EntryKind.User, Text = userText };
            entries.Add(userEntry);
        }

        try
        {
            var result = await run(runCancellation.Token);
            if (disposed)
            {
                return result;
            }

            if (result.StopReason == AgentStopReason.ReplayActionMismatch)
            {
                if (userEntry is not null)
                {
                    entries.Remove(userEntry);
                }

                awaitingPhotos = wasAwaitingPhotos;
                entries.Add(SystemEntry(result.ReplyText));
            }
            else
            {
                ApplyResult(result, LiveHandoffs());
            }

            return result;
        }
        catch (Exception ex) when (disposed)
        {
            // 已離開頁面：沒有畫面可以通知。
            Logger.LogDebug(ex, "Shop assistant turn ended after the view was disposed.");
            return null;
        }
        catch (AgentSessionConflictException ex)
        {
            // 另一個分頁已先更新這段對話：這一輪沒有存回。以系統訊息呈現（重新整理後還原的也是這則說明）。
            Logger.LogWarning(ex, "Shop assistant turn was not saved because the conversation was updated in another tab.");
            AddHandoffEntries(LiveHandoffs());
            entries.Add(SystemEntry(ex.Message));
            turnConflicted = true;
            conflictRequestNo = ex.CreatedRequestNo;
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // Runner 對「會話不存在／已結束」等狀況拋出的訊息是給顧客看的中文。
            Logger.LogWarning(ex, "Shop assistant turn was rejected by the agent runner.");
            AddHandoffEntries(LiveHandoffs());
            ViewNotification.Caution(NotificationService, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Shop assistant turn failed.");
            AddHandoffEntries(LiveHandoffs());
            ViewNotification.UnexpectedError(NotificationService, "線上客服處理失敗，請稍後再試。", ex);
            return null;
        }
        finally
        {
            busy = false;
            live = null;
            if (!disposed)
            {
                await RefreshReplayAsync();
            }
        }
    }

    /// <summary>重新取得回放劇本並帶入下一句（不是回放時不動輸入框）。取不到只記錄，維持原狀。</summary>
    private async Task RefreshReplayAsync()
    {
        if (sessionKey is not { } key)
        {
            return;
        }

        try
        {
            replayScript = await Runner.GetReplayScriptAsync(key);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop assistant could not load the replay script.");
            return;
        }

        if (IsReplay)
        {
            draft = ReplayEnd is null && ReplayNext is { Kind: AgentRecordedTurnKind.Send } next ? next.CustomerText ?? string.Empty : string.Empty;
        }
    }

    /// <summary>展示者回到展示控制台：互動元件換不了 Cookie，取一張切回展示者的票券整頁導到切換頁（成功後導到 <c>/demo-console</c>）。</summary>
    private void BackToConsole()
    {
        if (!isPresenter)
        {
            return;
        }

        var ticket = TicketService.IssueSwitchBack(CustomerId);
        Logger.LogInformation("Shop assistant switching back to the demo presenter after replay. UserId={UserId}", CustomerId);
        NavigationManager.NavigateTo(DemoSwitch.UrlFor(ticket), forceLoad: true);
    }

    /// <summary>串流的文字只是預覽：以最終的 <see cref="AgentTurnResult.ReplyText"/>（多 Agent 模式依發言者分段）重繪。</summary>
    private void ApplyResult(AgentTurnResult result, IReadOnlyList<string> handoffs)
    {
        AddReplyEntries(
            result.ReplyText, turnReplySource != AgentDataSource.Conversation, result.StopExplanation, result.PolicyCards,
            result.Speaker, result.Segments, handoffs);

        if (result.Confirmation is { } card)
        {
            entries.Add(new ChatEntry { Kind = EntryKind.Confirmation, Card = card, CardState = CardState.Pending });
        }

        awaitingPhotos = result.StopReason == AgentStopReason.AwaitingPhotoUpload;
        if (result.StopReason is AgentStopReason.ReplayDiverged or AgentStopReason.ReplayFinished)
        {
            replayStopReason = result.StopReason;
        }
    }

    /// <summary>
    /// 一輪的回覆（程式說明以系統訊息呈現）、另附的停止說明與規則卡；即時回合與還原共用。
    /// 多 Agent 模式（M5）：<paramref name="handoffs"/> 是這一輪依序的交接接手者。回覆有分段時，「已轉給…」插在該位第一次發言的那段之前
    /// （交接事件與專員的文字分屬不同執行緒，以發言者對應，不依事件時間）；沒有分段時全部放在回覆之前，整則回覆標示 <paramref name="speaker"/>。
    /// </summary>
    private void AddReplyEntries(
        string replyText,
        bool programReply,
        string? stopExplanation,
        IReadOnlyList<AgentPolicyCard> policyCards,
        string? speaker = null,
        IReadOnlyList<AgentReplySegment>? segments = null,
        IReadOnlyList<string>? handoffs = null)
    {
        var pending = new Queue<string>(handoffs ?? []);
        if (!programReply && segments is { Count: > 0 })
        {
            foreach (var segment in segments)
            {
                if (segment.Speaker is { } segmentSpeaker && pending.Contains(segmentSpeaker))
                {
                    while (pending.TryDequeue(out var to))
                    {
                        entries.Add(HandoffEntry(to));
                        if (to == segmentSpeaker)
                        {
                            break;
                        }
                    }
                }

                entries.Add(AgentEntry(segment.Text, segment.Speaker));
            }

            AddHandoffEntries(pending);
        }
        else
        {
            AddHandoffEntries(pending);
            if (!string.IsNullOrWhiteSpace(replyText))
            {
                entries.Add(programReply ? SystemEntry(replyText) : AgentEntry(replyText, speaker));
            }
        }

        // 模型有回覆、程式另有停止說明（步數上限、規則衝突…）時補一則系統訊息；程式回覆本身就是說明，不重複。
        if (!programReply && stopExplanation is { } explanation && explanation != replyText)
        {
            entries.Add(SystemEntry(explanation));
        }

        if (policyCards.Count > 0)
        {
            entries.Add(new ChatEntry { Kind = EntryKind.PolicyCards, PolicyCards = policyCards });
        }
    }

    private void AddHandoffEntries(IEnumerable<string> handoffs) => entries.AddRange(handoffs.Select(HandoffEntry));

    /// <summary>這一輪串流中依序的交接接手者。</summary>
    private List<string> LiveHandoffs() => live?.Where(x => x.HandoffTo is not null).Select(x => x.HandoffTo!).ToList() ?? [];

    /// <summary>
    /// Runner 的事件可能來自其他執行緒（多 Agent 模式的 Workflow 在背景執行緒執行）：一律回到渲染器的同步內容再改狀態。
    /// 交接事件（M5）在串流預覽插入「已轉給…」並切換目前的發言者。
    /// </summary>
    private Task OnEventAsync(AgentTraceEvent item) => InvokeAsync(() =>
    {
        if (disposed)
        {
            return;
        }

        traceEvents.Add(item);
        if (item.Kind == AgentStepKind.ModelReply)
        {
            turnReplySource = item.Source;
        }
        else if (item.Kind == AgentStepKind.Handoff && live is not null && HandoffEventData.FromRawJson(item.RawJson) is { } handoff)
        {
            live.Add(new LiveItem { HandoffTo = handoff.To });
            liveSpeaker = handoff.To;
        }

        StateHasChanged();
    });

    private Task OnTextDeltaAsync(string text) => InvokeAsync(() =>
    {
        if (disposed || live is null)
        {
            return;
        }

        if (live.Count == 0 || live[^1].HandoffTo is not null)
        {
            live.Add(new LiveItem { Speaker = liveSpeaker });
        }

        live[^1].Text.Append(text);
        var now = TimeProvider.GetTimestamp();
        if (TimeProvider.GetElapsedTime(lastDeltaRender, now) >= DeltaRenderInterval)
        {
            lastDeltaRender = now;
            StateHasChanged();
        }
    });

    private Task RecordAsync(Guid key, string action)
        => ProcessMetricService.RecordAsync(key, AfterSalesChannelExtensions.ForAgentMode(sessionMode), ProcessActor.Customer, action, CustomerId);

    /// <summary>網址的 <c>?mode=</c>（展示控制台用）；沒有或不認得時回 null。</summary>
    private AgentMode? QueryMode()
        => QueryHelpers.ParseQuery(new Uri(NavigationManager.Uri).Query).TryGetValue(AgentModeLabels.QueryName, out var value)
            ? AgentModeLabels.FromQuery(value.ToString())
            : null;

    /// <summary>
    /// 幕後面板停止原因下方的「確認卡仍在等待」說明（M5 Task 3 m1）：只在<b>記憶卡</b>待確認、而最近一輪的停止原因<b>不是</b>等待確認時才給
    /// （例如同一輪先查到規則衝突、再提出記住偏好，停止原因是「規則衝突」）。一般申請卡或停止原因本來就是等待確認時不顯示，單一模式畫面不變。
    /// </summary>
    private string? BackstagePendingCardTitle
        => PendingCard is { Kind: ConfirmationCardKind.Memory } card && LatestStopReason() is { } reason && reason != AgentStopReason.AwaitingConfirmation
            ? card.Title
            : null;

    /// <summary>幕後面板最近一筆 <see cref="AgentStepKind.Stop"/> 事件的停止原因（RawJson <c>{ stopReason }</c>）；讀不到回 null。</summary>
    private AgentStopReason? LatestStopReason()
    {
        var raw = traceEvents.LastOrDefault(x => x.Kind == AgentStepKind.Stop)?.RawJson;
        if (raw is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("stopReason", out var value) && value.ValueKind == JsonValueKind.String
                && Enum.TryParse<AgentStopReason>(value.GetString(), out var reason)
                ? reason
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatEntry AgentEntry(string text, string? speaker = null)
        => new() { Kind = EntryKind.Agent, Text = text, Speaker = speaker, Html = AiMarkdownRenderer.ToSafeHtml(text) };

    private static ChatEntry HandoffEntry(string to) => new() { Kind = EntryKind.Handoff, Text = to };

    private static ChatEntry SystemEntry(string text) => new() { Kind = EntryKind.System, Text = text };
}
