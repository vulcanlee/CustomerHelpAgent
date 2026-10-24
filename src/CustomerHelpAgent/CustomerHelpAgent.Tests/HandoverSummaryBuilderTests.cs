using System.Text.Encodings.Web;
using System.Text.Json;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.StaffAssistant;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 轉人工交接摘要（M4 Task 8，純程式、不呼叫模型）：從顧客端 Agent 的執行軌跡整理出查過的訂單、規則與版本、庫存、
/// 顧客說過的話、確認卡與照片；個資一律遮蔽。軌跡步驟以正式的序列化格式（工具信封、確認卡 JSON）寫入。
/// </summary>
public sealed class HandoverSummaryBuilderTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private StaffAssistantKit kit = null!;

    public async Task InitializeAsync() => kit = await StaffAssistantKit.CreateAsync();

    public async Task DisposeAsync() => await kit.DisposeAsync();

    private static ConfirmationCard Card(params string[] facts) => new(
        7, "approval-1", "確認換尺碼申請", "跑鞋（訂單 A0001）", "黑色 / 26", "黑色 / 27", "換尺碼",
        "依據：鞋類換尺碼（第 1 版，售後規則）", facts, 2);

    [Fact]
    public async Task Handover_ListsWhatCustomerAgentLookedUp()
    {
        var requestId = await kit.Host.AddRequestAsync();
        var detail = await kit.DetailAsync(requestId);
        var orders = ToolOutcome.Ok(new[]
        {
            new AgentOrderItem(11, "A0001", "跑鞋", "黑", "26", "已簽收"),
            new AgentOrderItem(12, "A0001", "襪子", "白", "M", "已簽收"),
        }, ["收件人", "地址", "手機", "價格"]);
        var policy = ToolOutcome.Ok(new AgentPolicyCard(
            1, "EX-SIZE", "鞋類換尺碼", "簽收 7 天內、未穿著可換尺碼。", "售後規則", 1, PreReviewTestHost.LocalNow.AddDays(-100), 2, [], []));
        var stock = ToolOutcome.Ok(new[]
        {
            new AgentStockOption("黑", "27", true),
            new AgentStockOption("黑", "28", false),
        }, ["庫存數量"]);

        await kit.SeedTrailAsync(detail.CaseKey, kit.Host.Alice,
        [
            (AgentStepKind.UserMessage, "我的黑色跑鞋太小，想換大一號", null),
            .. StaffAssistantKit.ToolCall(AfterSalesAgentTools.QueryMyOrdersName, orders),
            .. StaffAssistantKit.ToolCall(AfterSalesAgentTools.QueryMyOrdersName, orders),
            (AgentStepKind.UserMessage, "沒穿過，只試穿一下", null),
            .. StaffAssistantKit.ToolCall(AfterSalesAgentTools.GetEffectivePolicyName, policy),
            .. StaffAssistantKit.ToolCall(AfterSalesAgentTools.CheckVariantStockName, stock),
            (AgentStepKind.UserMessage, string.Format(AfterSalesAgentRunner.PhotosUploadedMessageFormat, 2), null),
            (AgentStepKind.ConfirmationShown, "顯示確認卡", Card("簽收第 2 天（程式計算）", "你表示商品未穿過").ToJson()),
            (AgentStepKind.ConfirmationDecided, "你按下「確認送出」", JsonSerializer.Serialize(new { confirmationId = 7, approved = true })),
        ]);

        var summary = await kit.Builder.BuildAsync(detail);

        Assert.True(summary.HasTrail);
        Assert.Equal(2, summary.OrderLookups);
        Assert.Equal(2, summary.OrderItemsFound);
        Assert.Equal(["鞋類換尺碼（第 1 版）"], summary.Policies);
        Assert.Equal(["有貨：黑 / 27；無貨：黑 / 28"], summary.StockChecks);
        // 照片的代送訊息不算顧客說的話。
        Assert.Equal(["我的黑色跑鞋太小，想換大一號", "沒穿過，只試穿一下"], summary.CustomerStatements);
        Assert.Equal(2, summary.PhotosUploaded);
        var card = Assert.IsType<HandoverConfirmation>(summary.Confirmation);
        Assert.Equal("黑色 / 26 → 黑色 / 27（換尺碼）", card.Spec);
        Assert.Equal("依據：鞋類換尺碼（第 1 版，售後規則）", card.PolicyLine);
        Assert.Contains("你表示商品未穿過", card.Facts);
        Assert.Equal(2, card.PhotoCount);
        Assert.Equal("顧客按下確認送出", card.Outcome);
    }

    [Fact]
    public async Task Handover_NoTrail_SaysSo()
    {
        var requestId = await kit.Host.AddRequestAsync();

        var summary = await kit.Builder.BuildAsync(await kit.DetailAsync(requestId));

        Assert.False(summary.HasTrail);
        Assert.Null(summary.Confirmation);
        Assert.Empty(summary.Policies);
    }

    [Fact]
    public async Task Handover_NoPersonalData()
    {
        var requestId = await kit.Host.AddRequestAsync();
        var detail = await kit.DetailAsync(requestId);
        await kit.SeedTrailAsync(detail.CaseKey, kit.Host.Alice,
        [
            (AgentStepKind.UserMessage, $"我是 Alice（帳號 alice），寄到台北市某路1號給王小明，電話 0912345678，信箱 {StaffAssistantKit.AliceEmail}", null),
            (AgentStepKind.ConfirmationShown, "顯示確認卡", Card("問題描述：王小明說鞋子太小，請打 0912345678").ToJson()),
            (AgentStepKind.ModelReply, "好的 Alice，我會寄到台北市某路1號。", null),
        ]);

        var (summary, steps) = await kit.Builder.BuildWithTrailAsync(detail);
        var text = JsonSerializer.Serialize(summary, ReadableJson) + JsonSerializer.Serialize(steps, ReadableJson);

        foreach (var personal in StaffAssistantKit.PersonalValues)
        {
            Assert.DoesNotContain(personal, text, StringComparison.Ordinal);
        }

        Assert.Contains("此顧客", summary.CustomerStatements.Single(), StringComparison.Ordinal);
        Assert.Contains("［已遮蔽］", summary.Confirmation!.Facts.Single(), StringComparison.Ordinal);
        Assert.Contains(steps, x => x.Kind == "Agent 回覆" && x.Text.Contains("此顧客", StringComparison.Ordinal));
    }

    /// <summary>
    /// 記憶確認卡（M5）不是申請：交接摘要的確認卡只取售後申請卡；記憶卡之後的「顧客決定」也不可蓋掉申請卡的結果。
    /// </summary>
    [Fact]
    public async Task Handover_SkipsMemoryCard()
    {
        var requestId = await kit.Host.AddRequestAsync();
        var detail = await kit.DetailAsync(requestId);
        var memoryCard = new ConfirmationCard(
            8, "approval-2", "記住您的偏好", string.Empty, string.Empty, null, "鞋碼", string.Empty,
            ["要我記住：鞋子平常穿 41 碼嗎？之後的對話會先向您確認。"], 0, ConfirmationCardKind.Memory, MemoryKind.ShoeSize, "41");
        await kit.SeedTrailAsync(detail.CaseKey, kit.Host.Alice,
        [
            (AgentStepKind.ConfirmationShown, "顯示確認卡", Card("簽收第 2 天（程式計算）").ToJson()),
            (AgentStepKind.ConfirmationDecided, "你按下「確認送出」", JsonSerializer.Serialize(new { confirmationId = 7, approved = true })),
            (AgentStepKind.ConfirmationShown, "顯示記憶確認卡", memoryCard.ToJson()),
            (AgentStepKind.ConfirmationDecided, "你按下「不用記住」", JsonSerializer.Serialize(new { confirmationId = 8, approved = false })),
        ]);

        var summary = await kit.Builder.BuildAsync(detail);

        var card = Assert.IsType<HandoverConfirmation>(summary.Confirmation);
        Assert.Equal("確認換尺碼申請", card.Title);
        Assert.Equal("顧客按下確認送出", card.Outcome);

        // 只有記憶卡的案件：沒有申請確認卡。
        var onlyMemoryId = await kit.Host.AddRequestAsync();
        var onlyMemory = await kit.DetailAsync(onlyMemoryId);
        await kit.SeedTrailAsync(onlyMemory.CaseKey, kit.Host.Alice,
        [
            (AgentStepKind.ConfirmationShown, "顯示記憶確認卡", memoryCard.ToJson()),
            (AgentStepKind.ConfirmationDecided, "你按下「記住」", JsonSerializer.Serialize(new { confirmationId = 8, approved = true })),
        ]);
        Assert.Null((await kit.Builder.BuildAsync(onlyMemory)).Confirmation);
    }
}
