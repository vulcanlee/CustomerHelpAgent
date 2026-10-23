using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 列表頁「刪除選取」的共用流程（0.9.132 起）：逐筆呼叫單筆刪除、部分失敗照常繼續、
/// 樹狀資料由深到淺、確認與結果文字的截斷。
/// </summary>
public sealed class BatchDeleteTests
{
    private sealed record Row(int Id, string Name, int Depth = 0);

    [Fact]
    public async Task Run_ShouldDeleteInTheGivenOrder_AndCountMixedResults()
    {
        var rows = new[] { new Row(1, "甲"), new Row(2, "乙"), new Row(3, "丙") };
        var called = new List<int>();

        var outcome = await BatchDelete.RunAsync(rows, x => x.Id.ToString(), x => x.Name, x =>
        {
            called.Add(x.Id);
            return Task.FromResult(x.Id == 2
                ? VerifyRecordResultFactory.Build(false, "還有子團隊")
                : VerifyRecordResultFactory.Build(true));
        }, NullLogger.Instance);

        Assert.Equal([1, 2, 3], called);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal([new BatchDeleteFailure("乙", "還有子團隊")], outcome.Failures);
        Assert.Null(outcome.FirstException);
    }

    [Fact]
    public async Task Run_WhenOneItemThrows_ShouldCountItAsFailedAndContinue()
    {
        var rows = new[] { new Row(1, "甲"), new Row(2, "乙"), new Row(3, "丙") };
        var boom = new InvalidOperationException("boom");

        var outcome = await BatchDelete.RunAsync(rows, x => x.Id.ToString(), x => x.Name,
            x => x.Id == 1 ? throw boom : Task.FromResult(VerifyRecordResultFactory.Build(true)),
            NullLogger.Instance);

        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal([new BatchDeleteFailure("甲", BatchDelete.UnexpectedReason)], outcome.Failures);
        Assert.Same(boom, outcome.FirstException);
    }

    [Fact]
    public void DeepestFirst_ShouldPutChildrenBeforeParents_AndKeepOrderWithinADepth()
    {
        var rows = new[] { new Row(1, "總部", 0), new Row(2, "業務部", 1), new Row(3, "北區", 2), new Row(4, "研發部", 1) };

        var ordered = BatchDelete.DeepestFirst(rows, x => x.Depth);

        Assert.Equal([3, 2, 4, 1], ordered.Select(x => x.Id));
    }

    [Fact]
    public void ConfirmContent_ShouldListNamesUpToTheLimit_ThenTheTotal()
    {
        var labels = Enumerable.Range(1, BatchDelete.MaxListed + 2).Select(i => $"項目{i}").ToList();

        var content = BatchDelete.BuildConfirmContent(labels, restorable: true, note: null);

        Assert.StartsWith($"確定要刪除以下 {labels.Count} 筆紀錄嗎？", content);
        Assert.Contains($"・項目{BatchDelete.MaxListed}", content);
        Assert.DoesNotContain($"・項目{BatchDelete.MaxListed + 1}", content);
        Assert.Contains($"…等共 {labels.Count} 筆", content);
        Assert.EndsWith("刪除後可在工具列的「顯示已刪除」中還原。", content);
    }

    [Fact]
    public void ConfirmContent_ForPermanentDelete_ShouldWarnAndIncludeTheNote()
    {
        var content = BatchDelete.BuildConfirmContent(["a.log"], restorable: false, note: "原始明細檔也會一併移除。");

        Assert.Contains("原始明細檔也會一併移除。", content);
        Assert.DoesNotContain("等共", content);
        Assert.EndsWith("此操作無法復原。", content);
    }

    [Fact]
    public void ResultText_AllSucceeded_ShouldOnlyGiveTheCount()
        => Assert.Equal("已刪除 3 筆", BatchDelete.BuildResultText(new BatchDeleteOutcome(3, [], null)));

    [Fact]
    public void ResultText_WithFailures_ShouldListReasonsUpToTheLimit()
    {
        var failures = Enumerable.Range(1, BatchDelete.MaxListed + 3)
            .Select(i => new BatchDeleteFailure($"角色{i}", "仍是使用者的主要角色"))
            .ToList();

        var text = BatchDelete.BuildResultText(new BatchDeleteOutcome(2, failures, null));

        Assert.StartsWith($"已刪除 2 筆，{failures.Count} 筆未刪除：", text);
        Assert.Contains("・角色1：仍是使用者的主要角色", text);
        Assert.DoesNotContain($"・角色{BatchDelete.MaxListed + 1}：", text);
        Assert.EndsWith("另有 3 筆未列出", text);
    }
}
