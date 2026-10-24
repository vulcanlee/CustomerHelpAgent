using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.StaffAssistant;
using CustomerHelpAgent.Web.Components.Views.AfterSales;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 審核窗的「AI 助手」分頁（M4 Task 8）：只有 Agent 管道才有；一打開就顯示程式整理的交接摘要；
/// 回放模式與 AI 未設定時停用；提問後工具標籤顯示在回覆上方，回覆經安全管線渲染；不寫顧客端執行軌跡。
/// </summary>
public sealed class StaffAssistantPanelTests : PreReviewViewTestBase
{
    private async Task<AfterSalesRequestView> AgentRequestAsync()
    {
        var id = await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        return await Services.GetRequiredService<AfterSalesRequestService>().GetReviewDetailAsync(id)
            ?? throw new InvalidOperationException("request missing");
    }

    [Fact]
    public async Task Panel_ReplayMode_Disabled()
    {
        var request = await AgentRequestAsync();
        await Demo.SetModeAsync(DemoMode.Replay, recordingId: 1);

        var cut = Render<StaffAssistantPanel>(p => p.Add(x => x.Request, request));

        cut.WaitForAssertion(() => Assert.Contains("轉人工交接：顧客端 Agent 已查過", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("回放模式不提供客服 AI 助手（需即時模型）", cut.Find(".staff-assistant-notice").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find(".staff-assistant-input").HasAttribute("disabled"));
        Assert.True(cut.Find(".staff-assistant-send").HasAttribute("disabled"));
        Assert.Equal(0, Model.CallCount);
    }

    [Fact]
    public async Task Panel_AiNotConfigured_Disabled()
    {
        var request = await AgentRequestAsync();
        AiSettings.ApiKey = string.Empty;

        var cut = Render<StaffAssistantPanel>(p => p.Add(x => x.Request, request));

        cut.WaitForAssertion(() => Assert.Contains("AI 服務尚未設定", cut.Find(".staff-assistant-notice").TextContent, StringComparison.Ordinal));
        Assert.True(cut.Find(".staff-assistant-input").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Panel_TraditionalChannel_NoAssistantTab()
    {
        await AddRequestAsync(Black40ItemId, AfterSalesChannel.Traditional);
        await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        var cut = Render<AfterSalesReviewView>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.after-sales-review-row").Count));

        cut.FindAll("tr.after-sales-review-row").Single(x => x.TextContent.Contains("傳統表單", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("申請內容", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("AI 助手", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".staff-assistant"));

        // 對照：Agent 管道的申請有「AI 助手」分頁，點開才建立助手（交接摘要隨即顯示）。
        await cut.InvokeAsync(() => cut.Find(".ant-modal-close").Click());
        cut.WaitForAssertion(() => Assert.DoesNotContain("申請內容", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("tr.after-sales-review-row").Single(x => x.TextContent.Contains("AI 助理", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains("AI 助手", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".staff-assistant"));

        cut.FindAll(".ant-tabs-tab").Single(x => x.TextContent.Contains("AI 助手", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("轉人工交接：顧客端 Agent 已查過", cut.Find(".staff-assistant").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Panel_Ask_ShowsToolTagsAboveReply()
    {
        var request = await AgentRequestAsync();
        // 直接渲染面板（不經審核頁的登入檢查）：登入者＝有售後審核權限的客服（基底授予的使用者）。
        Services.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = CustomerId, Account = "customer1" };
        Model.Call(StaffAssistantTools.GetCaseName, []);
        Model.Reply("依申請內容：此顧客要把**白 41** 換成 42。");

        var cut = Render<StaffAssistantPanel>(p => p.Add(x => x.Request, request));
        WaitUntilReady(cut);
        await AskAsync(cut, "這筆要換成什麼？");

        cut.WaitForAssertion(() => Assert.Contains("換成 42", cut.Find(".staff-assistant-reply").TextContent, StringComparison.Ordinal));
        Assert.Equal(["查了：申請內容"], cut.FindAll(".staff-assistant-tag").Select(x => x.TextContent.Trim()));
        // 模型文字經安全管線渲染（Markdown 粗體）；客服的提問以純文字顯示。
        Assert.NotEmpty(cut.Find(".staff-assistant-reply").QuerySelectorAll("strong"));
        Assert.Contains("這筆要換成什麼？", cut.Find(".staff-assistant-question").TextContent, StringComparison.Ordinal);

        // 用量記在「客服 AI 助手」；顧客端的執行軌跡不會多出任何一步。
        Assert.All(Usage.Entries, x => Assert.Equal(TokenUsageOperations.AfterSalesStaffAssistant, x.Operation));
        Assert.NotEmpty(Usage.Entries);
        using var context = Factory.CreateDbContext();
        Assert.Equal(0, await context.AgentRunTrace.CountAsync());
    }

    [Fact]
    public async Task Panel_FollowUpQuestion_KeepsConversationInMemory()
    {
        var request = await AgentRequestAsync();
        Services.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = CustomerId, Account = "customer1" };
        Model.Reply("第一個回答");
        Model.Reply("第二個回答");
        var cut = Render<StaffAssistantPanel>(p => p.Add(x => x.Request, request));
        WaitUntilReady(cut);

        await AskAsync(cut, "第一個問題");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".staff-assistant-reply")));
        await AskAsync(cut, "第二個問題");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".staff-assistant-reply").Count));
        // 第二輪送給模型的內容帶著第一輪的問答（同一段會話，存在面板的記憶體裡）；用量記在開啟助手的客服身上。
        var second = string.Join(" ", Model.Requests[1].Select(x => x.Text));
        Assert.Contains("第一個問題", second, StringComparison.Ordinal);
        Assert.Contains("第一個回答", second, StringComparison.Ordinal);
        Assert.Equal(2, Usage.Entries.Count);
        Assert.All(Usage.Entries, x => Assert.Equal(CustomerId, x.UserId));
    }

    [Fact]
    public async Task Panel_ReplaySwitchedOnAfterOpen_RefusesWithoutCallingModel()
    {
        var request = await AgentRequestAsync();
        Services.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = CustomerId, Account = "customer1" };
        var cut = Render<StaffAssistantPanel>(p => p.Add(x => x.Request, request));
        WaitUntilReady(cut);

        // 開著分頁時展示者切到回放：下一次提問由程式回固定說明，不呼叫模型。
        await Demo.SetModeAsync(DemoMode.Replay, recordingId: 1);
        await AskAsync(cut, "規則是哪一版？");

        cut.WaitForAssertion(() => Assert.Contains(
            StaffAssistantRunner.ReplayDisabledText, cut.Find(".staff-assistant-system").TextContent, StringComparison.Ordinal));
        Assert.Equal(0, Model.CallCount);
        Assert.Empty(Usage.Entries);
    }

    [Fact]
    public async Task Panel_SwitchTabs_KeepsConversation()
    {
        await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        Model.Reply("第一個回答");
        var cut = Render<AfterSalesReviewView>();
        cut.WaitForElement("tr.after-sales-review-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("AI 助手", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => Tab(cut, "AI 助手").Click());
        WaitUntilReady(cut);
        await AskAsync(cut, "第一個問題");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".staff-assistant-reply")));

        await cut.InvokeAsync(() => Tab(cut, "審核").Click());
        cut.WaitForAssertion(() => Assert.Contains("申請內容", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => Tab(cut, "AI 助手").Click());

        // 切回來對話還在（同一個助手元件，沒有重建）。
        cut.WaitForAssertion(() => Assert.Contains("第一個回答", cut.Find(".staff-assistant-reply").TextContent, StringComparison.Ordinal));
        Assert.Contains("第一個問題", cut.Find(".staff-assistant-question").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".staff-assistant"));
    }

    [Fact]
    public async Task Panel_CloseModal_CancelsInFlightTurn()
    {
        await AddRequestAsync(White41ItemId, AfterSalesChannel.Agent);
        var blocking = new BlockingChatClient();
        ModelOverride = blocking;
        var cut = Render<AfterSalesReviewView>();
        cut.WaitForElement("tr.after-sales-review-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("AI 助手", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => Tab(cut, "AI 助手").Click());
        WaitUntilReady(cut);
        await AskAsync(cut, "這筆可以換嗎？");
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cut.InvokeAsync(() => cut.Find(".ant-modal-close").Click());

        // 關窗：面板卸載，在途的模型呼叫被取消，對話隨之丟棄。
        await blocking.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".staff-assistant")));
    }

    /// <summary>助手可以提問、交接摘要也載入完成（之後不會再因載入而重繪）。</summary>
    private static void WaitUntilReady<T>(IRenderedComponent<T> cut)
        where T : Microsoft.AspNetCore.Components.IComponent
        => cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find(".staff-assistant-input").HasAttribute("disabled"));
            Assert.DoesNotContain("載入中", cut.Find(".staff-assistant-handover").TextContent, StringComparison.Ordinal);
        });

    /// <summary>輸入並送出；找元素與觸發事件放在同一次 InvokeAsync，避免中間重繪讓事件處理器失效。</summary>
    private static async Task AskAsync<T>(IRenderedComponent<T> cut, string question)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        await cut.InvokeAsync(() => cut.Find(".staff-assistant-input").Input(question));
        await cut.InvokeAsync(() => cut.Find(".staff-assistant-composer").Submit());
    }

    private static AngleSharp.Dom.IElement Tab(IRenderedComponent<AfterSalesReviewView> cut, string text)
        => cut.FindAll(".ant-tabs-tab").Single(x => x.TextContent.Contains(text, StringComparison.Ordinal));

    /// <summary>一直等到被取消的模型：記下開始等待與被取消的時間點。</summary>
    private sealed class BlockingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
