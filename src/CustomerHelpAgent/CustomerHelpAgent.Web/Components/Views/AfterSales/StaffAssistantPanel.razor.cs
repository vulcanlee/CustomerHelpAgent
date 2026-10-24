using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Web.Agents.StaffAssistant;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 審核窗的「AI 助手」分頁（M4，只給 Agent 管道）：
/// <list type="bullet">
/// <item>一打開就顯示 <see cref="HandoverSummaryBuilder"/> 整理的「轉人工交接：顧客端 Agent 已查過」（純程式，不呼叫模型）。</item>
/// <item>只讀的客服 AI 助手（<see cref="StaffAssistantRunner"/>）：工具呼叫以小標籤顯示在回覆上方（「查了：規則 v1」），
/// 串流時先以純文字預覽，完成後經 <see cref="AiMarkdownRenderer.ToSafeHtml"/> 重繪；客服的提問只以純文字顯示。</item>
/// <item>回放模式或 AI 未設定時停用並說明（交接摘要照常顯示）。</item>
/// <item>會話只在本元件的記憶體：審核窗關閉（元件 Dispose）即取消在途的回覆並丟棄整段對話。</item>
/// </list>
/// </summary>
public partial class StaffAssistantPanel : IDisposable
{
    private const int MaxQuestionLength = 500;

    /// <summary>串流增量最多每隔這麼久重繪一次，避免逐字觸發渲染。</summary>
    private static readonly TimeSpan DeltaRenderInterval = TimeSpan.FromMilliseconds(80);

    [Inject]
    public StaffAssistantRunner Runner { get; set; } = default!;

    [Inject]
    public HandoverSummaryBuilder HandoverSummaryBuilder { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<StaffAssistantPanel> Logger { get; set; } = default!;

    /// <summary>目前開啟的申請；助手綁定它（審核頁以 <c>@key</c> 保證換一筆就是新的元件）。</summary>
    [Parameter, EditorRequired]
    public AfterSalesRequestView Request { get; set; } = default!;

    private readonly CancellationTokenSource cancellation = new();
    private readonly List<ChatEntry> entries = [];
    private StaffAssistantSession? session;
    private HandoverSummary? handover;
    private bool handoverFailed;
    private string? disabledReason;
    private string draft = string.Empty;
    private bool busy;
    private bool disposed;

    /// <summary>串流中的預覽文字；null 表示目前沒有在跑的回合。</summary>
    private StringBuilder? streaming;
    private List<string> streamingTags = [];
    private long lastDeltaRender;

    private sealed class ChatEntry
    {
        public required bool IsQuestion { get; init; }

        public required string Text { get; init; }

        /// <summary>模型回覆的安全 HTML（<see cref="AiMarkdownRenderer.ToSafeHtml"/>），建立時算一次。</summary>
        public string Html { get; init; } = string.Empty;

        public bool IsProgramMessage { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = [];
    }

    private bool CanAsk => session is not null && disabledReason is null && !busy;

    private string Placeholder
        => disabledReason is not null ? "客服 AI 助手目前無法使用"
        : busy ? "助手查詢中，請稍候…"
        : "例如：此顧客之前說過有沒有穿過？";

    private string OrdersText => handover is null || handover.OrderLookups == 0
        ? "沒有查詢訂單"
        : $"查詢 {handover.OrderLookups} 次，查到 {handover.OrderItemsFound} 筆訂單商品";

    private static string JoinOr(IReadOnlyList<string> items, string empty) => items.Count == 0 ? empty : string.Join("；", items);

    protected override async Task OnInitializedAsync()
    {
        session = Runner.StartSession(Request.Id);
        disabledReason = Runner.UnavailableReason;
        if (disabledReason is not null)
        {
            Logger.LogInformation("Staff assistant opened while unavailable. RequestId={RequestId}", Request.Id);
        }

        try
        {
            handover = await HandoverSummaryBuilder.BuildAsync(Request);
        }
        catch (Exception ex)
        {
            handoverFailed = true;
            Logger.LogError(ex, "Failed to build the staff handover summary. RequestId={RequestId}", Request.Id);
        }
    }

    private async Task OnSendAsync()
    {
        var question = draft.Trim();
        if (!CanAsk || question.Length == 0 || session is not { } current)
        {
            return;
        }

        draft = string.Empty;
        busy = true;
        entries.Add(new ChatEntry { IsQuestion = true, Text = question });
        streaming = new StringBuilder();
        streamingTags = [];
        lastDeltaRender = TimeProvider.GetTimestamp();
        try
        {
            var reply = await Runner.AskAsync(current, question, OnTextDeltaAsync, OnToolCallAsync, cancellation.Token);
            if (disposed)
            {
                return;
            }

            entries.Add(new ChatEntry
            {
                IsQuestion = false,
                Text = reply.Text,
                Html = reply.IsProgramMessage ? string.Empty : AiMarkdownRenderer.ToSafeHtml(reply.Text),
                IsProgramMessage = reply.IsProgramMessage,
                Tags = reply.ToolTags,
            });
        }
        catch (Exception ex) when (disposed)
        {
            // 審核窗已關閉：沒有畫面可以通知。
            Logger.LogDebug(ex, "Staff assistant turn ended after the panel was disposed.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception during staff assistant turn. RequestId={RequestId}", Request.Id);
            ViewNotification.UnexpectedError(NotificationService, "客服 AI 助手發生未預期的錯誤，請稍後再試。", ex);
        }
        finally
        {
            streaming = null;
            streamingTags = [];
            busy = false;
        }
    }

    /// <summary>Runner 的回呼可能來自其他執行緒：一律回到渲染器的同步內容再改狀態。</summary>
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

    private Task OnToolCallAsync(string tag) => InvokeAsync(() =>
    {
        if (disposed || streaming is null)
        {
            return;
        }

        streamingTags = [.. streamingTags, tag];
        StateHasChanged();
    });

    public void Dispose()
    {
        disposed = true;
        // 關閉審核窗：中止在途的回覆，整段對話隨元件丟棄。
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
