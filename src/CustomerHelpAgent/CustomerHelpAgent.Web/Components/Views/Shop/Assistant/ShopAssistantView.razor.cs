using System.Globalization;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Ai;
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
/// <item>幕後面板：事件時間軸，開關偏好存 localStorage（讀寫失敗一律當作預設開）。</item>
/// </list>
/// 進頁時接續最新一個未關閉的會話並還原對話；每次送出、確認、取消、上傳各記一筆流程埋點（CaseKey＝會話識別碼）。
/// </summary>
public partial class ShopAssistantView : IDisposable
{
    /// <summary>幕後面板開關存在 localStorage 的鍵；值為 "1"（開）或 "0"（關）。</summary>
    internal const string BackstageStorageKey = "shop.assistant.backstage";

    private const int MaxMessageLength = 500;

    /// <summary>按下確認後這一輪失敗（沒有結果）時，接在確認卡下方的程式說明。</summary>
    private const string NotSubmittedExplanation = "這次沒有完成送出，請稍後再試或到「我的申請」確認。";

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
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

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
    private string draft = string.Empty;
    private bool busy;
    private bool awaitingPhotos;
    private bool showBackstage = true;

    /// <summary>串流中的預覽文字；null 表示目前沒有在跑的回合。</summary>
    private StringBuilder? streaming;
    private long lastDeltaRender;

    /// <summary>本輪 <see cref="AgentStepKind.ModelReply"/> 事件的來源：程式產生的回覆以系統訊息樣式呈現。</summary>
    private AgentDataSource? turnReplySource;

    private enum EntryKind
    {
        User,
        Agent,
        System,
        PolicyCards,
        Confirmation,
    }

    private sealed class ChatEntry
    {
        public required EntryKind Kind { get; init; }

        public string Text { get; init; } = string.Empty;

        /// <summary>Agent 泡泡的安全 HTML（<see cref="AiMarkdownRenderer.ToSafeHtml"/>），建立時算一次。</summary>
        public string Html { get; init; } = string.Empty;

        public IReadOnlyList<AgentPolicyCard> PolicyCards { get; init; } = [];

        public ConfirmationCard? Card { get; init; }

        public CardState CardState { get; set; }

        public string? RequestNo { get; set; }
    }

    private int CustomerId => CurrentUserService.CurrentUser.Id;

    private bool HasPendingCard => entries.Any(x => x.Kind == EntryKind.Confirmation && x.CardState is CardState.Pending or CardState.Submitting);

    private bool CanType => loaded && sessionKey is not null && aiError is null && !busy && !HasPendingCard;

    private string ComposerPlaceholder
        => aiError is not null ? "AI 服務尚未設定"
        : busy ? "客服處理中，請稍候…"
        : HasPendingCard ? "請先按下確認卡上的「確認送出」或「取消」"
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

        aiError = ChatClientFactory.Validate();
        if (aiError is not null)
        {
            Logger.LogWarning("Shop assistant opened while the AI service is not configured.");
        }

        try
        {
            var key = await Runner.StartOrResumeAsync();
            await RestoreAsync(key);
            sessionKey = key;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the shop assistant conversation.");
            ViewNotification.UnexpectedError(NotificationService, "載入線上客服失敗，請重新整理頁面。", ex);
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
            if (await JS.InvokeAsync<string?>("localStorage.getItem", BackstageStorageKey) == "0")
            {
                showBackstage = false;
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // 隱私模式或被封鎖時 localStorage 會拋例外：維持預設（開）。
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

    /// <summary>以會話內容還原對話（顧客與 Agent 的文字、最後一張未決的確認卡）；最後停在等待上傳時重新顯示上傳區。</summary>
    private async Task RestoreAsync(Guid key)
    {
        foreach (var item in await Runner.GetHistoryAsync(key))
        {
            switch (item.Kind)
            {
                case AgentStepKind.UserMessage:
                    entries.Add(new ChatEntry { Kind = EntryKind.User, Text = item.PlainText });
                    break;
                case AgentStepKind.ModelReply:
                    entries.Add(AgentEntry(item.PlainText));
                    break;
                case AgentStepKind.ConfirmationShown when item.RawJson is not null:
                    entries.Add(new ChatEntry { Kind = EntryKind.Confirmation, Card = ConfirmationCard.FromJson(item.RawJson), CardState = CardState.Pending });
                    break;
            }
        }

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
        if (busy || aiError is not null || entry.Card is not { } card || entry.CardState != CardState.Pending || sessionKey is not { } key)
        {
            return;
        }

        entry.CardState = CardState.Submitting;
        busy = true;
        await RecordAsync(key, approve ? ProcessMetricActions.AgentConfirm : ProcessMetricActions.AgentReject);
        var result = await RunTurnAsync(null, ct => Runner.ConfirmAsync(key, card.ConfirmationId, approve, OnEventAsync, OnTextDeltaAsync, ct));

        entry.RequestNo = result?.CreatedRequestNo;
        entry.CardState = !approve ? CardState.Canceled
            : entry.RequestNo is not null ? CardState.Created
            : CardState.NotCreated;

        // 這一輪沒有結果（例外已另行通知）：卡片寫「請看下方的說明」，對話裡要有那則說明。
        if (approve && result is null && !disposed)
        {
            entries.Add(SystemEntry(NotSubmittedExplanation));
        }
    }

    private Task OnPhotosSelectedAsync(InputFileChangeEventArgs args)
        => ShopPhotoPicker.AddAsync(args, pendingPhotos, NotificationService, Logger);

    private void RemovePhoto(Guid id) => pendingPhotos.RemoveAll(x => x.Id == id);

    /// <summary>把選好的照片放進暫存區，再請 Agent 續跑（申請送出時才隨申請寫入）。</summary>
    private async Task OnSendPhotosAsync()
    {
        if (busy || aiError is not null || pendingPhotos.Count == 0 || sessionKey is not { } key)
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

        try
        {
            sessionKey = await Runner.StartNewAsync();
            entries.Clear();
            traceEvents = [];
            pendingPhotos.Clear();
            awaitingPhotos = false;
            draft = string.Empty;
            Logger.LogInformation("Shop assistant started a new conversation. SessionKey={SessionKey}", sessionKey);
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

    /// <summary>執行一輪：先放顧客泡泡與串流預覽，完成後依結果重繪。失敗回 null（已通知）。</summary>
    private async Task<AgentTurnResult?> RunTurnAsync(string? userText, Func<CancellationToken, Task<AgentTurnResult>> run)
    {
        busy = true;
        awaitingPhotos = false;
        turnReplySource = null;
        streaming = new StringBuilder();
        lastDeltaRender = TimeProvider.GetTimestamp();
        if (userText is not null)
        {
            entries.Add(new ChatEntry { Kind = EntryKind.User, Text = userText });
        }

        try
        {
            var result = await run(runCancellation.Token);
            if (!disposed)
            {
                ApplyResult(result);
            }

            return result;
        }
        catch (Exception ex) when (disposed)
        {
            // 已離開頁面：沒有畫面可以通知。
            Logger.LogDebug(ex, "Shop assistant turn ended after the view was disposed.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // Runner 對「會話不存在／已結束」等狀況拋出的訊息是給顧客看的中文。
            Logger.LogWarning(ex, "Shop assistant turn was rejected by the agent runner.");
            ViewNotification.Caution(NotificationService, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Shop assistant turn failed.");
            ViewNotification.UnexpectedError(NotificationService, "線上客服處理失敗，請稍後再試。", ex);
            return null;
        }
        finally
        {
            busy = false;
            streaming = null;
        }
    }

    /// <summary>串流的文字只是預覽：以最終的 <see cref="AgentTurnResult.ReplyText"/> 重繪。</summary>
    private void ApplyResult(AgentTurnResult result)
    {
        var programReply = turnReplySource != AgentDataSource.Conversation;
        if (!string.IsNullOrWhiteSpace(result.ReplyText))
        {
            entries.Add(programReply ? SystemEntry(result.ReplyText) : AgentEntry(result.ReplyText));
        }

        // 模型有回覆、程式另有停止說明（步數上限、規則衝突…）時補一則系統訊息；程式回覆本身就是說明，不重複。
        if (!programReply && result.StopExplanation is { } explanation && explanation != result.ReplyText)
        {
            entries.Add(SystemEntry(explanation));
        }

        if (result.PolicyCards.Count > 0)
        {
            entries.Add(new ChatEntry { Kind = EntryKind.PolicyCards, PolicyCards = result.PolicyCards });
        }

        if (result.Confirmation is { } card)
        {
            entries.Add(new ChatEntry { Kind = EntryKind.Confirmation, Card = card, CardState = CardState.Pending });
        }

        awaitingPhotos = result.StopReason == AgentStopReason.AwaitingPhotoUpload;
    }

    /// <summary>Runner 的事件可能來自其他執行緒：一律回到渲染器的同步內容再改狀態。</summary>
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

        StateHasChanged();
    });

    private Task OnTextDeltaAsync(string text) => InvokeAsync(() =>
    {
        if (disposed || streaming is null)
        {
            return;
        }

        streaming.Append(text);
        var now = TimeProvider.GetTimestamp();
        if (TimeProvider.GetElapsedTime(lastDeltaRender, now) >= DeltaRenderInterval)
        {
            lastDeltaRender = now;
            StateHasChanged();
        }
    });

    private Task RecordAsync(Guid key, string action)
        => ProcessMetricService.RecordAsync(key, AfterSalesChannel.Agent, ProcessActor.Customer, action, CustomerId);

    private static ChatEntry AgentEntry(string text)
        => new() { Kind = EntryKind.Agent, Text = text, Html = AiMarkdownRenderer.ToSafeHtml(text) };

    private static ChatEntry SystemEntry(string text) => new() { Kind = EntryKind.System, Text = text };
}
